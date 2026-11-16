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
    [ObservableProperty] private string _windowTitle = "RWVideoWall — Novo projeto";
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

}
