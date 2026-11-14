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
    /// <remarks>
    /// Uma volta menor que isto é tratada como "sem duração real": o fim é anunciado
    /// UMA vez (pro autopilot poder avançar) e não fica se repetindo a cada frame.
    /// </remarks>
    private const double MinCycleSeconds = 0.05;

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
                // Autopilot também vale para clips em Loop (o padrão da grade): cada
                // volta completa conta como "fim" — se ninguém trocar o clip (autopilot
                // da layer OFF ou ação None), o loop segue e avisa de novo na próxima volta.
                if (_clip.Autopilot != AutopilotAction.None)
                {
                    RaiseEndReached();
                    // SÓ re-arma o disparo se a volta tem duração REAL. Com um intervalo
                    // degenerado (duração 0, sonda de duração falhou, ou In == Out por
                    // trim/edição) o playhead passa do fim em TODO frame e isto dispararia
                    // o autopilot 60-165x por segundo — cada disparo abrindo uma fonte
                    // nova (FFmpeg + texturas). Era uma tempestade que estourava
                    // memória/VRAM em segundos e derrubava o processo, além de travar a
                    // sequência (nunca assentava no próximo clipe).
                    if (outPoint - inPoint > MinCycleSeconds)
                        _ended = false;
                }
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

    /// <summary>Duração usada quando a do clipe é inválida — ver <see cref="GetBounds"/>.</summary>
    private const double FallbackCycleSeconds = 5.0;

    private (double In, double Out) GetBounds()
    {
        double duration = _clip.Source is FileImageSource && _clip.ImageDuration is not null
            ? Math.Max(0, _clip.ImageDuration.Value)
            : Math.Max(0, _source.Duration);
        // A MÍDIA não tem duração utilizável (duração 0 / sonda falhou / arquivo
        // quebrado): sem um fim válido o playhead passaria do fim em TODO frame e o
        // autopilot dispararia 60-165x por segundo, abrindo uma fonte nova a cada vez
        // até derrubar o processo. Nesse caso — e SÓ nesse — assume um ciclo padrão.
        // Um trim curto FEITO DE PROPÓSITO pelo operador é respeitado: quem segura a
        // tempestade ali é o guard do _ended (Tick) mais a trava por layer no engine.
        if (!double.IsInfinity(duration) && duration < MinCycleSeconds)
            return (0, FallbackCycleSeconds);

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
