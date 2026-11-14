namespace VideoWallRW.Core.Stats;

/// <summary>Estado do transporte de um clip.</summary>
public enum PlaybackState { Playing, Paused, Stopped, Error }

/// <summary>
/// Snapshot imutável do estado do clip ativo, publicado ~2×/segundo pela render thread.
/// </summary>
public sealed record ClipSnapshot(
    string FilePath,
    double PlayheadSeconds,
    double DurationSeconds,
    PlaybackState State,
    double DecodeFps);
