using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Stats;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Engine.Output;
using VideoWallRW.App.ViewModels;
using VideoWallRW.Core.Model;
using VideoWallRW.Core.Persistence;

namespace VideoWallRW.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly GraphicsDevice _gfx;
    private readonly RenderThread _render;
    private bool _masterPaused;
    private string? _currentProjectPath;
    private bool _isDirty;
    private OutputSetup _outputSetup = new();
    private InstallationConfig _installation = new();
    private readonly DispatcherTimer _autosaveTimer;
    private readonly DispatcherTimer _runtimeStateTimer;
    private readonly WatchFolderManager _watchFolders;

    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];
    public ObservableCollection<SceneViewModel> Scenes { get; } = [];

    [ObservableProperty] private string _statusText = "Pronto — marque um monitor para ativar a saída.";
    [ObservableProperty] private double _masterFader = 1.0;
    [ObservableProperty] private bool _blackout;
    [ObservableProperty] private string _windowTitle = "VideoWallRW — Novo projeto";
    [ObservableProperty] private bool _sequencerRunning;

    public CompositionViewModel Grid { get; }
    public FileBrowserViewModel FileBrowser { get; }

    private readonly bool _kiosk;

    public MainViewModel(GraphicsDevice gfx, RenderThread render, bool kiosk = false)
    {
        _gfx = gfx;
        _render = render;
        _kiosk = kiosk;

        var compModel = new Composition { ColumnCount = 8 };
        compModel.AddLayer(new Layer { Name = "Layer 3" });
        compModel.AddLayer(new Layer { Name = "Layer 2" });
        compModel.AddLayer(new Layer { Name = "Layer 1" }); // Base

        Grid = new CompositionViewModel(compModel, _render);
        Grid.SyncToEngine();
        Grid.Changed += MarkDirty;
        _watchFolders = new WatchFolderManager(Grid, Dispatcher.CurrentDispatcher);
        FileBrowser = new FileBrowserViewModel(SendFileToSelectedCell);

        _render.StatsUpdated += OnStatsUpdated;
        _render.EngineFailed += OnEngineFailed;
        RefreshMonitors();

        // T7.3: autosave a cada 120s.
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        _autosaveTimer.Tick += (_, _) => Autosave();
        _autosaveTimer.Start();

        // T8.1: estado de runtime a cada 5s, para o modo kiosk retomar sozinho após reinício.
        _runtimeStateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _runtimeStateTimer.Tick += (_, _) => SaveRuntimeState();
        _runtimeStateTimer.Start();

        if (!_kiosk)
            CheckCrashRecovery();
    }

    /// <summary>Usado pelo modo <c>--kiosk</c>: carrega sem os diálogos interativos
    /// (confirmação de descarte, mídia ausente) — não há operador presente.</summary>
    public void LoadProjectSilently(string path)
    {
        var project = ProjectSerializer.Load(path);
        ApplyProject(project);
        _currentProjectPath = path;
        _isDirty = false;
        UpdateWindowTitle();
    }

    private void SaveRuntimeState()
    {
        try
        {
            var (columns, _) = Grid.CaptureSceneState();
            RuntimeStateService.Save(new RuntimeState
            {
                ProjectPath = _currentProjectPath,
                ActiveColumns = columns,
                MasterFader = MasterFader,
                SequencerRunning = SequencerRunning,
            });
        }
        catch { /* melhor esforço — não deve derrubar a UI */ }
    }

    /// <summary>Aplica o RuntimeState salvo (Fase 8 T8.1): usado pelo modo <c>--kiosk</c>
    /// para restaurar exatamente o que estava no ar antes de reiniciar.</summary>
    public void ApplyRuntimeState(RuntimeState state)
    {
        foreach (var (layerId, column) in state.ActiveColumns)
        {
            int idx = Grid.Layers.ToList().FindIndex(l => l.Model.Id == layerId);
            if (idx < 0) continue;
            if (column < 0) _render.Post(new ClearLayer(idx));
            else _render.Post(new TriggerClip(idx, column));
        }
        MasterFader = state.MasterFader;
        if (state.SequencerRunning)
            ToggleSequencer();
    }

    [RelayCommand]
    private void RefreshMonitors()
    {
        var previouslyActive = Monitors.Where(m => m.IsActive)
            .Select(m => m.Info.DeviceName).ToHashSet();
        Monitors.Clear();
        foreach (var info in MonitorEnumerator.Enumerate(_gfx))
            Monitors.Add(new MonitorViewModel(info, _render)
            {
                IsActive = previouslyActive.Contains(info.DeviceName),
            });
    }

    [RelayCommand]
    private void OpenOutputSetup()
    {
        var vm = new OutputSetupViewModel(_render, MonitorEnumerator.Enumerate(_gfx),
            () => Grid.Model.Width, () => Grid.Model.Height);
        vm.LoadFromModel(_outputSetup);
        var window = new OutputSetupWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        _outputSetup = vm.ToModel();
        MarkDirty();
    }

    [RelayCommand]
    private void OpenAgenda()
    {
        var vm = new ViewModels.AgendaViewModel(GetProjectFolder, OpenImageEditorForFile, OpenPdfImporter, SendImageToCell);
        var window = new AgendaWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    private string GetProjectFolder() => _currentProjectPath is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VideoWallRW")
        : Path.GetDirectoryName(_currentProjectPath)!;

    /// <summary>Fase 10 T10.6: "editar imagem" abre o editor; ao salvar, envia direto
    /// pra célula selecionada (ou pergunta layer/coluna se nada estiver selecionado).</summary>
    private void OpenImageEditorForFile(string path, string suggestedName)
    {
        double? compAspect = Grid.Model.Height > 0 ? (double)Grid.Model.Width / Grid.Model.Height : null;
        var vm = new ImageEditorViewModel(path, (editedPath, name) => SendImageToCell(editedPath, name), compAspect);
        var window = new ImageEditorWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    private void OpenPdfImporter(string pdfPath)
    {
        var vm = new PdfImportViewModel(pdfPath, (imagePath, name) => OpenImageEditorForFile(imagePath, name));
        var window = new PdfImportWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>Painel Files (duplo-clique): arquivo vai pra célula selecionada; sem
    /// seleção, primeira célula vazia da layer 0.</summary>
    private void SendFileToSelectedCell(string path)
    {
        int layerIndex = Grid.SelectedClip?.LayerIndex ?? 0;
        int columnIndex = Grid.SelectedClip?.ColumnIndex ?? -1;
        if (columnIndex < 0)
            Grid.AddFileAutoColumn(layerIndex, path);
        else
            Grid.SetFiles(layerIndex, columnIndex, [path]);
        StatusText = $"Enviado para a grade: {Path.GetFileName(path)}";
    }

    /// <summary>Manda a imagem editada pra célula selecionada na grade; sem seleção,
    /// usa a primeira célula vazia da layer 0 (mesma heurística do watch-folder).</summary>
    private void SendImageToCell(string path, string suggestedName)
    {
        int layerIndex = Grid.SelectedClip?.LayerIndex ?? 0;
        int columnIndex = Grid.SelectedClip?.ColumnIndex ?? -1;
        if (columnIndex < 0)
        {
            Grid.AddFileAutoColumn(layerIndex, path);
        }
        else
        {
            Grid.SetClip(layerIndex, columnIndex, new Clip(new FileImageSource(path)) { Name = suggestedName });
        }
        StatusText = $"Imagem enviada para a célula: {suggestedName}";
    }

    /// <summary>Reabre o editor numa imagem já na grade (menu de contexto da célula) e,
    /// ao salvar, recarrega o clip no motor sem frame preto (comando ReloadClip).</summary>
    public void EditCellImage(int layerIndex, int columnIndex, string path)
    {
        double? compAspect = Grid.Model.Height > 0 ? (double)Grid.Model.Width / Grid.Model.Height : null;
        var vm = new ImageEditorViewModel(path, (editedPath, _) =>
        {
            Grid.SetClip(layerIndex, columnIndex, new Clip(new FileImageSource(editedPath))
            {
                Name = Grid.Model.Layers[layerIndex].Clips[columnIndex]?.Name ?? Path.GetFileNameWithoutExtension(editedPath),
            });
            _render.Post(new ReloadClip(layerIndex, columnIndex));
        }, compAspect);
        var window = new ImageEditorWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenInstallation()
    {
        var vm = new InstallationViewModel(_installation, UpdateInstallation);
        var window = new InstallationWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenHapConverter()
    {
        var window = new HapConverterWindow(new HapConverterViewModel()) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    /// <summary>MainWindow escuta para recarregar o KeymapService sem precisar reiniciar o app.</summary>
    public event Action? KeymapChanged;

    [RelayCommand]
    private void OpenKeymapEditor()
    {
        var window = new KeymapEditorWindow(new KeymapEditorViewModel()) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        KeymapChanged?.Invoke();
    }

    [RelayCommand]
    private void OpenVideo()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Adicionar Arquivo ao Deck",
            Filter = "Mídias|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.png;*.jpg;*.jpeg|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        SourceDescriptor src = ext is ".png" or ".jpg" or ".jpeg"
            ? new FileImageSource(dlg.FileName)
            : new FileVideoSource(dlg.FileName);

        Grid.SetClip(0, 0, new Clip(src) { Name = Path.GetFileName(dlg.FileName) });
        StatusText = $"Load p/ testes em [0,0]: {Path.GetFileName(dlg.FileName)}";
    }

    // ── Fase 7: projeto ──────────────────────────────────────────────────────

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmDiscardChanges()) return;

        var compModel = new Composition { ColumnCount = 8 };
        compModel.AddLayer(new Layer { Name = "Layer 1" });
        Grid.ReplaceModel(compModel);
        _outputSetup = new OutputSetup();
        _render.Post(new SyncOutputSetup(_outputSetup));
        Scenes.Clear();
        _render.Post(new SyncScenes([]));
        _installation = new InstallationConfig();
        _render.Post(new SyncInstallation(_installation));
        _watchFolders.Clear();
        SequencerRunning = false;
        _currentProjectPath = null;
        MasterFader = 1.0;
        Blackout = false;
        _isDirty = false;
        UpdateWindowTitle();
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (!ConfirmDiscardChanges()) return;

        var dlg = new OpenFileDialog { Title = "Abrir projeto", Filter = "Projeto VideoWallRW|*.vwrw" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            LoadProjectFromFile(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha ao abrir '{dlg.FileName}':\n{ex.Message}",
                "VideoWallRW", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadProjectFromFile(string path)
    {
        var project = ProjectSerializer.Load(path);
        ApplyProject(project);
        _currentProjectPath = path;
        _isDirty = false;
        UpdateWindowTitle();
        StatusText = $"Projeto carregado: {Path.GetFileName(path)}";

        CheckMissingMedia(project.Composition);
    }

    private void ApplyProject(ProjectFile project)
    {
        Grid.ReplaceModel(project.Composition);
        _outputSetup = project.Output;
        _render.Post(new SyncOutputSetup(_outputSetup));

        Scenes.Clear();
        foreach (var scene in project.Snapshots)
            Scenes.Add(new SceneViewModel(scene, _render, RemoveSceneInternal));
        _render.Post(new SyncScenes(project.Snapshots));

        _installation = project.Installation;
        _render.Post(new SyncInstallation(_installation));
        _watchFolders.Apply(_installation.WatchFolders);
        SequencerRunning = false;

        MasterFader = project.Composition.MasterOpacity;
        Blackout = project.Composition.Blackout;
    }

    private void CheckMissingMedia(Composition composition)
    {
        var missing = new List<string>();
        foreach (var layer in composition.Layers)
            foreach (var clip in layer.Clips)
            {
                string? path = clip?.Source switch
                {
                    FileVideoSource v => v.Path,
                    FileImageSource i => i.Path,
                    _ => null,
                };
                if (path is not null && !File.Exists(path))
                    missing.Add(path);
            }

        if (missing.Count > 0)
        {
            var result = MessageBox.Show(
                $"{missing.Count} arquivo(s) de mídia não encontrados (ex.: {missing[0]}).\n\n" +
                "Deseja localizar a pasta onde a mídia foi movida?",
                "Mídia ausente", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                RelinkMedia(composition);
        }
    }

    private void RelinkMedia(Composition composition)
    {
        var dlg = new OpenFolderDialog { Title = "Pasta com a mídia" };
        if (dlg.ShowDialog() != true) return;

        int relinked = 0;
        foreach (var layer in composition.Layers)
            for (int i = 0; i < layer.Clips.Length; i++)
            {
                var clip = layer.Clips[i];
                if (clip is null) continue;

                string? oldPath = clip.Source switch
                {
                    FileVideoSource v => v.Path,
                    FileImageSource im => im.Path,
                    _ => null,
                };
                if (oldPath is null || File.Exists(oldPath)) continue;

                string fileName = Path.GetFileName(oldPath);
                string candidate = Directory.EnumerateFiles(dlg.FolderName, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault() ?? "";
                if (candidate.Length == 0) continue;

                clip.Source = clip.Source switch
                {
                    FileVideoSource => new FileVideoSource(candidate),
                    FileImageSource => new FileImageSource(candidate),
                    var s => s,
                };
                relinked++;
            }

        Grid.SyncToEngine();
        StatusText = $"Relink: {relinked} arquivo(s) resolvido(s).";
    }

    [RelayCommand]
    private void SaveProject()
    {
        if (_currentProjectPath is null) { SaveProjectAs(); return; }
        SaveProjectToFile(_currentProjectPath);
    }

    [RelayCommand]
    private void SaveProjectAs()
    {
        var dlg = new SaveFileDialog { Title = "Salvar projeto", Filter = "Projeto VideoWallRW|*.vwrw", DefaultExt = ".vwrw" };
        if (dlg.ShowDialog() != true) return;
        SaveProjectToFile(dlg.FileName);
    }

    private void SaveProjectToFile(string path)
    {
        try
        {
            var project = BuildProjectFile();
            ProjectSerializer.Save(path, project);
            _currentProjectPath = path;
            _isDirty = false;
            UpdateWindowTitle();
            StatusText = $"Projeto salvo: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha ao salvar:\n{ex.Message}", "VideoWallRW", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ProjectFile BuildProjectFile()
    {
        Grid.Model.MasterOpacity = MasterFader;
        Grid.Model.Blackout = Blackout;
        return new ProjectFile
        {
            Composition = Grid.Model,
            Output = _outputSetup,
            Snapshots = Scenes.Select(s => s.Model).ToList(),
            Installation = _installation,
        };
    }

    /// <summary>Config atual do modo instalação (Fase 8), exposta para a janela de edição.</summary>
    public InstallationConfig Installation => _installation;

    /// <summary>Usado pelo OscService (T9.4) para resolver "/vwrw/scene/{nome}/trigger".</summary>
    public Guid? ResolveSceneByName(string name)
        => Scenes.FirstOrDefault(s => s.Name == name)?.Model.Id;

    /// <summary>Snapshot leve pro controle remoto web (T9.5) — só nomes, sem thumbs.</summary>
    public object BuildRemoteSnapshot() => new
    {
        layers = Grid.Model.Layers.Select(l => new
        {
            name = l.Name,
            clips = l.Clips.Select(c => c?.Name).ToArray(),
        }),
    };

    /// <summary>Chamado pela janela de Instalação depois de editar sequenciador/agenda/watch-folders.</summary>
    public void UpdateInstallation(InstallationConfig config)
    {
        _installation = config;
        _render.Post(new SyncInstallation(_installation));
        _watchFolders.Apply(_installation.WatchFolders);
        MarkDirty();
    }

    public void ToggleSequencer() => SequencerRunning = !SequencerRunning;

    partial void OnSequencerRunningChanged(bool value)
        => _render.Post(new SetSequencerRunning(value));

    private void Autosave()
    {
        try
        {
            AutosaveService.Save(_currentProjectPath, BuildProjectFile());
        }
        catch (Exception ex)
        {
            StatusText = $"Falha no autosave: {ex.Message}";
        }
    }

    private void CheckCrashRecovery()
    {
        if (!AutosaveService.WasUncleanShutdown())
            return;

        var autosave = AutosaveService.TryLoadAutosave(_currentProjectPath);
        if (autosave is null)
            return;

        var result = MessageBox.Show(
            "O VideoWallRW não foi fechado normalmente da última vez. " +
            "Encontramos um autosave — deseja recuperar o trabalho não salvo?",
            "Recuperação", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            ApplyProject(autosave);
            _isDirty = true;
            UpdateWindowTitle();
            StatusText = "Projeto recuperado do autosave.";
        }
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty) return true;
        var result = MessageBox.Show("Há mudanças não salvas. Descartar?", "VideoWallRW",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private void MarkDirty()
    {
        _isDirty = true;
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        string name = _currentProjectPath is null ? "Novo projeto" : Path.GetFileName(_currentProjectPath);
        WindowTitle = $"VideoWallRW — {name}{(_isDirty ? " *" : "")}";
    }

    public bool CanCloseWithoutPrompt() => !_isDirty || ConfirmDiscardChanges();

    public void OnClosing()
    {
        try { Autosave(); } catch { /* melhor esforço no fechamento */ }
    }

    // ── Fase 7: cenas ────────────────────────────────────────────────────────

    [RelayCommand]
    private void SaveScene()
    {
        var (columns, opacities) = Grid.CaptureSceneState();
        var snapshot = new SceneSnapshot
        {
            Name = $"Cena {Scenes.Count + 1}",
            ActiveColumns = columns,
            LayerOpacities = opacities,
            MasterFader = MasterFader,
        };
        Scenes.Add(new SceneViewModel(snapshot, _render, RemoveSceneInternal));
        _render.Post(new SyncScenes(Scenes.Select(s => s.Model).ToList()));
        MarkDirty();
    }

    private void RemoveSceneInternal(SceneViewModel scene)
    {
        Scenes.Remove(scene);
        _render.Post(new SyncScenes(Scenes.Select(s => s.Model).ToList()));
        MarkDirty();
    }

    // ── Fase 3/7: transporte e master fader/blackout ────────────────────────

    partial void OnMasterFaderChanged(double value)
        => _render.Post(new SetMasterOpacity(value));

    partial void OnBlackoutChanged(bool value)
        => _render.Post(new SetBlackout(value));

    private void OnStatsUpdated(EngineStats stats)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var monitor in Monitors)
            {
                var output = stats.Outputs.FirstOrDefault(
                    o => o.MonitorDeviceName == monitor.Info.DeviceName);
                monitor.FpsText = output is null ? "" : $"{output.Fps:F1} fps";
            }

            string outputsText = stats.Outputs.Count > 0
                ? string.Join("   |   ", stats.Outputs.Select(o => $"{o.MonitorDeviceName}: {o.Fps:F1} fps"))
                : "Nenhuma saída ativa";
            string dropsText = stats.DroppedFrames > 0 ? $"   |   ⚠ {stats.DroppedFrames} drops" : "";
            StatusText = $"{outputsText}   |   {stats.ActiveClipCount} clip(s) ativo(s){dropsText}";
        });
    }

    private void OnEngineFailed(string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusText = $"ERRO no engine: {message}";
            foreach (var monitor in Monitors) monitor.IsActive = false;
        });
    }

    public void ToggleMasterPause()
    {
        _masterPaused = !_masterPaused;
        _render.Post(new SetMasterPaused(_masterPaused));
        StatusText = _masterPaused ? "Pausa global ativa" : "Pausa global desativada";
    }

    public void ToggleBlackout() => Blackout = !Blackout;

    public void ToggleOutputs()
    {
        bool enableOutputs = Monitors.All(monitor => !monitor.IsActive);
        foreach (var monitor in Monitors)
            monitor.IsActive = enableOutputs;
    }
}

public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly RenderThread _render;

    public MonitorInfo Info { get; }
    public string Title => $"{Info.DeviceName} — {Info.Width}×{Info.Height} @ ({Info.X},{Info.Y})";

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _fpsText = "";

    public MonitorViewModel(MonitorInfo info, RenderThread render)
    {
        Info = info;
        _render = render;
    }

    partial void OnIsActiveChanged(bool value)
        => _render.Post(value
            ? new EnableOutput(Info.DeviceName)
            : new DisableOutput(Info.DeviceName));
}
