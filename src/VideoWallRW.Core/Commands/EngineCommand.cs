namespace VideoWallRW.Core.Commands;

/// <summary>
/// Comando UI → engine. Imutável; processado exclusivamente pela render thread.
/// </summary>
public abstract record EngineCommand;

public sealed record EnableOutput(string MonitorDeviceName)  : EngineCommand;
public sealed record DisableOutput(string MonitorDeviceName) : EngineCommand;

// ── Fase 1: transporte de clip ────────────────────────────────────────────────
public sealed record LoadTestClip(string Path)   : EngineCommand;
public sealed record PlayClip                    : EngineCommand;
public sealed record PauseClip                   : EngineCommand;
public sealed record SeekClip(double Seconds)    : EngineCommand;
public sealed record SetClipSpeed(double Speed)  : EngineCommand;

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
