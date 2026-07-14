namespace VideoWallRW.Core.Commands;

/// <summary>
/// Comando UI → engine. Imutável; processado exclusivamente pela render thread.
/// </summary>
public abstract record EngineCommand;

public sealed record EnableOutput(string MonitorDeviceName)  : EngineCommand;
public sealed record DisableOutput(string MonitorDeviceName) : EngineCommand;

// ── Fase 2: acionadores de grade ──────────────────────────────────────────────
public sealed record SyncComposition(VideoWallRW.Core.Model.Composition Data) : EngineCommand;
public sealed record TriggerClip(int LayerIndex, int ColumnIndex) : EngineCommand;
public sealed record TriggerColumn(int ColumnIndex) : EngineCommand;
public sealed record ClearLayer(int LayerIndex) : EngineCommand;
public sealed record ClearAll : EngineCommand;
public sealed record SetLayerOpacity(int LayerIndex, double Opacity) : EngineCommand;
public sealed record SetLayerState(int LayerIndex, VideoWallRW.Core.Model.BlendMode Blend,
    bool Bypass, bool Solo, double TransitionSeconds) : EngineCommand;
public sealed record UpdateClip(int LayerIndex, int ColumnIndex,
    VideoWallRW.Core.Model.Clip? Clip) : EngineCommand;
public sealed record SetMasterPaused(bool IsPaused) : EngineCommand;
public sealed record SetMasterOpacity(double Opacity) : EngineCommand;

// ── Fase 4: Advanced Output (fatiamento de saída) ─────────────────────────────
public sealed record SyncOutputSetup(VideoWallRW.Core.Model.OutputSetup Data) : EngineCommand;
