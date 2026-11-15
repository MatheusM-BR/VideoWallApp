using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
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
    private string? _currentProjectPath;
    private bool _isDirty;
    private OutputSetup _outputSetup = new();
    private InstallationConfig _installation = new();
    private readonly DispatcherTimer _autosaveTimer;
    private readonly DispatcherTimer _runtimeStateTimer;
    private readonly WatchFolderManager _watchFolders;
    private readonly Media.Ndi.NdiFinder? _ndiFinder;

    /// <summary>Fontes NDI vistas na rede agora — aba Sources (T29). Populada pelo
    /// NdiFinder (thread própria de descoberta) e espelhada aqui via Dispatcher.</summary>
    public ObservableCollection<string> NdiSources { get; } = [];
    public bool NdiAvailable { get; }
    public string? NdiUnavailableReason { get; }

    private void OnNdiSourcesChanged(IReadOnlyList<string> sources)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Atualiza por DIFERENÇA, nunca Clear()+re-add: a descoberta NDI publica a
            // cada ~1s mesmo sem novidade, e reconstruir a lista inteira zerava o
            // SelectedItem do ListBox uma vez por segundo — o duplo-clique e o arraste
            // pra célula falhavam silenciosamente por "nada selecionado".
            for (int i = NdiSources.Count - 1; i >= 0; i--)
                if (!sources.Contains(NdiSources[i]))
                    NdiSources.RemoveAt(i);

            foreach (var s in sources)
                if (!NdiSources.Contains(s))
                    NdiSources.Add(s);
        });
    }

    /// <summary>Duplo-clique numa fonte NDI (aba Sources): igual a SendFileToSelectedCell,
    /// mas cria um Clip com NdiSource em vez de FileVideoSource/FileImageSource.</summary>
    [RelayCommand]
    public void SendNdiToSelectedCell(string sourceName)
    {
        int layerIndex, columnIndex;
        if (Grid.SelectedClip is { } selected)
            (layerIndex, columnIndex) = (selected.LayerIndex, selected.ColumnIndex);
        else if (PickTargetCell() is { } cell)
            (layerIndex, columnIndex) = cell;
        else return;

        SendNdiToCell(sourceName, layerIndex, columnIndex);
    }

    /// <summary>Arrastar uma fonte NDI (aba Sources) direto numa célula da grade —
    /// mesmo destino final de SendNdiToSelectedCell, mas a célula já vem escolhida
    /// pelo drop (igual ao arraste de arquivo do painel Files).</summary>
    public void SendNdiToCell(string sourceName, int layerIndex, int columnIndex)
    {
        Grid.SetClip(layerIndex, columnIndex, new Core.Model.Clip(new Core.Model.NdiSource(sourceName))
        {
            Name = sourceName,
            Resize = Core.Model.ResizeMode.Fit,
            // Fonte ao vivo: não existe "próximo clip" quando ela "termina" (ela nunca
            // termina) — autopilot aqui não faz sentido semântico, mesmo já protegido
            // pela Duration=Infinity do NdiVideoSource.
            Autopilot = Core.Model.AutopilotAction.None,
        });
        StatusText = $"Enviado para a grade: NDI — {sourceName}";
    }

    // Janelas "de origem" (Agenda, importador de PDF) fecham sozinhas ao abrir o
    // editor de imagem — do contrário elas ficam por cima atrapalhando o arraste
    // da tela de recorte/tela do editor.
    private AgendaWindow? _agendaWindow;
    private PdfImportWindow? _pdfImportWindow;

    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];
    public ObservableCollection<SceneViewModel> Scenes { get; } = [];

    /// <summary>Cena "aberta" agora (T30) — null quando nenhuma cena foi selecionada
    /// (grade "solta", sem aba nenhuma ativa). Editar a grade com uma cena selecionada
    /// atualiza essa cena sozinho, ver <see cref="SyncSelectedSceneFromGrid"/>.</summary>
    [ObservableProperty] private SceneViewModel? _selectedScene;

    [ObservableProperty] private string _statusText = "Pronto — marque um monitor para ativar a saída.";
    [ObservableProperty] private double _masterFader = 1.0;
    [ObservableProperty] private bool _blackout;
    [ObservableProperty] private string _windowTitle = "RW VideoWall — Novo projeto";
    [ObservableProperty] private bool _sequencerRunning;

    /// <summary>Autopilot global: liga/desliga o autopilot de TODAS as layers de uma
    /// vez (envia um comando por layer) — botão em evidência + tecla Espaço.</summary>
    [ObservableProperty] private bool _autopilotAll = true;

    /// <summary>Dock direito: false = aba Files, true = aba Sources (recentes + NDI).</summary>
    [ObservableProperty] private bool _showSourcesTab;

    /// <summary>Composition/Preview Monitor ao vivo — desligado por padrão (custa uma
    /// leitura de textura da GPU por frame capturado); liga/desliga o readback no engine.</summary>
    [ObservableProperty] private bool _compositionMonitorEnabled;
    [ObservableProperty] private System.Windows.Media.Imaging.WriteableBitmap? _compositionMonitorBitmap;

    /// <summary>Com a captura ao vivo desligada, mostra a thumbnail do clip ativo mais
    /// "no topo" (última layer com um clip no ar, na ordem de blend) em vez de deixar
    /// o monitor mostrando só texto — dá pro operador ver o que está rodando mesmo
    /// sem pagar o custo de GPU do readback ao vivo.</summary>
    [ObservableProperty] private string? _activeClipThumbnailPath;

    public bool ShowMonitorPlaceholderText => !CompositionMonitorEnabled && ActiveClipThumbnailPath is null;

    partial void OnCompositionMonitorEnabledChanged(bool value)
    {
        _render.Post(new SetCompositionMonitorEnabled(value));
        if (!value) CompositionMonitorBitmap = null;
        OnPropertyChanged(nameof(ShowMonitorPlaceholderText));
    }

    partial void OnActiveClipThumbnailPathChanged(string? value) => OnPropertyChanged(nameof(ShowMonitorPlaceholderText));

    private void OnEngineClipTriggered(int layerIndex, int columnIndex)
        => Application.Current?.Dispatcher.BeginInvoke(() =>
            Grid.MarkClipTriggeredFromEngine(layerIndex, columnIndex));

    /// <summary>Percorre as layers em ordem — a última com um clip ativo é a que renderiza
    /// por cima (CompositionRenderer itera 0..N blendando cada uma sobre a anterior).</summary>
    private void RefreshActiveClipThumbnail()
    {
        string? thumb = null;
        foreach (var layer in Grid.Layers)
        {
            if (layer.Clips.FirstOrDefault(c => c.IsActive) is { ThumbnailPath: { } path })
                thumb = path;
        }
        ActiveClipThumbnailPath = thumb;
    }

    // Deduplica o disparo do PNG: guarda o último caminho por fonte NDI (a fonte
    // publica a cada 30s; encode fora da thread de captura).
    private readonly Dictionary<string, string> _ndiSnapshotPaths = new();

    /// <summary>Chamado na thread de captura NDI (~30s): gera um PNG reduzido do frame
    /// e usa como miniatura de toda célula que aponta pra essa fonte NDI.</summary>
    private void OnNdiSnapshot(string sourceName, byte[] bgra, int width, int height, int stride)
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Miniatura pequena (largura ~360px) — barata e suficiente pra célula.
                int targetW = Math.Min(width, 360);
                int targetH = Math.Max(1, (int)(height * (targetW / (double)width)));

                var src = System.Windows.Media.Imaging.BitmapSource.Create(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, stride);
                var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
                    src, new System.Windows.Media.ScaleTransform(targetW / (double)width, targetH / (double)height));
                scaled.Freeze(); // congelado = seguro pra encodar fora da UI thread

                string dir = Path.Combine(Path.GetTempPath(), "VideoWallRW", "ndi-thumbs");
                Directory.CreateDirectory(dir);
                // Nome ÚNICO por snapshot — o WPF cacheia imagem por caminho, então
                // reusar o mesmo nome mostraria o frame velho; o anterior é apagado depois.
                string safe = string.Concat(sourceName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                string path = Path.Combine(dir, $"{safe}-{DateTime.Now.Ticks}.png");

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
                using (var fs = File.Create(path))
                    encoder.Save(fs);

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    string? old = _ndiSnapshotPaths.GetValueOrDefault(sourceName);
                    _ndiSnapshotPaths[sourceName] = path;
                    foreach (var layer in Grid.Layers)
                        foreach (var cell in layer.Clips)
                            if (cell.Model?.Source is Core.Model.NdiSource n && n.SourceName == sourceName)
                                cell.ThumbnailPath = path;
                    // O anterior já não é exibido por ninguém (todos migraram pro novo) — apaga.
                    if (old is not null && old != path)
                        try { File.Delete(old); } catch { /* melhor esforço */ }
                });
            }
            catch { /* prévia é best-effort, nunca atrapalha a operação */ }
        });
    }

    private void OnCompositionFrameCaptured(byte[] pixelsBgra, int width, int height)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!CompositionMonitorEnabled) return;
            if (CompositionMonitorBitmap is not { } bmp || bmp.PixelWidth != width || bmp.PixelHeight != height)
            {
                bmp = new System.Windows.Media.Imaging.WriteableBitmap(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                CompositionMonitorBitmap = bmp;
            }
            bmp.WritePixels(new Int32Rect(0, 0, width, height), pixelsBgra, width * 4, 0);
        });
    }


    public CompositionViewModel Grid { get; }
    public FileBrowserViewModel FileBrowser { get; }

    private readonly bool _kiosk;

    public MainViewModel(GraphicsDevice gfx, RenderThread render, bool kiosk = false,
        Media.Ndi.NdiFinder? ndiFinder = null)
    {
        _gfx = gfx;
        _render = render;
        _kiosk = kiosk;

        _ndiFinder = ndiFinder;
        NdiAvailable = ndiFinder?.IsAvailable ?? false;
        NdiUnavailableReason = ndiFinder?.UnavailableReason;
        if (ndiFinder is not null)
        {
            foreach (var s in ndiFinder.CurrentSources) NdiSources.Add(s);
            ndiFinder.SourcesChanged += OnNdiSourcesChanged;
        }

        var compModel = new Composition { ColumnCount = 8 };
        compModel.AddLayer(new Layer { Name = "Camada 3" });
        compModel.AddLayer(new Layer { Name = "Camada 2" });
        compModel.AddLayer(new Layer { Name = "Camada 1" }); // Base

        Grid = new CompositionViewModel(compModel, _render);
        Grid.SyncToEngine();
        Grid.Changed += MarkDirty;
        Grid.Changed += RefreshMonitorRouteOptions; // layers mudaram → combos de roteamento também
        Grid.Changed += SyncSelectedSceneFromGrid; // T30: cada edição já atualiza a cena aberta
        Grid.Changed += EnsureColumnShortcuts;     // atalho pra cada coluna do projeto
        // Edição de propriedade (volume, duração, resize, transform, opacidade de
        // layer...) não passa pelo Changed estrutural. Só precisa sujar o projeto —
        // a cena aberta é congelada nos dois momentos que importam (trocar de cena e
        // salvar), em vez de clonar a composição inteira a cada tick de slider.
        Grid.Edited += MarkDirty;
        EnsureCurrentGridHasScene();
        _watchFolders = new WatchFolderManager(Grid, Dispatcher.CurrentDispatcher);
        FileBrowser = new FileBrowserViewModel(SendFileToSelectedCell);

        _render.StatsUpdated += OnStatsUpdated;
        _render.EngineFailed += OnEngineFailed;
        _render.CompositionFrameCaptured += OnCompositionFrameCaptured;
        _render.ClipTriggered += OnEngineClipTriggered;
        // Prévia da célula NDI a cada 30s (fonte no ar publica o frame; aqui vira PNG).
        Media.Ndi.NdiSnapshotHub.SnapshotReady += OnNdiSnapshot;
        RefreshMonitors();
        EnsureColumnShortcuts(); // grade inicial já garante os atalhos de coluna

        // T7.3: autosave a cada 120s.
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        _autosaveTimer.Tick += (_, _) => Autosave();
        _autosaveTimer.Start();

        // T8.1: estado de runtime a cada 5s, para o modo kiosk retomar sozinho após reinício.
        _runtimeStateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _runtimeStateTimer.Tick += (_, _) => SaveRuntimeState();
        _runtimeStateTimer.Start();
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
            Monitors.Add(new MonitorViewModel(info, _render, OnMonitorRouteChanged)
            {
                IsActive = previouslyActive.Contains(info.DeviceName),
                // Depois do IsActive de propósito: restaurar o estado anterior ao
                // reenumerar monitores não é edição do operador, não pode sujar o projeto.
                Edited = MarkDirty,
            });
        RefreshMonitorRouteOptions();
    }

    /// <summary>Preenche o combo "Composição / Layer N" de cada monitor com os nomes
    /// atuais das layers e a seleção salva no OutputSetup. Também espelha o nome dado
    /// à tela no Advanced Output pro rodapé SAÍDAS (pedido do operador: renomear lá
    /// troca o nome aqui também).</summary>
    private void RefreshMonitorRouteOptions()
    {
        var layerNames = Grid.Layers.Select(l => l.Name).ToList();
        foreach (var monitor in Monitors)
        {
            var screen = _outputSetup.Screens.FirstOrDefault(s => s.MonitorDeviceName == monitor.Info.DeviceName);
            monitor.SetRouteOptions(layerNames, screen?.SourceLayerIndex);
            monitor.CustomName = screen?.Name;
        }
    }

    /// <summary>Roteamento rápido layer→monitor pela linha SAÍDAS: grava no OutputSetup
    /// (criando a tela fullscreen padrão se o monitor ainda não estava configurado).</summary>
    private void OnMonitorRouteChanged(MonitorViewModel monitor, int? layerIndex)
    {
        var screen = _outputSetup.Screens.FirstOrDefault(s => s.MonitorDeviceName == monitor.Info.DeviceName);
        if (screen is null)
        {
            screen = OutputSetup.CreateFullscreenScreen(monitor.Info.DeviceName,
                Grid.Model.Width, Grid.Model.Height, monitor.Info.Width, monitor.Info.Height);
            _outputSetup.Screens.Add(screen);
        }
        screen.SourceLayerIndex = layerIndex;
        _render.Post(new SyncOutputSetup(_outputSetup));
        MarkDirty();
        StatusText = layerIndex is { } idx
            ? $"{monitor.Info.DeviceName} → Layer {idx + 1}"
            : $"{monitor.Info.DeviceName} → composição inteira";
    }

    [RelayCommand]
    private void OpenOutputSetup()
    {
        var vm = new OutputSetupViewModel(_render, MonitorEnumerator.Enumerate(_gfx),
            () => Grid.Model.Width, () => Grid.Model.Height,
            () => CompositionMonitorEnabled ? CompositionMonitorBitmap : null);
        vm.LoadFromModel(_outputSetup);
        var window = new OutputSetupWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        _outputSetup = vm.ToModel();
        RefreshMonitorRouteOptions(); // nome/roteamento editados lá refletem no rodapé na hora
        MarkDirty();
    }

    [RelayCommand]
    private void OpenHelp()
    {
        var window = new HelpWindow { Owner = Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenAgenda()
    {
        var vm = new ViewModels.AgendaViewModel(OpenImageEditorForFile, OpenPdfImporter, RevealInLibrary);
        _agendaWindow = new AgendaWindow(vm) { Owner = Application.Current.MainWindow };
        _agendaWindow.Closed += (_, _) => _agendaWindow = null;
        _agendaWindow.Show();
    }

    /// <summary>Abre o editor numa imagem nova (Agenda/PDF/avulsa): a inserção na grade
    /// é pelo chip "🖼 ARRASTE p/ célula" do próprio editor — solta na célula que quiser.
    /// Fecha Agenda/importador de PDF por baixo, senão eles ficam por cima atrapalhando
    /// o arraste das guias de recorte/tela do editor.</summary>
    private void OpenImageEditorForFile(string path, string suggestedName)
    {
        CloseSourceWindows();
        double? compAspect = Grid.Model.Height > 0 ? (double)Grid.Model.Width / Grid.Model.Height : null;
        var vm = new ImageEditorViewModel(path, onSaved: null, compAspect);
        vm.FileSaved += RevealInLibrary;
        var window = new ImageEditorWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>Biblioteca → Arquivos, aberta na pasta do arquivo e com ele selecionado.</summary>
    public void RevealInLibrary(string path)
    {
        ShowSourcesTab = false;
        FileBrowser.ShowFile(path);
    }

    private void OpenPdfImporter(string pdfPath)
    {
        var vm = new PdfImportViewModel(pdfPath, (imagePath, name) => OpenImageEditorForFile(imagePath, name));
        _pdfImportWindow = new PdfImportWindow(vm) { Owner = Application.Current.MainWindow };
        _pdfImportWindow.Closed += (_, _) => _pdfImportWindow = null;
        _pdfImportWindow.Show();
    }

    private void CloseSourceWindows()
    {
        _agendaWindow?.Close();
        _pdfImportWindow?.Close();
    }

    /// <summary>Abre o seletor de célula de destino, pré-selecionando a célula atual.
    /// Retorna null se o operador cancelar.</summary>
    private (int Layer, int Column)? PickTargetCell()
    {
        var dlg = new CellPickerWindow(
            Grid.Layers.Select(l => l.Name).ToList(), Grid.Model.ColumnCount,
            Grid.SelectedClip?.LayerIndex ?? 0, Grid.SelectedClip?.ColumnIndex ?? 0)
        {
            Owner = Application.Current.MainWindow,
        };
        return dlg.ShowDialog() == true ? (dlg.SelectedLayer, dlg.SelectedColumn) : null;
    }

    /// <summary>Painel Sources (duplo-clique num recente) — mesma regra do painel Files.</summary>
    [RelayCommand]
    public void SendRecentSource(string path) => SendFileToSelectedCell(path);

    /// <summary>Painel Files (duplo-clique): com célula selecionada vai direto nela;
    /// sem seleção, pergunta o destino.</summary>
    private void SendFileToSelectedCell(string path)
    {
        int layerIndex, columnIndex;
        if (Grid.SelectedClip is { } selected)
        {
            (layerIndex, columnIndex) = (selected.LayerIndex, selected.ColumnIndex);
        }
        else if (PickTargetCell() is { } cell)
        {
            (layerIndex, columnIndex) = cell;
        }
        else return;

        Grid.SetFiles(layerIndex, columnIndex, [path]);
        StatusText = $"Enviado para a grade: {Path.GetFileName(path)}";
    }

    /// <summary>Reabre o editor numa imagem já na grade (menu de contexto da célula) e,
    /// ao salvar, recarrega o clip no motor sem frame preto (comando ReloadClip).</summary>
    public void EditCellImage(int layerIndex, int columnIndex, string path)
    {
        CloseSourceWindows();
        double? compAspect = Grid.Model.Height > 0 ? (double)Grid.Model.Width / Grid.Model.Height : null;
        var vm = new ImageEditorViewModel(path, (editedPath, _) =>
        {
            var previous = Grid.Model.Layers[layerIndex].Clips[columnIndex];
            Grid.SetClip(layerIndex, columnIndex, new Clip(new FileImageSource(editedPath))
            {
                Name = previous?.Name ?? Path.GetFileNameWithoutExtension(editedPath),
                // Editar não reseta o timer/autopilot que o operador já tinha ajustado.
                ImageDuration = previous?.ImageDuration ?? 7,
                Autopilot = previous?.Autopilot ?? AutopilotAction.PlayNextClip,
            });
            _render.Post(new ReloadClip(layerIndex, columnIndex));
        }, compAspect);
        vm.FileSaved += RevealInLibrary;
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

    private int _keymapColumns = -1;

    /// <summary>Toda coluna do projeto tem atalho (T: "acompanha a quantidade máxima de
    /// colunas do projeto, independente da cena"). Só mexe no arquivo quando o número de
    /// colunas realmente muda — Grid.Changed dispara a cada edição de célula.</summary>
    private void EnsureColumnShortcuts()
    {
        int columns = Grid.Model.ColumnCount;
        if (columns == _keymapColumns) return;
        _keymapColumns = columns;
        Input.KeymapService.EnsureColumns(Input.KeymapService.FilePath, columns);
        KeymapChanged?.Invoke();
    }

    [RelayCommand]
    private void OpenKeymapEditor()
    {
        var names = Grid.Model.Layers.Select((layer, index) =>
            string.IsNullOrWhiteSpace(layer.Name) ? $"Camada {index + 1}" : layer.Name);
        var window = new KeymapEditorWindow(new KeymapEditorViewModel(names)) { Owner = Application.Current.MainWindow };
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

        if (PickTargetCell() is not { } cell)
            return;

        Grid.SetFiles(cell.Layer, cell.Column, [dlg.FileName]);
        StatusText = $"Carregado em [{cell.Layer},{cell.Column}]: {Path.GetFileName(dlg.FileName)}";
    }

    // ── Fase 7: projeto ──────────────────────────────────────────────────────

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmDiscardChanges()) return;

        var compModel = new Composition { ColumnCount = 8 };
        compModel.AddLayer(new Layer { Name = "Camada 1" });
        Grid.ReplaceModel(compModel);
        _outputSetup = new OutputSetup();
        _render.Post(new SyncOutputSetup(_outputSetup));
        SelectedScene = null;
        Scenes.Clear();
        EnsureCurrentGridHasScene();
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

        Grid.ImportBlankCellLinks(project.Meta.BlankCellLinks); // vínculos de células vazias

        SelectedScene = null; // cenas antigas foram descartadas — evita referência solta
        Scenes.Clear();
        foreach (var scene in project.Snapshots)
            Scenes.Add(NewSceneViewModel(scene));
        _render.Post(new SyncScenes(project.Snapshots));

        // Reabre na mesma aba em que o projeto foi salvo. Sem trocar a grade: ela já
        // veio de project.Composition, que É a grade dessa cena (BuildProjectFile
        // congela a cena aberta a partir da grade antes de gravar). Chamar SelectScene
        // aqui faria um ReplaceModel redundante — e sujaria o projeto recém-aberto.
        if (project.Meta.SelectedSceneId is { } openScene)
        {
            SelectedScene = Scenes.FirstOrDefault(s => s.Id == openScene);
            if (SelectedScene is not null)
                SelectedScene.IsSelected = true;
        }
        EnsureCurrentGridHasScene();

        _installation = project.Installation;
        _render.Post(new SyncInstallation(_installation));
        _watchFolders.Apply(_installation.WatchFolders);

        // Restaura o toggle global sem disparar a propagação normal (que forçaria
        // TODAS as layers a seguir esse valor) — as layers já vieram com o
        // AutopilotEnabled certo de cada uma via Grid.ReplaceModel logo acima.
        _restoringAutopilotAll = true;
        AutopilotAll = project.Meta.AutopilotAllEnabled;
        _restoringAutopilotAll = false;

        // Sequenciador: restaura o estado salvo (liga/desliga o comando no engine de
        // verdade), em vez de sempre voltar parado mesmo que estivesse rodando.
        SequencerRunning = project.Meta.SequencerRunning;

        MasterFader = project.Composition.MasterOpacity;
        Blackout = project.Composition.Blackout;
        RefreshMonitorRouteOptions();

        // T32: religa os telões que estavam LIGADOS no save — setar IsActive dispara
        // o EnableOutput de verdade via OnIsActiveChanged; monitores que não existem
        // mais nesta máquina são simplesmente ignorados.
        foreach (var monitor in Monitors)
            monitor.IsActive = project.Meta.EnabledOutputs.Contains(monitor.Info.DeviceName);
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
        // Idem SelectScene: garante que a cena ABERTA vá pro arquivo com as últimas
        // edições de propriedade, que não passam pelo Changed estrutural.
        SyncSelectedSceneFromGrid();

        Grid.Model.MasterOpacity = MasterFader;
        Grid.Model.Blackout = Blackout;
        return new ProjectFile
        {
            Composition = Grid.Model,
            Output = _outputSetup,
            Snapshots = Scenes.Select(s => s.Model).ToList(),
            Installation = _installation,
            Meta = new ProjectMeta
            {
                AutopilotAllEnabled = AutopilotAll,
                SequencerRunning = SequencerRunning,
                EnabledOutputs = Monitors.Where(m => m.IsActive).Select(m => m.Info.DeviceName).ToList(),
                BlankCellLinks = Grid.ExportBlankCellLinks(),
                SelectedSceneId = SelectedScene?.Id,
            },
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
    {
        _render.Post(new SetSequencerRunning(value));
        MarkDirty(); // vai pro arquivo (ProjectMeta.SequencerRunning)
    }

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
        WindowTitle = $"RW VideoWall — {name}{(_isDirty ? " *" : "")}";
    }

    public bool CanCloseWithoutPrompt() => !_isDirty || ConfirmDiscardChanges();

    public void OnClosing()
    {
        try { Autosave(); } catch { /* melhor esforço no fechamento */ }
    }

    // ── Cenas (T30): cada cena é uma grade completa e independente, como uma aba ──

    [RelayCommand]
    private void NewScene()
    {
        // A composição inicial podia estar fora de qualquer cena. Antes de abrir a
        // nova grade, registra o estado atual para que ele continue acessível.
        EnsureCurrentGridHasScene();
        SyncSelectedSceneFromGrid();

        var blank = new Composition { ColumnCount = 8 };
        blank.AddLayer(new Layer { Name = "Camada 3" });
        blank.AddLayer(new Layer { Name = "Camada 2" });
        blank.AddLayer(new Layer { Name = "Camada 1" });

        var snapshot = new SceneSnapshot { Name = $"Cena {Scenes.Count + 1}", Grid = blank };
        var vm = NewSceneViewModel(snapshot);
        Scenes.Add(vm);
        _render.Post(new SyncScenes(Scenes.Select(s => s.Model).ToList()));
        SelectScene(vm); // "abrir uma cena nova é um cenário limpo" — já entra nela
        MarkDirty();
    }

    /// <summary>Garante que a grade visível pertença a uma cena. Projetos novos e
    /// arquivos antigos sem SelectedSceneId deixam de ter uma composição “solta”
    /// que desaparece quando o operador cria a próxima cena.</summary>
    private void EnsureCurrentGridHasScene()
    {
        if (SelectedScene is not null) return;

        var grid = Grid.Model.Clone();
        var snapshot = new SceneSnapshot
        {
            Name = $"Cena {Scenes.Count + 1}",
            Grid = grid,
            BlankCellLinks = Grid.ExportBlankCellLinks(),
        };
        var scene = NewSceneViewModel(snapshot);
        Scenes.Add(scene);
        SelectedScene = scene;
        scene.IsSelected = true;
        _render.Post(new SyncScenes(Scenes.Select(s => s.Model).ToList()));
    }

    /// <summary>Troca a grade inteira da tela do operador pra a da cena clicada — como
    /// trocar de aba. A cena que sai é congelada aqui mesmo, na primeira linha: o sync
    /// contínuo do Grid.Changed só enxerga mudança ESTRUTURAL, então ajuste de
    /// propriedade (volume, duração, resize, transform) só entra por este caminho.</summary>
    private void SelectScene(SceneViewModel scene)
    {
        if (ReferenceEquals(SelectedScene, scene)) return;

        // Congela a cena que está SAINDO com o estado atual da grade. Edições de
        // propriedade de clip/layer não disparam Changed, então o sync contínuo não
        // as tinha visto: sem esta linha, ajustar volume/duração/resize e clicar em
        // outra aba fazia a cena voltar com a versão velha, perdendo o ajuste.
        SyncSelectedSceneFromGrid();

        if (SelectedScene is not null)
            SelectedScene.IsSelected = false;
        SelectedScene = scene;
        scene.IsSelected = true;

        // Lê os valores da cena ANTES de mexer em qualquer coisa: atribuir MasterFader/
        // Blackout dispara SyncSelectedSceneFromGrid, que sobrescreve scene.Model.Grid
        // com um clone carimbado com os valores AINDA da cena anterior. Sem guardar
        // antes (e sem suprimir o sync durante a troca), o Blackout salvo era perdido
        // e a cena abria com o estado errado.
        double masterOfScene = scene.Model.Grid.MasterOpacity;
        bool blackoutOfScene = scene.Model.Grid.Blackout;

        _switchingScene = true;
        try
        {
            Grid.ReplaceModel(scene.Model.Grid.Clone());
            Grid.ImportBlankCellLinks(scene.Model.BlankCellLinks); // vínculos vazios são da cena
            MasterFader = masterOfScene;
            Blackout = blackoutOfScene;
        }
        finally
        {
            _switchingScene = false;
        }

        RefreshMonitorRouteOptions();
        MarkDirty();
    }

    /// <summary>Suprime o auto-save da cena enquanto a troca está em andamento (a grade
    /// e o master/blackout mudam em passos, e cada passo dispararia um snapshot parcial).</summary>
    private bool _switchingScene;

    /// <summary>Atalhos PageDown/PageUp (KeymapService) — percorre as cenas na ordem em
    /// que aparecem na barra, dando a volta nas pontas. Sem cena nenhuma criada ainda,
    /// não faz nada (não existe "cena 0" implícita).</summary>
    public void SelectNextScene() => SelectSceneByOffset(1);
    public void SelectPreviousScene() => SelectSceneByOffset(-1);

    private void SelectSceneByOffset(int offset)
    {
        if (Scenes.Count == 0) return;
        int current = SelectedScene is null ? -1 : Scenes.IndexOf(SelectedScene);
        int next = ((current + offset) % Scenes.Count + Scenes.Count) % Scenes.Count;
        SelectScene(Scenes[next]);
    }

    /// <summary>Chamado a cada Grid.Changed (qualquer edição de célula/coluna/layer) —
    /// clona a grade inteira de volta pra dentro da cena aberta agora, pra "editar com
    /// uma cena selecionada já atualiza ela sozinha" funcionar sem passo manual.</summary>
    private void SyncSelectedSceneFromGrid()
    {
        if (SelectedScene is null || _switchingScene) return;
        var snapshot = Grid.Model.Clone();
        snapshot.MasterOpacity = MasterFader;
        snapshot.Blackout = Blackout;
        SelectedScene.Model.Grid = snapshot;
        SelectedScene.Model.BlankCellLinks = Grid.ExportBlankCellLinks();
    }

    /// <summary>Ponto único de criação da aba de cena — garante que renomear/atribuir
    /// atalho também suje o projeto (ver SceneViewModel.Edited).</summary>
    private SceneViewModel NewSceneViewModel(SceneSnapshot snapshot)
        => new(snapshot, SelectScene, RemoveSceneInternal) { Edited = MarkDirty };

    private void RemoveSceneInternal(SceneViewModel scene)
    {
        var confirm = MessageBox.Show($"Excluir a cena \"{scene.Name}\"?", "VideoWallRW",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        if (ReferenceEquals(SelectedScene, scene))
            SelectedScene = null;
        Scenes.Remove(scene);
        _render.Post(new SyncScenes(Scenes.Select(s => s.Model).ToList()));
        MarkDirty();
    }

    // ── Fase 3/7: transporte e master fader/blackout ────────────────────────

    partial void OnMasterFaderChanged(double value)
    {
        _render.Post(new SetMasterOpacity(value));
        SyncSelectedSceneFromGrid();
        MarkDirty(); // master fader vai pro arquivo (Composition.MasterOpacity)
    }

    partial void OnBlackoutChanged(bool value)
    {
        _render.Post(new SetBlackout(value));
        SyncSelectedSceneFromGrid();
        MarkDirty(); // idem blackout (Composition.Blackout)
    }

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

            if (stats.Positions is not null)
                Grid.UpdatePlayheads(stats.Positions);

            RefreshActiveClipThumbnail();
        });
    }

    /// <summary>
    /// Chamado (na UI) depois que o engine morreu e o estado já foi salvo. O App decide
    /// como reiniciar: pelo watchdog, se ele estiver presente, ou relançando o processo.
    /// </summary>
    public Action<string>? EngineRecoveryRequested { get; set; }

    private void OnEngineFailed(string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Salva ANTES de desligar os monitores: o autosave grava quais telões
            // estavam ligados (EnabledOutputs), e é isso que o processo reiniciado religa.
            Autosave();
            SaveRuntimeState();
            StatusText = $"ERRO no engine: {message} — reiniciando…";
            foreach (var monitor in Monitors) monitor.IsActive = false;
            EngineRecoveryRequested?.Invoke(message);
        });
    }

    /// <summary>
    /// Retoma a última sessão depois de um reinício sem operador (modo kiosk/watchdog ou
    /// recuperação de falha do engine): prefere o autosave quando ele é mais novo que o
    /// arquivo do projeto (edições ainda não salvas), e religa exatamente o que estava no ar.
    /// </summary>
    public bool ResumeLastSession()
    {
        var runtime = RuntimeStateService.TryLoad();
        if (runtime is null) return false;

        string? projectPath = runtime.ProjectPath is { } p && File.Exists(p) ? p : null;
        string autosavePath = AutosaveService.GetAutosavePath(projectPath);
        bool autosaveIsNewer = File.Exists(autosavePath) &&
            (projectPath is null || File.GetLastWriteTimeUtc(autosavePath) > File.GetLastWriteTimeUtc(projectPath));

        if (autosaveIsNewer)
        {
            ApplyProject(ProjectSerializer.Load(autosavePath));
            _currentProjectPath = projectPath;
            _isDirty = true; // conteúdo veio do autosave: ainda não está no arquivo do usuário
            UpdateWindowTitle();
        }
        else if (projectPath is not null)
        {
            LoadProjectSilently(projectPath);
        }
        else
        {
            return false;
        }

        ApplyRuntimeState(runtime);
        return true;
    }

    /// <summary>Mantém o nome antigo por compatibilidade com o keymap ("ToggleMasterPause"
    /// e a tecla Espaço) — hoje alterna o autopilot global.</summary>
    public void ToggleMasterPause() => AutopilotAll = !AutopilotAll;

    /// <summary>Ao restaurar um projeto salvo, o toggle global não deve forçar todas as
    /// layers a seguir o mesmo valor — cada uma já veio com o AutopilotEnabled certo
    /// pelo próprio projeto (podem ter sido salvas em estados diferentes entre si).</summary>
    private bool _restoringAutopilotAll;

    partial void OnAutopilotAllChanged(bool value)
    {
        if (_restoringAutopilotAll) return;

        // "Envia comando para todos": o toggle global dirige o toggle de cada layer.
        foreach (var layer in Grid.Layers)
            layer.AutopilotEnabled = value;
        StatusText = value
            ? "Troca automática ligada em todas as camadas"
            : "Troca automática desligada — os vídeos continuam; ao terminar, o clipe permanece na camada";
        MarkDirty(); // vai pro arquivo (ProjectMeta.AutopilotAllEnabled)
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
    private readonly Action<MonitorViewModel, int?>? _routeChanged;
    private bool _suppressRoute;

    public MonitorInfo Info { get; }

    /// <summary>Nome dado pelo operador no Advanced Output (Screen.Name) — quando
    /// definido, o rodapé SAÍDAS mostra ele em vez do nome técnico do Windows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? _customName;

    public string Title
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(CustomName) || CustomName == Info.DeviceName
                ? Info.DeviceName
                : $"{CustomName} ({Info.DeviceName})";
            return $"{name} — {Info.Width}×{Info.Height} @ ({Info.X},{Info.Y})";
        }
    }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _fpsText = "";

    /// <summary>Opções de roteamento: [0]="Composição", [1..]="layers".</summary>
    public ObservableCollection<string> RouteOptions { get; } = [];

    /// <summary>0 = composição inteira; N = layer N-1 isolada neste monitor.</summary>
    [ObservableProperty] private int _selectedRouteIndex;

    /// <summary>Avisa que o telão foi ligado/desligado — o projeto guarda quais
    /// monitores estavam LIGADOS (ProjectMeta.EnabledOutputs) pra religar sozinho na
    /// próxima abertura, então marcar/desmarcar tem que sujar o projeto.</summary>
    public Action? Edited { get; set; }

    public MonitorViewModel(MonitorInfo info, RenderThread render,
        Action<MonitorViewModel, int?>? routeChanged = null)
    {
        Info = info;
        _render = render;
        _routeChanged = routeChanged;
    }

    public void SetRouteOptions(IReadOnlyList<string> layerNames, int? currentLayerIndex)
    {
        _suppressRoute = true;
        RouteOptions.Clear();
        RouteOptions.Add("Composição");
        for (int i = 0; i < layerNames.Count; i++)
            RouteOptions.Add($"{i + 1}: {layerNames[i]}");
        SelectedRouteIndex = currentLayerIndex is { } idx && idx >= 0 && idx < layerNames.Count ? idx + 1 : 0;
        _suppressRoute = false;
    }

    partial void OnSelectedRouteIndexChanged(int value)
    {
        if (_suppressRoute) return;
        _routeChanged?.Invoke(this, value <= 0 ? null : value - 1);
    }

    partial void OnIsActiveChanged(bool value)
    {
        _render.Post(value
            ? new EnableOutput(Info.DeviceName)
            : new DisableOutput(Info.DeviceName));
        Edited?.Invoke();
    }
}
