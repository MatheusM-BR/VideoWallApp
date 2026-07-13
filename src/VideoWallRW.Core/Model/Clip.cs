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
    
    public ResizeMode? Resize { get; set; } // null = herda da Layer
    
    public double Volume { get; set; } = 1.0;
    public bool Mute { get; set; }
    
    public AutopilotAction Autopilot { get; set; } = AutopilotAction.None;

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
            Resize = Resize,
            Volume = Volume,
            Mute = Mute,
            Autopilot = Autopilot
        };
    }
}
