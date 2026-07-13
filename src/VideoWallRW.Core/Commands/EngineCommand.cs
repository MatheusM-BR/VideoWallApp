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
