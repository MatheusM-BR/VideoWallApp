namespace VideoWallRW.Core.Stats;

public sealed record OutputStats(string MonitorDeviceName, double Fps);

/// <summary>
/// Snapshot leve engine → UI. Publicado ~2x por segundo pela render thread.
/// </summary>
public sealed record EngineStats(IReadOnlyList<OutputStats> Outputs, ClipSnapshot? Clip = null);
