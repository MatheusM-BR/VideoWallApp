namespace VideoWallRW.Core.Stats;

public sealed record OutputStats(string MonitorDeviceName, double Fps);

/// <summary>Posição de reprodução do clip ativo de uma layer — alimenta a "agulha"
/// da barra de trim na UI.</summary>
public sealed record LayerClipPosition(int LayerIndex, Guid ClipId, double Playhead);

/// <summary>
/// Snapshot leve engine → UI. Publicado ~2x por segundo pela render thread.
/// </summary>
public sealed record EngineStats(IReadOnlyList<OutputStats> Outputs, ClipSnapshot? Clip = null,
    int DroppedFrames = 0, int ActiveClipCount = 0,
    IReadOnlyList<LayerClipPosition>? Positions = null);
