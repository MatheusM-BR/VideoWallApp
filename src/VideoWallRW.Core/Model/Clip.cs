namespace VideoWallRW.Core.Model;

public sealed class Clip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public SourceDescriptor Source { get; set; }

    public PlayMode PlayMode { get; set; } = PlayMode.Loop;
    public double Speed { get; set; } = 1.0;
    public double? InPoint { get; set; }
    public double? OutPoint { get; set; }
    public double? ImageDuration { get; set; }

    /// <summary>Duração total do arquivo em segundos (sondada uma vez no import,
    /// persistida no projeto) — alimenta a barra de trim e os defaults de In/Out.</summary>
    public double? DurationSeconds { get; set; }

    public ResizeMode? Resize { get; set; } // null = herda da Layer

    /// <summary>Padrão da operação: todo vídeo novo entra com volume ZERADO —
    /// o operador sobe o volume só no clip que deve soar (telão costuma ser mudo).</summary>
    public double Volume { get; set; } = 0.0;
    public bool Mute { get; set; }

    /// <summary>Padrão da operação: toda mídia nova já entra avançando pro próximo
    /// clip ao terminar (fotos após o timer, vídeos após cada passada).</summary>
    public AutopilotAction Autopilot { get; set; } = AutopilotAction.PlayNextClip;

    // ── Transform manual (painel CLIP interativo) ──────────────────────────
    // Aplicado POR CIMA do retângulo calculado pelo Resize (Fit/Fill/etc.):
    // primeiro o Resize encaixa a mídia na composição, depois este transform
    // escala/move o resultado. A maioria dos clipes nunca mexe nisso (fica
    // no padrão PositionX=PositionY=0, ScaleX=ScaleY=1).

    /// <summary>Deslocamento horizontal, em frações da largura da composição
    /// (-1 = uma tela inteira pra esquerda, 0 = centrado, 1 = uma tela inteira pra direita).</summary>
    public double PositionX { get; set; } = 0.0;

    /// <summary>Deslocamento vertical, em frações da altura da composição.</summary>
    public double PositionY { get; set; } = 0.0;

    /// <summary>Escala horizontal aplicada sobre o retângulo do Resize (1 = tamanho normal).</summary>
    public double ScaleX { get; set; } = 1.0;

    /// <summary>Escala vertical aplicada sobre o retângulo do Resize (1 = tamanho normal).</summary>
    public double ScaleY { get; set; } = 1.0;

    public Clip(SourceDescriptor source)
    {
        Source = source;
    }

    /// <summary>Criar um deep clone (novo ID) caso seja duplicado.</summary>
    public Clip Clone()
    {
        return new Clip(Source)
        {
            Id = Guid.NewGuid(),
            Name = Name,
            PlayMode = PlayMode,
            Speed = Speed,
            InPoint = InPoint,
            OutPoint = OutPoint,
            ImageDuration = ImageDuration,
            DurationSeconds = DurationSeconds,
            Resize = Resize,
            Volume = Volume,
            Mute = Mute,
            Autopilot = Autopilot,
            PositionX = PositionX,
            PositionY = PositionY,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
        };
    }

    /// <summary>Cópia para sincronização: mantém a identidade do clip entre UI e engine.</summary>
    public Clip Copy()
    {
        return new Clip(Source)
        {
            Id = Id,
            Name = Name,
            PlayMode = PlayMode,
            Speed = Speed,
            InPoint = InPoint,
            OutPoint = OutPoint,
            ImageDuration = ImageDuration,
            DurationSeconds = DurationSeconds,
            Resize = Resize,
            Volume = Volume,
            Mute = Mute,
            Autopilot = Autopilot,
            PositionX = PositionX,
            PositionY = PositionY,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
        };
    }
}
