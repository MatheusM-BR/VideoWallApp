using VideoWallRW.Core.Stats;

namespace VideoWallRW.Engine.Playback;

/// <summary>
/// Relógio e transporte de um clip. Chamado exclusivamente pela render thread.
/// Pausa = <see cref="Tick"/> não avança o playhead → render repete o último frame.
/// </summary>
public sealed class ClipPlayer : IDisposable
{
    private readonly IPlayableSource _source;

    public IPlayableSource Source => _source;
    public PlaybackState State { get; private set; } = PlaybackState.Paused;
    public double Playhead { get; private set; }
    public double Speed { get; set; } = 1.0;

    public ClipPlayer(IPlayableSource source)
    {
        _source = source;
        _source.Start();
        State = PlaybackState.Playing;
    }

    /// <summary>
    /// Avança o relógio do clip. Chamar uma vez por frame na render thread.
    /// </summary>
    public void Tick(double dt)
    {
        if (State != PlaybackState.Playing) return;

        Playhead += dt * Speed;

        // Loop simples: enrola o playhead quando ultrapassa a duração.
        if (_source.Duration > 0 && Playhead >= _source.Duration)
            Playhead -= _source.Duration;
    }

    public void Play()
    {
        if (State == PlaybackState.Paused) State = PlaybackState.Playing;
    }

    public void Pause()
    {
        if (State == PlaybackState.Playing) State = PlaybackState.Paused;
    }

    public void Seek(double seconds)
    {
        Playhead = Math.Clamp(seconds, 0, _source.Duration > 0 ? _source.Duration : double.MaxValue);
        _source.Seek(Playhead);
    }

    public void Dispose()
    {
        _source.Stop();
        _source.Dispose();
    }
}
