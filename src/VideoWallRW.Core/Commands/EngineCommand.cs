namespace VideoWallRW.Core.Commands;

/// <summary>
/// Comando UI → engine. Imutável; processado exclusivamente pela render thread.
/// </summary>
public abstract record EngineCommand;

public sealed record EnableOutput(string MonitorDeviceName) : EngineCommand;

public sealed record DisableOutput(string MonitorDeviceName) : EngineCommand;
