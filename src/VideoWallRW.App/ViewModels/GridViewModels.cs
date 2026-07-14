using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Model;
using VideoWallRW.Engine;

namespace VideoWallRW.App.ViewModels;

public sealed partial class ClipViewModel : ObservableObject
{
    private readonly int _layerIndex;
    private readonly int _colIndex;
    private readonly RenderThread _render;
    private readonly Action<ClipViewModel> _select;
    private readonly Action<ClipViewModel> _changed;
    private readonly Action<ClipViewModel>? _triggered;
    private bool _updatingFromModel;

    public int LayerIndex => _layerIndex;
    public int ColumnIndex => _colIndex;

    [ObservableProperty] private string _name = "Empty";
    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private string? _thumbnailPath;
    [ObservableProperty] private PlayMode _playMode;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private AutopilotAction _autopilot;
    [ObservableProperty] private string _inPointText = string.Empty;
    [ObservableProperty] private string _outPointText = string.Empty;
    [ObservableProperty] private string _imageDurationText = string.Empty;
    [ObservableProperty] private ResizeMode? _resize;
    [ObservableProperty] private double _volume = 1.0;
    [ObservableProperty] private bool _mute;

    public Clip? Model { get; private set; }

    public ClipViewModel(Clip? model, int layerIndex, int colIndex, RenderThread render,
        Action<ClipViewModel> select, Action<ClipViewModel> changed, Action<ClipViewModel>? triggered = null)
    {
        Model = model;
        _layerIndex = layerIndex;
        _colIndex = colIndex;
        _render = render;
        _select = select;
        _changed = changed;
        _triggered = triggered;

        UpdateFromModel();
    }

    public void UpdateFromModel()
    {
        _updatingFromModel = true;
        HasMedia = Model?.Source != null;
        Name = Model?.Name ?? "Empty";
        PlayMode = Model?.PlayMode ?? PlayMode.Loop;
        Speed = Model?.Speed ?? 1.0;
        Autopilot = Model?.Autopilot ?? AutopilotAction.None;
        InPointText = FormatOptional(Model?.InPoint);
        OutPointText = FormatOptional(Model?.OutPoint);
        ImageDurationText = FormatOptional(Model?.ImageDuration);
        Resize = Model?.Resize;
        Volume = Model?.Volume ?? 1.0;
        Mute = Model?.Mute ?? false;
        _updatingFromModel = false;
    }

    // Usado p/ injetar novo Model caso Drag&Drop ocorra
    public void SetModel(Clip model)
    {
        Model = model;
        UpdateFromModel();
    }

    [RelayCommand]
    private void Trigger()
    {
        _select(this);
        if (!HasMedia) return;
        _render.Post(new TriggerClip(_layerIndex, _colIndex));
        _triggered?.Invoke(this);
    }

    partial void OnNameChanged(string value) => ApplyEdits();
    partial void OnPlayModeChanged(PlayMode value) => ApplyEdits();
    partial void OnSpeedChanged(double value) => ApplyEdits();
    partial void OnAutopilotChanged(AutopilotAction value) => ApplyEdits();
    partial void OnInPointTextChanged(string value) => ApplyEdits();
    partial void OnOutPointTextChanged(string value) => ApplyEdits();
    partial void OnImageDurationTextChanged(string value) => ApplyEdits();
    partial void OnResizeChanged(ResizeMode? value) => ApplyEdits();
    partial void OnVolumeChanged(double value) => ApplyEdits();
    partial void OnMuteChanged(bool value) => ApplyEdits();

    private void ApplyEdits()
    {
        if (_updatingFromModel || Model is null)
            return;

        if (!TryParseOptional(InPointText, out double? inPoint) ||
            !TryParseOptional(OutPointText, out double? outPoint) ||
            !TryParseOptional(ImageDurationText, out double? imageDuration))
            return;

        Model.Name = Name;
        Model.PlayMode = PlayMode;
        Model.Speed = Math.Clamp(Speed, 0.1, 4.0);
        Model.Autopilot = Autopilot;
        Model.InPoint = inPoint;
        Model.OutPoint = outPoint;
        Model.ImageDuration = imageDuration;
        Model.Resize = Resize;
        Model.Volume = Math.Clamp(Volume, 0.0, 1.0);
        Model.Mute = Mute;
        _changed(this);
    }

    private static string FormatOptional(double? value)
        => value?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;

    private static bool TryParseOptional(string text, out double? value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }
}

public sealed partial class LayerViewModel : ObservableObject
{
    private readonly int _layerIndex;
    private readonly RenderThread _render;
    private readonly Action<ClipViewModel> _selectClip;
    private readonly Action<ClipViewModel> _clipChanged;

    public int LayerIndex => _layerIndex;

    [ObservableProperty] private string _name = "Layer";
    [ObservableProperty] private BlendMode _blend;
    [ObservableProperty] private bool _bypass;
    [ObservableProperty] private bool _solo;
    [ObservableProperty] private double _transitionSeconds;

    /// <summary>
    /// Última coluna disparada nesta layer pela UI — usado para capturar cenas
    /// (T7.5). Reflete apenas triggers vindos do painel (sem MIDI/OSC ainda,
    /// então é uma fonte de verdade confiável por enquanto).
    /// </summary>
    [ObservableProperty] private int _lastTriggeredColumn = -1;

    private double _opacity = 1.0;
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (SetProperty(ref _opacity, value))
            {
                Model.Opacity = value;
                _render.Post(new SetLayerOpacity(_layerIndex, value));
            }
        }
    }

    public Layer Model { get; }
    public ObservableCollection<ClipViewModel> Clips { get; } = [];

    public LayerViewModel(Layer model, int layerIndex, RenderThread render,
        Action<ClipViewModel> selectClip, Action<ClipViewModel> clipChanged)
    {
        Model = model;
        _layerIndex = layerIndex;
        _render = render;
        _selectClip = selectClip;
        _clipChanged = clipChanged;
        Name = model.Name;
        _opacity = model.Opacity;
        Blend = model.Blend;
        Bypass = model.Bypass;
        Solo = model.Solo;
        TransitionSeconds = model.TransitionSeconds;

        for (int i = 0; i < model.Clips.Length; i++)
        {
            Clips.Add(new ClipViewModel(model.Clips[i], layerIndex, i, render, _selectClip, _clipChanged,
                clip => LastTriggeredColumn = clip.ColumnIndex));
        }
    }

    public void AddClip(ClipViewModel clip) => Clips.Add(clip);

    [RelayCommand]
    private void Clear()
    {
        _render.Post(new ClearLayer(_layerIndex));
    }

    partial void OnBlendChanged(BlendMode value) => UpdateLayerState();
    partial void OnBypassChanged(bool value) => UpdateLayerState();
    partial void OnSoloChanged(bool value) => UpdateLayerState();
    partial void OnTransitionSecondsChanged(double value) => UpdateLayerState();

    private void UpdateLayerState()
    {
        Model.Blend = Blend;
        Model.Bypass = Bypass;
        Model.Solo = Solo;
        Model.TransitionSeconds = TransitionSeconds;
        _render.Post(new SetLayerState(_layerIndex, Blend, Bypass, Solo, TransitionSeconds));
    }
}

public sealed partial class CompositionViewModel : ObservableObject
{
    private readonly RenderThread _render;
    
    public Composition Model { get; }
    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    public ObservableCollection<int> Columns { get; } = [];
    public IReadOnlyList<BlendMode> BlendModes { get; } = Enum.GetValues<BlendMode>();
    public IReadOnlyList<PlayMode> PlayModes { get; } = Enum.GetValues<PlayMode>();
    public IReadOnlyList<AutopilotAction> AutopilotActions { get; } = Enum.GetValues<AutopilotAction>();
    public IReadOnlyList<ResizeMode> ResizeModes { get; } = Enum.GetValues<ResizeMode>();

    [ObservableProperty] private ClipViewModel? _selectedClip;

    /// <summary>Mudanças estruturais (clip definido, layer/coluna adicionada) — usado para marcar o projeto como não salvo (T7.2).</summary>
    public event Action? Changed;

    public CompositionViewModel(Composition model, RenderThread render)
    {
        Model = model;
        _render = render;

        for (int i = 0; i < model.ColumnCount; i++) Columns.Add(i);

        for (int i = 0; i < model.Layers.Count; i++)
        {
            Layers.Add(CreateLayerViewModel(model.Layers[i], i));
        }
    }

    /// <summary>Reconstrói toda a árvore de ViewModels a partir de um novo Composition (abrir projeto — T7.2).</summary>
    public void ReplaceModel(Composition newModel)
    {
        Layers.Clear();
        Columns.Clear();
        SelectedClip = null;

        Model.Width = newModel.Width;
        Model.Height = newModel.Height;
        Model.DefaultImageDuration = newModel.DefaultImageDuration;
        Model.MasterOpacity = newModel.MasterOpacity;
        Model.Blackout = newModel.Blackout;
        Model.ColumnCount = newModel.ColumnCount;
        Model.Layers = newModel.Layers;

        for (int i = 0; i < Model.ColumnCount; i++) Columns.Add(i);
        for (int i = 0; i < Model.Layers.Count; i++) Layers.Add(CreateLayerViewModel(Model.Layers[i], i));

        SyncToEngine();
    }

    /// <summary>Estado atual da grade para capturar uma cena (T7.5): coluna e opacidade ativas por layer.</summary>
    public (Dictionary<Guid, int> ActiveColumns, Dictionary<Guid, double> Opacities) CaptureSceneState()
    {
        var columns = new Dictionary<Guid, int>();
        var opacities = new Dictionary<Guid, double>();
        foreach (var layer in Layers)
        {
            columns[layer.Model.Id] = layer.LastTriggeredColumn;
            opacities[layer.Model.Id] = layer.Opacity;
        }
        return (columns, opacities);
    }

    [RelayCommand]
    private void TriggerColumn(int colIndex)
    {
        _render.Post(new TriggerColumn(colIndex));
    }
    
    public void SyncToEngine()
    {
        _render.Post(new SyncComposition(Model));
    }

    public void TriggerColumnFromShortcut(int columnIndex)
    {
        if (columnIndex >= 0 && columnIndex < Model.ColumnCount)
            _render.Post(new TriggerColumn(columnIndex));
    }

    public void ClearSelectedLayer()
    {
        if (SelectedClip is not null)
            _render.Post(new ClearLayer(SelectedClip.LayerIndex));
    }

    public void ClearAll() => _render.Post(new ClearAll());

    public void SetClip(int layerIndex, int columnIndex, Clip clip)
    {
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count ||
            columnIndex < 0 || columnIndex >= Model.ColumnCount)
            throw new ArgumentOutOfRangeException();

        Model.Layers[layerIndex].Clips[columnIndex] = clip;
        Layers[layerIndex].Clips[columnIndex].SetModel(clip);
        SelectedClip = Layers[layerIndex].Clips[columnIndex];
        _render.Post(new UpdateClip(layerIndex, columnIndex, clip));
        Changed?.Invoke();
    }

    private LayerViewModel CreateLayerViewModel(Layer layer, int layerIndex)
        => new(layer, layerIndex, _render, SelectClip, OnClipChanged);

    private void SelectClip(ClipViewModel clip) => SelectedClip = clip;

    private void OnClipChanged(ClipViewModel clip)
    {
        if (clip.Model is null)
            return;

        Model.Layers[clip.LayerIndex].Clips[clip.ColumnIndex] = clip.Model;
        _render.Post(new UpdateClip(clip.LayerIndex, clip.ColumnIndex, clip.Model));
    }

    public void SetFiles(int layerIndex, int startColumn, IEnumerable<string> files)
    {
        int column = startColumn;
        foreach (var file in files)
        {
            if (column >= Model.ColumnCount)
                break;

            var extension = Path.GetExtension(file).ToLowerInvariant();
            SourceDescriptor source = extension is ".png" or ".jpg" or ".jpeg" or ".bmp"
                ? new FileImageSource(file)
                : new FileVideoSource(file);
            SetClip(layerIndex, column++, new Clip(source) { Name = Path.GetFileName(file) });
        }
    }

    /// <summary>
    /// Fase 8 T8.6: adiciona um arquivo (de uma pasta monitorada) na primeira coluna
    /// vazia da layer; se não houver, cria uma coluna nova. Usado pelo watch-folder —
    /// nunca sobrescreve um clip já configurado manualmente.
    /// </summary>
    public void AddFileAutoColumn(int layerIndex, string path)
    {
        if (layerIndex < 0 || layerIndex >= Layers.Count)
            return;

        int column = Array.FindIndex(Model.Layers[layerIndex].Clips, c => c is null);
        if (column < 0)
        {
            AddColumn();
            column = Model.ColumnCount - 1;
        }
        SetFiles(layerIndex, column, [path]);
    }

    [RelayCommand]
    private void AddLayer()
    {
        var layer = new Layer { Name = $"Layer {Model.Layers.Count + 1}" };
        Model.AddLayer(layer);
        Layers.Add(CreateLayerViewModel(layer, Layers.Count));
        SyncToEngine();
        Changed?.Invoke();
    }

    [RelayCommand]
    private void AddColumn()
    {
        Model.ColumnCount++;
        Columns.Add(Model.ColumnCount - 1);
        foreach (var layer in Layers)
            layer.AddClip(new ClipViewModel(null, layer.LayerIndex, Model.ColumnCount - 1, _render,
                SelectClip, OnClipChanged, clip => layer.LastTriggeredColumn = clip.ColumnIndex));
        SyncToEngine();
        Changed?.Invoke();
    }
}
