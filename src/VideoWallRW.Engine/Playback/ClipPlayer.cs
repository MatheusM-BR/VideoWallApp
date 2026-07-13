using VideoWallRW.Core.Model;
using VideoWallRW.Core.Stats;

namespace VideoWallRW.Engine.Playback;

/// <summary>
/// Relógio e transporte de um clip. Chamado exclusivamente pela render thread.
/// Pausa = <see cref="Tick"/> não avança o playhead → render repete o último frame.
/// </summary>
public sealed class ClipPlayer : IDisposable
{
    private readonly IPlayableSource _source;
    private Clip _clip;
    private int _direction = 1;
    private bool _ended;
    private double _audioFade = 1.0;

    public IPlayableSource Source => _source;
    public PlaybackState State { get; private set; } = PlaybackState.Paused;
    public double Playhead { get; private set; }
    public double Speed { get; set; } = 1.0;
    public event Action<Clip>? EndReached;

    public ClipPlayer(IPlayableSource source, Clip clip)
    {
        _source = source;
        _clip = clip;
        _source.Start();
        State = PlaybackState.Playing;
        Speed = clip.Speed;
        Playhead = GetBounds().In;
        ApplyAudioState();
    }

    /// <summary>
    /// Avança o relógio do clip. Chamar uma vez por frame na render thread.
    /// </summary>
    public void Tick(double dt)
    {
        if (State != PlaybackState.Playing || _source.State == SourceState.Arming) return;

        var (inPoint, outPoint) = GetBounds();
        Playhead += dt * Speed * _direction;

        if (_direction > 0 && Playhead < outPoint || _direction < 0 && Playhead > inPoint)
            return;

        switch (_clip.PlayMode)
        {
            case PlayMode.Loop:
                Playhead = Wrap(Playhead, inPoint, outPoint);
                _source.Seek(Playhead);
                break;

            case PlayMode.PingPong:
                if (_direction > 0)
                {
                    Playhead = outPoint;
                    _direction = -1;
                }
                else
                {
                    Playhead = inPoint;
                    _direction = 1;
                }
                _source.Seek(Playhead);
                break;

            case PlayMode.OnceHold:
                Playhead = _direction > 0 ? outPoint : inPoint;
                State = PlaybackState.Paused;
                RaiseEndReached();
                break;

            case PlayMode.OnceClear:
                Playhead = _direction > 0 ? outPoint : inPoint;
                State = PlaybackState.Stopped;
                RaiseEndReached();
                break;
        }
    }

    public void Play()
    {
        if (State == PlaybackState.Paused)
        {
            State = PlaybackState.Playing;
            _ended = false;
            _source.Start();
            ApplyAudioState();
        }
    }

    public void Pause()
    {
        if (State == PlaybackState.Playing)
        {
            State = PlaybackState.Paused;
            ApplyAudioState();
        }
    }

    public void Seek(double seconds)
    {
        var (inPoint, outPoint) = GetBounds();
        Playhead = Math.Clamp(seconds, inPoint, outPoint);
        _ended = false;
        _source.Seek(Playhead);
    }

    /// <summary>Aplica metadados editáveis sem recriar a fonte que já está em reprodução.</summary>
    public void UpdateSettings(Clip clip)
    {
        if (!Equals(_clip.Source, clip.Source))
            return;

        _clip = clip;
        Speed = Math.Clamp(clip.Speed, 0.1, 4.0);
        var (inPoint, outPoint) = GetBounds();
        Playhead = Math.Clamp(Playhead, inPoint, outPoint);
        ApplyAudioState();
    }

    /// <summary>Ganho transitório usado durante o crossfade, sem alterar o volume do clip.</summary>
    public void SetAudioFade(double fade)
    {
        _audioFade = Math.Clamp(fade, 0.0, 1.0);
        ApplyAudioState();
    }

    private (double In, double Out) GetBounds()
    {
        double duration = _clip.Source is FileImageSource && _clip.ImageDuration is not null
            ? Math.Max(0, _clip.ImageDuration.Value)
            : Math.Max(0, _source.Duration);
        double inPoint = Math.Clamp(_clip.InPoint ?? 0, 0, duration);
        double outPoint = Math.Clamp(_clip.OutPoint ?? duration, inPoint, duration);
        return (inPoint, outPoint);
    }

    private static double Wrap(double value, double start, double end)
    {
        double length = end - start;
        if (length <= 0) return start;
        return start + ((value - start) % length + length) % length;
    }

    private void RaiseEndReached()
    {
        if (_ended) return;
        _ended = true;
        EndReached?.Invoke(_clip);
    }

    private void ApplyAudioState()
    {
        if (_source is IAudioControllable audio)
            audio.SetAudioState(State == PlaybackState.Playing, _clip.Volume, _clip.Mute, Speed, _audioFade);
    }

    public void Dispose()
    {
        if (_source is IAudioControllable audio)
            audio.SetAudioState(false, 0, true, Speed, 0);
        _source.Stop();
        _source.Dispose();
    }
}
