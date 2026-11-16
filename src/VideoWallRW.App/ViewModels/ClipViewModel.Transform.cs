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

public sealed partial class ClipViewModel
{
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
    // async void só como ponte de evento: toda exceção fica aqui dentro, logada —
    // nunca sobe pro dispatcher no meio de um evento ao vivo.
    private async void ProbeDurationIfNeeded()
    {
        try { await ProbeDurationCoreAsync(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Falha na sonda de mídia (ProbeDuration)"); }
    }

    private async Task ProbeDurationCoreAsync()
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
    // async void só como ponte de evento: toda exceção fica aqui dentro, logada —
    // nunca sobe pro dispatcher no meio de um evento ao vivo.
    private async void ProbeDimensionsIfNeeded()
    {
        try { await ProbeDimensionsCoreAsync(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Falha na sonda de mídia (ProbeDimensions)"); }
    }

    private async Task ProbeDimensionsCoreAsync()
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
