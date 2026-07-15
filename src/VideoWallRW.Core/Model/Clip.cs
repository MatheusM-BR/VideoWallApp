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
            Autopilot = Autopilot
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
            Autopilot = Autopilot
        };
    }
}
