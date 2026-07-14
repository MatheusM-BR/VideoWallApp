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

    /// <summary>Esta célula é a "conectada" da layer (última disparada) — destaque
    /// visual estilo Resolume. Mantido pela LayerViewModel via LastTriggeredColumn.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Célula selecionada para edição (clique no nome) — não confundir com
    /// <see cref="IsActive"/> (no ar). Mantido pela CompositionViewModel.</summary>
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _thumbnailPath;
    [ObservableProperty] private PlayMode _playMode;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private AutopilotAction _autopilot;
    [ObservableProperty] private string _inPointText = string.Empty;
    [ObservableProperty] private string _outPointText = string.Empty;

    /// <summary>Duração total do arquivo (s); 0 = desconhecida/não é vídeo. Alimenta a TrimBar.</summary>
    [ObservableProperty] private double _duration;

    /// <summary>Frame extraído durante o scrub da TrimBar (null = sem scrub ativo).</summary>
    [ObservableProperty] private string? _scrubFramePath;

    /// <summary>Posição atual de reprodução (s) quando este clip está no ar; -1 = parado.
    /// Alimentado pelos stats do engine (~2x/s) — vira a agulha da TrimBar.</summary>
    [ObservableProperty] private double _playheadSeconds = -1;

    /// <summary>Instante do scrub vindo da TrimBar (-1 = parado). Debounce de 120ms
    /// antes de extrair o frame — arrastar rápido não enfileira decodificações.</summary>
    private double _scrubSeconds = -1;
    private int _scrubRequestId;

    public double ScrubSeconds
    {
        get => _scrubSeconds;
        set
        {
            _scrubSeconds = value;
            if (value < 0)
            {
                ScrubFramePath = null; // soltou a alça — preview volta pro thumbnail
                return;
            }
            _ = ExtractScrubFrameAsync(value);
        }
    }

    private async Task ExtractScrubFrameAsync(double seconds)
    {
        if (MediaPathOf(Model) is not { } path)
            return;

        int requestId = ++_scrubRequestId;
        await Task.Delay(120); // debounce: só extrai quando a alça "assenta"
        if (requestId != _scrubRequestId || _scrubSeconds < 0)
            return;

        string? frame = await Task.Run(() => VideoWallRW.Media.FrameExtractor.TryExtractFrame(path, seconds));
        if (frame is not null && requestId == _scrubRequestId && _scrubSeconds >= 0)
            ScrubFramePath = frame;
    }

    /// <summary>O que o painel PREVIEW mostra: frame do scrub quando arrastando, senão o thumbnail.</summary>
    public string? PreviewImagePath => ScrubFramePath ?? ThumbnailPath;

    partial void OnScrubFramePathChanged(string? value) => OnPropertyChanged(nameof(PreviewImagePath));
    partial void OnThumbnailPathChanged(string? value) => OnPropertyChanged(nameof(PreviewImagePath));
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

        RequestThumbnail();
        ProbeDurationIfNeeded();
    }

    /// <summary>Ponte pro TrimBar: In efetivo em segundos (campo vazio = 0).</summary>
    public double TrimIn
    {
        get => TryParseOptional(InPointText, out double? v) && v is { } x ? x : 0;
        set => InPointText = value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>Ponte pro TrimBar: Out efetivo em segundos (campo vazio = duração total).</summary>
    public double TrimOut
    {
        get => TryParseOptional(OutPointText, out double? v) && v is { } x ? x : Duration;
        set => OutPointText = value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Sonda a duração do vídeo uma vez (persistida no Clip) e, quando conhecida,
    /// mostra os tempos inicial/final nos campos In/Out — o operador vê 0 e a duração
    /// total imediatamente, como no Resolume, em vez de campos vazios.
    /// </summary>
    private async void ProbeDurationIfNeeded()
    {
        if (Model?.Source is not FileVideoSource video)
        {
            Duration = 0;
            return;
        }

        if (Model.DurationSeconds is { } known)
        {
            Duration = known;
            EnsureTrimDefaults();
            return;
        }

        string path = video.Path;
        double? probed = await Task.Run(() => VideoWallRW.Media.MediaProbe.TryGetDuration(path));
        if (probed is not { } duration || MediaPathOf(Model) != path || Model is null)
            return;

        Model.DurationSeconds = duration;
        Duration = duration;
        EnsureTrimDefaults();
        _changed(this); // persiste DurationSeconds no engine/projeto mesmo sem edição manual
    }

    private void EnsureTrimDefaults()
    {
        if (Duration <= 0) return;
        if (string.IsNullOrWhiteSpace(InPointText))
            InPointText = "0";
        if (string.IsNullOrWhiteSpace(OutPointText))
            OutPointText = Duration.ToString("0.##", CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(TrimIn));
        OnPropertyChanged(nameof(TrimOut));
    }

    private static string? MediaPathOf(Clip? model) => model?.Source switch
    {
        FileVideoSource v => v.Path,
        FileImageSource i => i.Path,
        _ => null,
    };

    private void RequestThumbnail()
    {
        string? path = MediaPathOf(Model);
        ThumbnailPath = null;
        if (path is null)
            return;

        ThumbnailProvider.Request(path, thumb =>
        {
            // O usuário pode ter trocado a mídia da célula enquanto a miniatura era
            // gerada — só aplica se o clip ainda aponta pro mesmo arquivo.
            if (MediaPathOf(Model) == path)
                ThumbnailPath = thumb;
        });
    }

    // Usado p/ injetar novo Model caso Drag&Drop ocorra
    public void SetModel(Clip model)
    {
        Model = model;
        UpdateFromModel();
    }

    /// <summary>Limpa o conteúdo da célula (menu "Limpar célula" / limpar coluna).</summary>
    public void ClearModel()
    {
        Model = null;
        IsActive = false;
        UpdateFromModel();
    }

    /// <summary>Clique no preview (estilo Resolume): seleciona E coloca no ar.</summary>
    [RelayCommand]
    private void Trigger()
    {
        _select(this);
        if (!HasMedia) return;
        _render.Post(new TriggerClip(_layerIndex, _colIndex));
        _triggered?.Invoke(this);
    }

    /// <summary>Clique no nome (estilo Resolume): só seleciona para edição, sem tocar.</summary>
    [RelayCommand]
    private void Select() => _select(this);

    partial void OnNameChanged(string value) => ApplyEdits();
    partial void OnPlayModeChanged(PlayMode value) => ApplyEdits();
    partial void OnSpeedChanged(double value) => ApplyEdits();
    partial void OnAutopilotChanged(AutopilotAction value) => ApplyEdits();
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(TrimOut));

    partial void OnInPointTextChanged(string value)
    {
        OnPropertyChanged(nameof(TrimIn));
        ApplyEdits();
    }

    partial void OnOutPointTextChanged(string value)
    {
        OnPropertyChanged(nameof(TrimOut));
        ApplyEdits();
    }
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

    /// <summary>Autopilot rápido da layer: escolher aqui aplica a TODOS os clips da
    /// layer de uma vez (o operador monta a playlist sem abrir clip por clip).</summary>
    [ObservableProperty] private AutopilotAction _layerAutopilot;

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
        // Reflete o autopilot atual se todos os clips da layer concordam (sem disparar aplicação).
        _layerAutopilot = model.Clips.FirstOrDefault(c => c is not null)?.Autopilot ?? AutopilotAction.None;

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
        // Consistência: sem isso, capturar uma cena depois de limpar a layer ainda
        // gravava a coluna antiga como ativa.
        LastTriggeredColumn = -1;
    }

    partial void OnLastTriggeredColumnChanged(int value)
    {
        foreach (var clip in Clips)
            clip.IsActive = clip.ColumnIndex == value;
    }

    // Renomear layer pela UI (o engine não usa o nome; basta persistir no modelo).
    partial void OnNameChanged(string value) => Model.Name = value;

    partial void OnLayerAutopilotChanged(AutopilotAction value)
    {
        foreach (var clip in Clips)
            if (clip.Model is not null)
                clip.Autopilot = value; // dispara ApplyEdits → UpdateClip no engine, clip a clip
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
        if (SelectedClip is not null)
            SelectedClip.IsSelected = false;
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

    /// <summary>Distribui as posições de reprodução vindas dos stats do engine para as
    /// células certas (agulha da TrimBar). Chamado na UI thread ~2x/s.</summary>
    public void UpdatePlayheads(IReadOnlyList<VideoWallRW.Core.Stats.LayerClipPosition> positions)
    {
        foreach (var layer in Layers)
            foreach (var clip in layer.Clips)
            {
                var match = positions.FirstOrDefault(p =>
                    p.LayerIndex == layer.LayerIndex && clip.Model?.Id == p.ClipId);
                clip.PlayheadSeconds = match?.Playhead ?? -1;
            }
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
        if (colIndex < 0 || colIndex >= Model.ColumnCount) return;
        _render.Post(new TriggerColumn(colIndex));
        MarkColumnTriggered(colIndex);
    }

    public void SyncToEngine()
    {
        _render.Post(new SyncComposition(Model));
    }

    public void TriggerColumnFromShortcut(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= Model.ColumnCount) return;
        _render.Post(new TriggerColumn(columnIndex));
        MarkColumnTriggered(columnIndex);
    }

    /// <summary>
    /// Consistência: o engine, ao disparar uma coluna, toca o slot com mídia e limpa
    /// os vazios — o rastreio da UI (LastTriggeredColumn, usado por cenas e pelo
    /// destaque de célula ativa) precisa espelhar exatamente isso. Antes, disparos
    /// por coluna/atalho não atualizavam o rastreio e a cena capturada saía errada.
    /// </summary>
    private void MarkColumnTriggered(int colIndex)
    {
        foreach (var layer in Layers)
            layer.LastTriggeredColumn =
                layer.Model.Clips[colIndex]?.Source is null ? -1 : colIndex;
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

        SnapshotForUndo(); // sobrescrever uma célula é desfazível
        Model.Layers[layerIndex].Clips[columnIndex] = clip;
        Layers[layerIndex].Clips[columnIndex].SetModel(clip);
        SelectClip(Layers[layerIndex].Clips[columnIndex]);
        _render.Post(new UpdateClip(layerIndex, columnIndex, clip));
        Changed?.Invoke();
    }

    private LayerViewModel CreateLayerViewModel(Layer layer, int layerIndex)
        => new(layer, layerIndex, _render, SelectClip, OnClipChanged);

    // ── Desfazer (grade): snapshot da composição antes de cada operação destrutiva ──
    private readonly Stack<Composition> _gridUndoStack = new();
    private const int MaxGridUndo = 10;

    public bool CanUndoGrid => _gridUndoStack.Count > 0;

    private void SnapshotForUndo()
    {
        _gridUndoStack.Push(Model.Clone());
        while (_gridUndoStack.Count > MaxGridUndo)
        {
            var arr = _gridUndoStack.ToArray();
            _gridUndoStack.Clear();
            for (int i = arr.Length - 2; i >= 0; i--) _gridUndoStack.Push(arr[i]);
        }
        OnPropertyChanged(nameof(CanUndoGrid));
    }

    /// <summary>Restaura a grade ao estado anterior à última ação (limpar/remover/
    /// clonar coluna, duplicar layer, definir/limpar célula...).</summary>
    [RelayCommand]
    private void UndoGrid()
    {
        if (_gridUndoStack.Count == 0) return;
        ReplaceModel(_gridUndoStack.Pop());
        OnPropertyChanged(nameof(CanUndoGrid));
        Changed?.Invoke();
    }

    // ── Operações de coluna (menu de contexto no cabeçalho) ─────────────────────

    /// <summary>Limpa o conteúdo (imagens/vídeos) de todas as células da coluna.</summary>
    public void ClearColumn(int col)
    {
        if (col < 0 || col >= Model.ColumnCount) return;
        SnapshotForUndo();
        for (int l = 0; l < Model.Layers.Count; l++)
            ClearCellInternal(l, col);
        Changed?.Invoke();
    }

    public void RemoveColumn(int col)
    {
        if (col < 0 || col >= Model.ColumnCount || Model.ColumnCount <= 1) return;
        SnapshotForUndo();
        Model.ColumnCount--;
        foreach (var layer in Model.Layers)
        {
            var clips = layer.Clips.ToList();
            clips.RemoveAt(col);
            layer.Clips = [.. clips];
        }
        ReplaceModel(Model);
        Changed?.Invoke();
    }

    public void CloneColumn(int col) => InsertColumnInternal(col + 1, cloneFrom: col);

    public void InsertColumnBefore(int col) => InsertColumnInternal(col, cloneFrom: null);

    public void InsertColumnAfter(int col) => InsertColumnInternal(col + 1, cloneFrom: null);

    private void InsertColumnInternal(int index, int? cloneFrom)
    {
        index = System.Math.Clamp(index, 0, Model.ColumnCount);
        SnapshotForUndo();
        Model.ColumnCount++;
        foreach (var layer in Model.Layers)
        {
            var clips = layer.Clips.ToList();
            clips.Insert(index, cloneFrom is { } src ? layer.Clips[src]?.Clone() : null);
            layer.Clips = [.. clips];
        }
        ReplaceModel(Model);
        Changed?.Invoke();
    }

    // ── Layer: duplicar ─────────────────────────────────────────────────────────

    public void DuplicateLayer(int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count) return;
        SnapshotForUndo();
        var clone = Model.Layers[layerIndex].Clone();
        clone.Name = $"{Model.Layers[layerIndex].Name} (cópia)";
        Model.Layers.Insert(layerIndex + 1, clone);
        ReplaceModel(Model);
        Changed?.Invoke();
    }

    // ── Célula: limpar conteúdo ─────────────────────────────────────────────────

    public void ClearCell(int layerIndex, int columnIndex)
    {
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count ||
            columnIndex < 0 || columnIndex >= Model.ColumnCount) return;
        SnapshotForUndo();
        ClearCellInternal(layerIndex, columnIndex);
        Changed?.Invoke();
    }

    private void ClearCellInternal(int layerIndex, int columnIndex)
    {
        // Se a célula limpa era a que estava no ar, tira a layer do ar também.
        bool wasActive = Layers[layerIndex].LastTriggeredColumn == columnIndex;

        Model.Layers[layerIndex].Clips[columnIndex] = null;
        Layers[layerIndex].Clips[columnIndex].ClearModel();
        _render.Post(new UpdateClip(layerIndex, columnIndex, null));
        if (wasActive)
        {
            _render.Post(new ClearLayer(layerIndex));
            Layers[layerIndex].LastTriggeredColumn = -1;
        }
    }

    // ── Copy/paste de clip entre células (Ctrl+C / Ctrl+V com uma célula selecionada) ──
    private Clip? _clipboardClip;

    public bool CopySelectedClip()
    {
        if (SelectedClip?.Model is not { } model)
            return false;
        _clipboardClip = model.Copy();
        return true;
    }

    public bool PasteClipboardClip()
    {
        if (_clipboardClip is null || SelectedClip is null)
            return false;
        // Clone() gera um Id novo — célula colada é um clip independente (retrigger
        // e autopilot não confundem as duas cópias).
        SetClip(SelectedClip.LayerIndex, SelectedClip.ColumnIndex, _clipboardClip.Clone());
        return true;
    }

    private void SelectClip(ClipViewModel clip)
    {
        if (SelectedClip is not null)
            SelectedClip.IsSelected = false;
        SelectedClip = clip;
        clip.IsSelected = true;
    }

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
