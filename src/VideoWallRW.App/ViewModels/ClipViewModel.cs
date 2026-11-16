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

}
