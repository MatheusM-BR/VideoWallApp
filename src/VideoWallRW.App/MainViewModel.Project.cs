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

public sealed partial class MainViewModel
{
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
                "RWVideoWall", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show($"Falha ao salvar:\n{ex.Message}", "RWVideoWall", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var result = MessageBox.Show("Há mudanças não salvas. Descartar?", "RWVideoWall",
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
        WindowTitle = $"RWVideoWall — {name}{(_isDirty ? " *" : "")}";
    }

    public bool CanCloseWithoutPrompt() => !_isDirty || ConfirmDiscardChanges();

    public void OnClosing()
    {
        try { Autosave(); } catch { /* melhor esforço no fechamento */ }
    }

}
