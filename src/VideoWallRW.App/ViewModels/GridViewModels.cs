using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Model;
using VideoWallRW.Engine;

namespace VideoWallRW.App.ViewModels;

public sealed partial class ClipViewModel : ObservableObject, System.ComponentModel.IDataErrorInfo
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

    [ObservableProperty] private string _name = "Vazio";
    [ObservableProperty] private bool _hasMedia;

    /// <summary>Esta célula é a "conectada" da layer (última disparada) — destaque
    /// visual estilo Resolume. Mantido pela LayerViewModel via LastTriggeredColumn.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Célula selecionada para edição (clique no nome) — não confundir com
    /// <see cref="IsActive"/> (no ar). Mantido pela CompositionViewModel.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Marcada com Ctrl+clique pra formar um grupo de troca (T20) — separado
    /// de <see cref="IsSelected"/>, que é só a célula em edição no painel CLIP.</summary>
    [ObservableProperty] private bool _isMultiSelected;

    /// <summary>Grupo de troca desta célula (null = independente) — espelha Model.LinkGroupId.</summary>
    [ObservableProperty] private Guid? _linkGroupId;
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

    public double TimelineSeekSeconds
    {
        get => PlayheadSeconds;
        set
        {
            if (Model is null) return;
            _render.Post(new SeekClip(_layerIndex, _colIndex, value));
            PlayheadSeconds = value;
        }
    }

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

    /// <summary>Dimensões nativas (px) do arquivo de origem — sondadas uma vez (null =
    /// ainda não sondado ou sem mídia). Base pro cálculo de EffectiveWidth/Height.</summary>
    [ObservableProperty] private int? _sourceWidth;
    [ObservableProperty] private int? _sourceHeight;

    /// <summary>Largura/altura EFETIVA em pixel (nativa × escala) — o que o +/- e o
    /// slide "volante" da aba Clip mexem de verdade, não um passo abstrato de escala.</summary>
    public int? EffectiveWidth => SourceWidth is { } w ? (int)Math.Round(w * ScaleX) : null;
    public int? EffectiveHeight => SourceHeight is { } h ? (int)Math.Round(h * ScaleY) : null;
    partial void OnSourceWidthChanged(int? value) => OnPropertyChanged(nameof(EffectiveWidth));
    partial void OnSourceHeightChanged(int? value) => OnPropertyChanged(nameof(EffectiveHeight));

    // ── Transform manual (painel CLIP interativo — TransformGizmo) ──────────────
    [ObservableProperty] private double _positionX;
    [ObservableProperty] private double _positionY;
    [ObservableProperty] private double _scaleX = 1.0;
    [ObservableProperty] private double _scaleY = 1.0;

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
        Name = Model?.Name ?? "Vazio";
        PlayMode = Model?.PlayMode ?? PlayMode.Loop;
        Speed = Model?.Speed ?? 1.0;
        Autopilot = Model?.Autopilot ?? AutopilotAction.None;
        InPointText = FormatOptional(Model?.InPoint);
        OutPointText = FormatOptional(Model?.OutPoint);
        ImageDurationText = FormatOptional(Model?.ImageDuration);
        Resize = Model?.Resize;
        Volume = Model?.Volume ?? 1.0;
        Mute = Model?.Mute ?? false;
        PositionX = Model?.PositionX ?? 0.0;
        PositionY = Model?.PositionY ?? 0.0;
        ScaleX = Model?.ScaleX ?? 1.0;
        ScaleY = Model?.ScaleY ?? 1.0;
        // O vínculo é da CÉLULA (existe até vazia), não da mídia — então só ADOTA o
        // grupo quando o Model traz um, nunca zera um vínculo de VM já existente por
        // causa de um Model vazio/sem grupo (ex.: limpar célula vinculada mantém o
        // vínculo pronto pra próxima mídia; célula vazia pode ser vinculada).
        if (Model?.LinkGroupId is { } group)
            LinkGroupId = group;
        _updatingFromModel = false;

        RequestThumbnail();
        ProbeDurationIfNeeded();
        ProbeDimensionsIfNeeded();
    }

    /// <summary>Ponte pro TrimBar: In efetivo em segundos (campo vazio = 0).</summary>
    public double TrimIn
    {
        get => !HasValidTrim ? Model?.InPoint ?? 0 : TryParseOptional(InPointText, out double? v) && v is { } x ? x : 0;
        set => InPointText = value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>Ponte pro TrimBar: Out efetivo em segundos (campo vazio = duração total).</summary>
    public double TrimOut
    {
        get => !HasValidTrim ? Model?.OutPoint ?? Duration : TryParseOptional(OutPointText, out double? v) && v is { } x ? x : Duration;
        set => OutPointText = value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Portão das sondas: ao abrir um projeto, TODAS as células sondam duração/dimensão
    /// ao mesmo tempo — dezenas de aberturas nativas simultâneas (FFmpeg/WIC) no pool de
    /// threads. É a mesma classe de corrida que corrompia o heap na criação de fontes,
    /// então aqui também roda uma sonda por vez. Não bloqueia a UI (tudo await).
    /// </summary>
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);

    private static async Task<T?> RunProbeAsync<T>(Func<T> probe)
    {
        // Espera limitada: um arquivo em pasta de REDE que saiu do ar faz o FFmpeg/WIC
        // travar por dezenas de segundos segurando o portão. Sem teto, uma única sonda
        // presa congelaria todas as outras da sessão (duração e dimensão de TODA a
        // grade). Passado o limite, esta sonda desiste — o valor é opcional e será
        // sondado de novo na próxima vez que a célula for tocada.
        if (!await ProbeGate.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false))
            return default;
        try { return await Task.Run(probe).ConfigureAwait(false); }
        finally { ProbeGate.Release(); }
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
        double? probed = await RunProbeAsync(() => VideoWallRW.Media.MediaProbe.TryGetDuration(path));
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

    /// <summary>
    /// Sonda as dimensões nativas do arquivo (vídeo via MediaDecoder leve, imagem via
    /// header do WIC/BitmapFrame — sem decodificar pixels) — mostradas na aba Clip
    /// abaixo do combo Resize.
    /// </summary>
    private async void ProbeDimensionsIfNeeded()
    {
        string? path = MediaPathOf(Model);
        if (path is null)
        {
            SourceWidth = null;
            SourceHeight = null;
            return;
        }

        bool isVideo = Model?.Source is FileVideoSource;
        var dims = await RunProbeAsync(() => isVideo
            ? VideoWallRW.Media.MediaProbe.TryGetVideoDimensions(path)
            : TryGetImageDimensions(path));

        if (dims is not { } d || MediaPathOf(Model) != path)
            return;
        SourceWidth = d.Width;
        SourceHeight = d.Height;
    }

    private static (int Width, int Height)? TryGetImageDimensions(string path)
    {
        try
        {
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(path),
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    // Largura/altura efetivas em PIXEL (T24) — o slide "volante" mexe no tamanho real
    // exibido, não num passo abstrato de ScaleX/Y; a escala é só o meio de chegar lá
    // (largura efetiva ÷ largura nativa).
    private void SetEffectiveWidth(double px)
    {
        if (SourceWidth is not { } w || w <= 0) return;
        ScaleX = Math.Max(0.05, Math.Round(px / w, 4));
    }

    private void SetEffectiveHeight(double px)
    {
        if (SourceHeight is not { } h || h <= 0) return;
        ScaleY = Math.Max(0.05, Math.Round(px / h, 4));
    }

    // ── Slide "volante" (T24): sempre centralizado, arrastar aplica uma taxa contínua
    // enquanto segura (rate em -1..1) — o CenterSnapSlider chama isso a cada tick do
    // seu timer interno enquanto o operador segura o arraste, e centraliza sozinho ao soltar.
    private const double JogPxPerTick = 6;

    [RelayCommand]
    private void JogWidth(object? rate)
    {
        if (rate is not double r || r == 0) return;
        SetEffectiveWidth(Math.Max(1, (EffectiveWidth ?? 0) + r * JogPxPerTick));
    }

    [RelayCommand]
    private void JogHeight(object? rate)
    {
        if (rate is not double r || r == 0) return;
        SetEffectiveHeight(Math.Max(1, (EffectiveHeight ?? 0) + r * JogPxPerTick));
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
    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(TrimOut));
        OnPropertyChanged(nameof(TimingError));
        OnPropertyChanged(nameof(HasTimingError));
    }

    private bool HasValidTrim => this[nameof(InPointText)].Length == 0 && this[nameof(OutPointText)].Length == 0;

    partial void OnInPointTextChanged(string value)
    {
        OnPropertyChanged(nameof(TrimIn));
        OnPropertyChanged(nameof(OutPointText));
        OnPropertyChanged(nameof(TrimOut));
        ApplyEdits();
    }

    partial void OnOutPointTextChanged(string value)
    {
        OnPropertyChanged(nameof(TrimOut));
        OnPropertyChanged(nameof(InPointText));
        OnPropertyChanged(nameof(TrimIn));
        ApplyEdits();
    }
    partial void OnImageDurationTextChanged(string value) => ApplyEdits();
    partial void OnResizeChanged(ResizeMode? value) => ApplyEdits();
    partial void OnVolumeChanged(double value) => ApplyEdits();
    partial void OnMuteChanged(bool value) => ApplyEdits();
    partial void OnPositionXChanged(double value) => ApplyEdits();
    partial void OnPositionYChanged(double value) => ApplyEdits();
    partial void OnScaleXChanged(double value) { ApplyEdits(); OnPropertyChanged(nameof(EffectiveWidth)); }
    partial void OnScaleYChanged(double value) { ApplyEdits(); OnPropertyChanged(nameof(EffectiveHeight)); }

    /// <summary>Botão "Redefinir posição/escala" do gizmo.</summary>
    [RelayCommand]
    private void ResetTransform()
    {
        PositionX = 0; PositionY = 0; ScaleX = 1; ScaleY = 1;
    }

    private void ApplyEdits()
    {
        if (_updatingFromModel || Model is null)
            return;

        OnPropertyChanged(nameof(TimingError));
        OnPropertyChanged(nameof(HasTimingError));
        OnPropertyChanged("Item[]");
        // Um tempo inválido conserva os últimos tempos válidos, sem bloquear volume,
        // nome e transformação. Entrada/saída formam um intervalo indivisível.
        if (this[nameof(InPointText)].Length == 0 && this[nameof(OutPointText)].Length == 0)
        {
            TryParseOptional(InPointText, out double? inPoint);
            TryParseOptional(OutPointText, out double? outPoint);
            Model.InPoint = inPoint;
            Model.OutPoint = outPoint;
        }
        if (this[nameof(ImageDurationText)].Length == 0)
        {
            TryParseOptional(ImageDurationText, out double? imageDuration);
            Model.ImageDuration = imageDuration;
        }

        Model.Name = Name;
        Model.PlayMode = PlayMode;
        Model.Speed = Math.Clamp(Speed, 0.1, 4.0);
        Model.Autopilot = Autopilot;
        Model.Resize = Resize;
        Model.Volume = Math.Clamp(Volume, 0.0, 1.0);
        Model.Mute = Mute;
        Model.PositionX = Math.Clamp(PositionX, -2.0, 2.0);
        Model.PositionY = Math.Clamp(PositionY, -2.0, 2.0);
        Model.ScaleX = Math.Clamp(ScaleX, 0.05, 8.0);
        Model.ScaleY = Math.Clamp(ScaleY, 0.05, 8.0);
        _changed(this);
    }

    public string Error => TimingError;
    public bool HasTimingError => TimingError.Length > 0;
    public string TimingError => string.Join(" ", new[] { nameof(InPointText), nameof(OutPointText), nameof(ImageDurationText) }
        .Select(name => this[name]).Where(message => message.Length > 0).Distinct());

    public string this[string columnName]
    {
        get
        {
            string? text = columnName switch
            {
                nameof(InPointText) => InPointText,
                nameof(OutPointText) => OutPointText,
                nameof(ImageDurationText) => ImageDurationText,
                _ => null,
            };
            if (text is null) return "";
            string label = columnName == nameof(InPointText) ? "Entrada" : columnName == nameof(OutPointText) ? "Saída" : "Duração";
            if (!TryParseOptional(text, out double? value)) return $"{label}: informe segundos com um número válido, sem valor negativo.";
            if (columnName == nameof(ImageDurationText))
                return value is <= 0 ? "Duração: use um valor maior que zero ou deixe vazio para usar o padrão." : "";
            if (!TryParseOptional(InPointText, out var start) || !TryParseOptional(OutPointText, out var end)) return "";
            if (end is { } stop && stop <= (start ?? 0)) return "A saída deve ser maior que a entrada.";
            if (Duration > 0 && (value > Duration || columnName == nameof(InPointText) && value >= Duration))
                return $"{label}: o tempo deve ficar dentro dos {Duration:0.##} segundos da mídia.";
            return "";
        }
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

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) && double.IsFinite(parsed) && parsed >= 0)
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

    [ObservableProperty] private string _name = "Camada";
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

    /// <summary>Autopilot ON/OFF da layer: OFF = o que estiver no ar segura (fim de
    /// clip não avança nada). O toggle global da barra liga/desliga todas as layers.</summary>
    [ObservableProperty] private bool _autopilotEnabled = true;

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
                Edited?.Invoke();
            }
        }
    }

    public Layer Model { get; }
    public ObservableCollection<ClipViewModel> Clips { get; } = [];

    public LayerViewModel(Layer model, int layerIndex, RenderThread render,
        Action<ClipViewModel> selectClip, Action<ClipViewModel> clipChanged)
    {
        // Compatibilidade com projetos antigos: o toggle podia estar ON enquanto
        // todos os clips estavam em None, combinação que visualmente prometia avanço
        // mas nunca gerava um evento de fim para o autopilot.
        if (model.AutopilotEnabled && model.Clips.Any(c => c is not null) &&
            model.Clips.Where(c => c is not null).All(c => c!.Autopilot == AutopilotAction.None))
        {
            foreach (var clip in model.Clips)
                if (clip is not null)
                    clip.Autopilot = AutopilotAction.PlayNextClip;
        }
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
        _layerAutopilot = model.Clips.FirstOrDefault(c => c is not null)?.Autopilot ?? AutopilotAction.PlayNextClip;
        _autopilotEnabled = model.AutopilotEnabled;
        _ignoreColumnTrigger = model.IgnoreColumnTrigger;

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

    /// <summary>
    /// Avisa que uma PROPRIEDADE da layer mudou (opacidade, blend, bypass, solo,
    /// nome, autopilot, layer fixa). Diferente do evento estrutural Changed: não
    /// mexe na forma da grade, mas precisa sujar o projeto e entrar na cena aberta —
    /// sem isto, ajustar a layer e fechar o programa perdia o ajuste sem nem
    /// perguntar. Atribuído pela CompositionViewModel.
    /// </summary>
    public Action? Edited { get; set; }

    // Renomear layer pela UI (o engine não usa o nome; basta persistir no modelo).
    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        Edited?.Invoke();
    }

    partial void OnLayerAutopilotChanged(AutopilotAction value)
    {
        foreach (var clip in Clips)
            if (clip.Model is not null)
                clip.Autopilot = value; // dispara ApplyEdits → UpdateClip no engine, clip a clip
    }

    /// <summary>Layer FIXA: fica de fora do disparo/atalho de coluna inteira — pra
    /// linha de entrada ao vivo (câmera NDI) não cair quando o operador troca a coluna.</summary>
    [ObservableProperty] private bool _ignoreColumnTrigger;

    /// <summary>Ressincroniza a composição inteira no engine — atribuído pela
    /// CompositionViewModel. Necessário porque o engine decide pular a layer no
    /// disparo de coluna lendo a própria cópia da composição.</summary>
    public Action? SyncComposition { get; set; }

    partial void OnIgnoreColumnTriggerChanged(bool value)
    {
        Model.IgnoreColumnTrigger = value;
        SyncComposition?.Invoke();
        Edited?.Invoke();
    }

    partial void OnAutopilotEnabledChanged(bool value)
    {
        if (value && Clips.Any(c => c.Model is not null) &&
            Clips.Where(c => c.Model is not null).All(c => c.Autopilot == AutopilotAction.None))
            LayerAutopilot = AutopilotAction.PlayNextClip;
        Model.AutopilotEnabled = value;
        _render.Post(new SetLayerAutopilot(_layerIndex, value));
        Edited?.Invoke();
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
        Edited?.Invoke();
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

    /// <summary>
    /// Edição de PROPRIEDADE de clip ou layer (volume, duração da imagem, resize,
    /// velocidade, in/out, posição, escala, opacidade, blend, layer fixa...). Não
    /// muda a estrutura da grade, então não vale reconstruir combos de roteamento
    /// nem atalhos de coluna — mas TEM que sujar o projeto: essas edições passavam
    /// batido, o título não ganhava o "*", fechar não perguntava nada e o ajuste ia
    /// embora em silêncio. Mantido separado de <see cref="Changed"/> justamente
    /// porque dispara em rajada enquanto o operador arrasta um slider.
    /// </summary>
    public event Action? Edited;

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
    /// <summary>
    /// Vínculo de células VAZIAS (grupo de troca), por "layer:coluna". Célula com mídia
    /// guarda o grupo dentro do próprio Clip e sobrevive sozinha; a vazia não tem onde
    /// guardar, e o ViewModel dela é DESTRUÍDO em toda reconstrução da grade
    /// (ReplaceModel: troca de cena, carregar projeto, desfazer, add/remover coluna ou
    /// layer). Por isso o vínculo das vazias vive aqui, fora dos ViewModels.
    /// </summary>
    private readonly Dictionary<(int Layer, int Column), Guid> _blankCellLinks = [];

    /// <summary>Lê os vínculos das células vazias dos ViewModels atuais pro dicionário —
    /// chamado antes de destruir os VMs.</summary>
    private void CaptureBlankCellLinks()
    {
        _blankCellLinks.Clear();
        foreach (var cell in AllClips())
            if (cell.Model is null && cell.LinkGroupId is { } group)
                _blankCellLinks[(cell.LayerIndex, cell.ColumnIndex)] = group;
    }

    /// <summary>Reaplica os vínculos nas células vazias dos ViewModels recém-criados.</summary>
    private void RestoreBlankCellLinks()
    {
        foreach (var ((layerIndex, columnIndex), group) in _blankCellLinks)
        {
            if (layerIndex < 0 || layerIndex >= Layers.Count) continue;
            var clips = Layers[layerIndex].Clips;
            if (columnIndex < 0 || columnIndex >= clips.Count) continue;
            if (clips[columnIndex].Model is null)
                clips[columnIndex].LinkGroupId = group;
        }
    }

    /// <summary>Desloca os vínculos de células vazias quando uma coluna é inserida ou
    /// removida — sem isso eles ficariam apontando pra coluna errada depois da cirurgia
    /// (os clipes com mídia já se movem sozinhos, porque o grupo vai dentro do Clip).</summary>
    private void ShiftBlankCellLinks(int column, int delta)
    {
        var shifted = new Dictionary<(int, int), Guid>();
        foreach (var ((layer, col), group) in _blankCellLinks)
        {
            if (delta < 0 && col == column) continue;      // coluna removida: vínculo some
            int newCol = col >= column ? col + delta : col;
            if (newCol >= 0) shifted[(layer, newCol)] = group;
        }
        _blankCellLinks.Clear();
        foreach (var kv in shifted) _blankCellLinks[kv.Key] = kv.Value;
    }

    public void ReplaceModel(Composition newModel)
    {
        // Captura antes de destruir os VMs; restaura depois de recriá-los (ver campo).
        // ATENÇÃO: quem já ajustou o dicionário (ShiftBlankCellLinks, ImportBlankCellLinks)
        // sinaliza via _blankLinksPrepared pra não ser sobrescrito por uma captura tardia.
        if (!_blankLinksPrepared) CaptureBlankCellLinks();
        _blankLinksPrepared = false;

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
        // Rede de segurança: projeto salvo por versão antiga (ou caminho que deixou o
        // array fora de sincronia) fica com layers de tamanho diferente do ColumnCount —
        // era a origem do IndexOutOfRange ao remover coluna. Normaliza na entrada.
        Model.NormalizeLayers();

        for (int i = 0; i < Model.ColumnCount; i++) Columns.Add(i);
        for (int i = 0; i < Model.Layers.Count; i++) Layers.Add(CreateLayerViewModel(Model.Layers[i], i));

        RestoreBlankCellLinks();
        SyncToEngine();
    }

    /// <summary>Marca que o dicionário de vínculos vazios já foi preparado por quem
    /// chamou (shift de coluna / import do projeto) — ver ReplaceModel.</summary>
    private bool _blankLinksPrepared;

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

    public void TriggerClipFromShortcut(int layerIndex, int columnIndex)
    {
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count ||
            columnIndex < 0 || columnIndex >= Model.ColumnCount) return;
        _render.Post(new TriggerClip(layerIndex, columnIndex));
        Layers[layerIndex].LastTriggeredColumn =
            Model.Layers[layerIndex].Clips[columnIndex]?.Source is null ? -1 : columnIndex;
    }

    public void MarkClipTriggeredFromEngine(int layerIndex, int columnIndex)
    {
        if (layerIndex < 0 || layerIndex >= Layers.Count) return;
        Layers[layerIndex].LastTriggeredColumn = columnIndex;
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
        {
            // Layer fixa não entra no disparo de coluna (ver Layer.IgnoreColumnTrigger),
            // então o destaque dela também não pode mudar — senão a grade mostraria
            // como "no ar" uma célula que o engine nem disparou.
            if (layer.Model.IgnoreColumnTrigger) continue;
            layer.LastTriggeredColumn =
                layer.Model.Clips[colIndex]?.Source is null ? -1 : colIndex;
        }
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
        // Importar mídia numa célula vinculada (T20/T33) não tira ela do grupo — herda
        // o vínculo do ViewModel da célula (que existe até quando ela estava VAZIA),
        // não do Model antigo (que era null numa célula em branco). É isso que faz
        // "importar numa célula já vinculada" propagar e mostrar a miniatura nas demais.
        var targetVm = Layers[layerIndex].Clips[columnIndex];
        clip.LinkGroupId = targetVm.LinkGroupId;
        Model.Layers[layerIndex].Clips[columnIndex] = clip;
        targetVm.SetModel(clip);
        SelectClip(targetVm);
        _render.Post(new UpdateClip(layerIndex, columnIndex, clip));
        PropagateLinkGroup(targetVm);
        Changed?.Invoke();
    }

    // ── Grupos de troca (T20): células vinculadas trocam juntas ─────────────────

    private IEnumerable<ClipViewModel> AllClips() => Layers.SelectMany(l => l.Clips);

    /// <summary>Copia a célula alterada pras outras do mesmo grupo (mídia + todas as
    /// configurações) — chamado depois de qualquer edição/troca de mídia numa célula
    /// vinculada. O grupo é lido do ViewModel (autoritativo, existe até em célula
    /// vazia), então células vazias vinculadas TAMBÉM recebem a mídia (e a miniatura).</summary>
    private void PropagateLinkGroup(ClipViewModel source)
    {
        if (source.LinkGroupId is not { } groupId || source.Model is null)
            return;

        foreach (var other in AllClips())
        {
            if (ReferenceEquals(other, source) || other.LinkGroupId != groupId)
                continue;

            // Mantém a identidade da célula destino (CloneWithId): trocar o Id a cada
            // propagação fazia o autopilot perder a coluna atual e voltar pra coluna 0.
            var propagated = source.Model.CloneWithId(other.Model?.Id ?? Guid.NewGuid());
            propagated.LinkGroupId = groupId;
            Model.Layers[other.LayerIndex].Clips[other.ColumnIndex] = propagated;
            other.SetModel(propagated);
            _render.Post(new UpdateClip(other.LayerIndex, other.ColumnIndex, propagated));
        }
    }

    /// <summary>Vincula todas as células marcadas (Ctrl+clique) num grupo de troca novo —
    /// precisa de pelo menos 2 células, COM OU SEM mídia (célula vazia entra no grupo e
    /// recebe a mídia quando qualquer uma do grupo for preenchida). Botão direito >
    /// "Vincular células".</summary>
    public void LinkMultiSelectedCells()
    {
        var selected = AllClips().Where(c => c.IsMultiSelected).ToList();
        if (selected.Count < 2)
            return;

        SnapshotForUndo();
        var groupId = Guid.NewGuid();
        foreach (var clip in selected)
        {
            clip.LinkGroupId = groupId;
            clip.IsMultiSelected = false;
            if (clip.Model is not null)
            {
                clip.Model.LinkGroupId = groupId;
                _render.Post(new UpdateClip(clip.LayerIndex, clip.ColumnIndex, clip.Model));
            }
        }
        // Se alguma já tinha mídia, propaga pra igualar o grupo na hora (inclusive
        // pras vazias); se todas estavam vazias, o grupo só espera a primeira mídia.
        var seed = selected.FirstOrDefault(c => c.Model is not null);
        if (seed is not null)
            PropagateLinkGroup(seed);
        Changed?.Invoke();
    }

    /// <summary>Tira uma célula do grupo de troca (as outras continuam vinculadas entre si).
    /// Funciona em célula vazia também (o vínculo é do ViewModel).</summary>
    public void UnlinkCell(ClipViewModel clip)
    {
        if (clip.LinkGroupId is null)
            return;

        SnapshotForUndo();
        clip.LinkGroupId = null;
        if (clip.Model is not null)
        {
            clip.Model.LinkGroupId = null;
            _render.Post(new UpdateClip(clip.LayerIndex, clip.ColumnIndex, clip.Model));
        }
        Changed?.Invoke();
    }

    public void ClearMultiSelection()
    {
        foreach (var clip in AllClips())
            clip.IsMultiSelected = false;
    }

    /// <summary>Vínculos de células VAZIAS pro arquivo do projeto — as com mídia já
    /// guardam o grupo no próprio Clip (ver ProjectMeta.BlankCellLinks).</summary>
    public Dictionary<string, Guid> ExportBlankCellLinks()
    {
        CaptureBlankCellLinks();
        var map = new Dictionary<string, Guid>();
        foreach (var ((layer, column), group) in _blankCellLinks)
            map[$"{layer}:{column}"] = group;
        return map;
    }

    /// <summary>Restaura os vínculos de células vazias ao abrir o projeto.</summary>
    public void ImportBlankCellLinks(Dictionary<string, Guid> map)
    {
        // Zera o vínculo das células vazias ANTES de aplicar o novo mapa: sem isto,
        // trocar de cena deixava as vazias com o grupo da cena anterior (Restore só
        // ESCREVE o que está no mapa, nunca apaga o que não está).
        foreach (var cell in AllClips())
            if (cell.Model is null)
                cell.LinkGroupId = null;

        _blankCellLinks.Clear();
        foreach (var (key, group) in map)
        {
            var parts = key.Split(':');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out int layerIndex) ||
                !int.TryParse(parts[1], out int columnIndex))
                continue;
            _blankCellLinks[(layerIndex, columnIndex)] = group;
        }
        // Aplica já nos ViewModels atuais (o projeto acabou de ser carregado) e mantém
        // no dicionário pra sobreviver às próximas reconstruções da grade.
        RestoreBlankCellLinks();
    }

    private LayerViewModel CreateLayerViewModel(Layer layer, int layerIndex)
    {
        var vm = new LayerViewModel(layer, layerIndex, _render, SelectClip, OnClipChanged);
        vm.SyncComposition = SyncToEngine;   // ver LayerViewModel.IgnoreColumnTrigger
        vm.Edited = () => Edited?.Invoke();  // ajuste de layer também suja o projeto
        return vm;
    }

    // ── Desfazer (grade): snapshot da composição antes de cada operação destrutiva ──
    private readonly Stack<Composition> _gridUndoStack = new();
    private readonly Stack<Composition> _gridRedoStack = new();
    private const int MaxGridUndo = 10;

    public bool CanUndoGrid => _gridUndoStack.Count > 0;
    public bool CanRedoGrid => _gridRedoStack.Count > 0;

    private void SnapshotForUndo()
    {
        _gridUndoStack.Push(Model.Clone());
        _gridRedoStack.Clear();
        while (_gridUndoStack.Count > MaxGridUndo)
        {
            var arr = _gridUndoStack.ToArray();
            _gridUndoStack.Clear();
            for (int i = arr.Length - 2; i >= 0; i--) _gridUndoStack.Push(arr[i]);
        }
        OnPropertyChanged(nameof(CanUndoGrid));
        OnPropertyChanged(nameof(CanRedoGrid));
    }

    /// <summary>Restaura a grade ao estado anterior à última ação (limpar/remover/
    /// clonar coluna, duplicar layer, definir/limpar célula...).</summary>
    [RelayCommand]
    private void UndoGrid()
    {
        if (_gridUndoStack.Count == 0) return;
        _gridRedoStack.Push(Model.Clone());
        ReplaceModel(_gridUndoStack.Pop());
        OnPropertyChanged(nameof(CanUndoGrid));
        OnPropertyChanged(nameof(CanRedoGrid));
        Changed?.Invoke();
    }

    [RelayCommand]
    private void RedoGrid()
    {
        if (_gridRedoStack.Count == 0) return;
        _gridUndoStack.Push(Model.Clone());
        ReplaceModel(_gridRedoStack.Pop());
        OnPropertyChanged(nameof(CanUndoGrid));
        OnPropertyChanged(nameof(CanRedoGrid));
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
        // Ordem importa: o setter de Model.ColumnCount redimensiona (Array.Resize) o
        // array Clips de TODAS as layers pro novo tamanho — se isso rodasse antes do
        // RemoveAt, o array já teria encolhido cortando o ÚLTIMO slot (não o "col"
        // pedido) e, ao remover justo a última coluna, o RemoveAt(col) apontaria pra
        // um índice que não existe mais (era o bug real por trás de "deletar a última
        // coluna trava o app"). Por isso o RemoveAt manual roda primeiro, com o array
        // ainda no tamanho antigo, e só then o ColumnCount-- (que vira um no-op de
        // resize, já que os arrays já estão no tamanho certo).
        foreach (var layer in Model.Layers)
        {
            var clips = layer.Clips.ToList();
            // O array de uma layer pode estar DESSINCRONIZADO do ColumnCount (projeto
            // antigo, layer criada por caminho que não normalizou) — o guard lá em cima
            // valida contra ColumnCount, não contra este array. Sem este if, remover a
            // última coluna com um array mais curto estourava IndexOutOfRange e, fora
            // da UI thread do handler global, derrubava o processo inteiro (crash real
            // capturado no Event Log em 16/07 10:05).
            if (col < clips.Count)
                clips.RemoveAt(col);
            while (clips.Count > Model.ColumnCount - 1)
                clips.RemoveAt(clips.Count - 1);
            while (clips.Count < Model.ColumnCount - 1)
                clips.Add(null);
            layer.Clips = [.. clips];
        }
        CaptureBlankCellLinks();
        ShiftBlankCellLinks(col, -1);   // vínculos das colunas seguintes andam pra trás
        _blankLinksPrepared = true;
        Model.ColumnCount--;
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
        // Mesma questão de ordem do RemoveColumn: o Insert manual roda com o array
        // ainda no tamanho antigo (senão o ColumnCount++ já apenderia um slot extra no
        // fim antes do Insert, deixando o array 2 posições maior que ColumnCount).
        foreach (var layer in Model.Layers)
        {
            var clips = layer.Clips.ToList();
            // Mesma defesa do RemoveColumn: o array desta layer pode estar mais curto
            // que ColumnCount — normaliza antes pro Insert/índice nunca estourar.
            while (clips.Count < Model.ColumnCount)
                clips.Add(null);
            var cloned = cloneFrom is { } src && src < clips.Count ? clips[src]?.Clone() : null;
            clips.Insert(System.Math.Min(index, clips.Count), cloned);
            layer.Clips = [.. clips];
        }
        CaptureBlankCellLinks();
        ShiftBlankCellLinks(index, +1);  // abre espaço: vínculos daqui pra frente avançam
        _blankLinksPrepared = true;
        Model.ColumnCount++;
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

        // Guarda contra ClipViewModel "órfão": uma sonda assíncrona (duração/dimensões,
        // ProbeDurationIfNeeded/ProbeDimensionsIfNeeded) pode terminar DEPOIS que a
        // coluna/layer dela já foi removida (RemoveColumn/RemoveLayer encolhem os
        // arrays) — sem isso, o callback tentava escrever num índice que não existe
        // mais e derrubava o app inteiro (IndexOutOfRangeException na UI thread, fora
        // do try/catch da render thread).
        if (clip.LayerIndex < 0 || clip.LayerIndex >= Model.Layers.Count)
            return;
        var clips = Model.Layers[clip.LayerIndex].Clips;
        if (clip.ColumnIndex < 0 || clip.ColumnIndex >= clips.Length)
            return;

        clips[clip.ColumnIndex] = clip.Model;
        _render.Post(new UpdateClip(clip.LayerIndex, clip.ColumnIndex, clip.Model));
        PropagateLinkGroup(clip);
        Edited?.Invoke();
    }

    private const int MaxRecentSources = 20;

    /// <summary>Últimos arquivos enviados pra grade, mais recente primeiro — aba Sources.</summary>
    public ObservableCollection<string> RecentSources { get; } = [];

    public void SetFiles(int layerIndex, int startColumn, IEnumerable<string> files)
    {
        int column = startColumn;
        foreach (var file in files)
        {
            if (column >= Model.ColumnCount)
                break;

            var extension = Path.GetExtension(file).ToLowerInvariant();
            bool isImage = extension is ".png" or ".jpg" or ".jpeg" or ".bmp";
            SourceDescriptor source = isImage
                ? new FileImageSource(file)
                : new FileVideoSource(file);
            SetClip(layerIndex, column++, new Clip(source)
            {
                Name = Path.GetFileName(file),
                // Padrão da operação: imagem fica 7s no ar antes do autopilot avançar.
                ImageDuration = isImage ? 7 : null,
                // Todo clip novo entra em Fit: ocupa o máximo da composição respeitando
                // a proporção original, sem cortar nem esticar (pedido do operador).
                Resize = ResizeMode.Fit,
            });

            // Aba Sources > Recentes: só imagens (geralmente recortadas no editor) —
            // vídeo é conteúdo pesado/específico de cada leilão, não faz sentido
            // reaproveitar como "fonte recente" do mesmo jeito que um banner/logo.
            if (isImage)
            {
                RecentSources.Remove(file);
                RecentSources.Insert(0, file);
                while (RecentSources.Count > MaxRecentSources)
                    RecentSources.RemoveAt(RecentSources.Count - 1);
            }
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
        var layer = new Layer { Name = $"Camada {Model.Layers.Count + 1}" };
        Model.AddLayer(layer);
        Layers.Add(CreateLayerViewModel(layer, Layers.Count));
        SyncToEngine();
        Changed?.Invoke();
    }

    /// <summary>Remove a layer inteira (botão direito no painel da layer) — mantém pelo
    /// menos uma layer. Reconstrói os ViewModels pra renumerar os índices das demais.</summary>
    public void RemoveLayer(int layerIndex)
    {
        if (Model.Layers.Count <= 1) return;
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count) return;

        SnapshotForUndo();
        Model.Layers.RemoveAt(layerIndex);
        if (SelectedClip is not null)
        {
            SelectedClip.IsSelected = false;
            SelectedClip = null;
        }
        Layers.Clear();
        for (int i = 0; i < Model.Layers.Count; i++)
            Layers.Add(CreateLayerViewModel(Model.Layers[i], i));
        SyncToEngine();
        Changed?.Invoke();
    }

    [RelayCommand]
    private void AddColumn()
    {
        // SetColumnCount (não o setter puro): aqui NÃO há cirurgia manual no array —
        // é o próprio modelo que precisa crescer os slots de todas as layers.
        Model.SetColumnCount(Model.ColumnCount + 1);
        Columns.Add(Model.ColumnCount - 1);
        foreach (var layer in Layers)
            layer.AddClip(new ClipViewModel(null, layer.LayerIndex, Model.ColumnCount - 1, _render,
                SelectClip, OnClipChanged, clip => layer.LastTriggeredColumn = clip.ColumnIndex));
        SyncToEngine();
        Changed?.Invoke();
    }
}
