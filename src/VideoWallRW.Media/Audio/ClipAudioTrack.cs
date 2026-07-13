using NAudio.Wave;
using VideoWallRW.Engine;

namespace VideoWallRW.Media.Audio;

/// <summary>
/// Ring buffer de amostras de um clip (float32 estéreo interleaved, 48kHz) exposto
/// como <see cref="ISampleProvider"/> para o <see cref="AudioEngine"/>.
/// <see cref="Write"/> roda na decode thread; <see cref="Read"/> roda na thread de
/// áudio do NAudio — a seção compartilhada é protegida por um lock curto.
/// </summary>
public sealed class ClipAudioTrack : ISampleProvider, IAudioControllable
{
    public const int Channels = 2;
    public const int SampleRate = 48000;
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    // ~2s de buffer: absorve o jitter do decode sem crescer sem limite.
    // Em overflow o mais antigo é sobrescrito (drop-oldest).
    private const int CapacitySamples = SampleRate * Channels * 2;

    private readonly float[] _buffer = new float[CapacitySamples];
    private readonly object _lock = new();
    private int _writePos;
    private int _available;

    private volatile bool _playing;
    private volatile float _gain;

    WaveFormat ISampleProvider.WaveFormat => Format;

    /// <summary>Chamado pela decode thread com amostras já no formato canônico.</summary>
    public void Write(float[] samples, int count)
    {
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                _buffer[_writePos] = samples[i];
                _writePos = (_writePos + 1) % CapacitySamples;
                if (_available < CapacitySamples)
                    _available++;
                // overflow: o mais antigo já foi sobrescrito acima (drop-oldest implícito).
            }
        }
    }

    /// <summary>Descarta amostras pendentes — usado em seek e ao pausar (sem "cauda" de áudio).</summary>
    public void Clear()
    {
        lock (_lock) { _available = 0; }
    }

    /// <summary>Chamado pela thread de áudio. Nunca retorna 0 — o mixer removeria a track.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        float gain = _gain;
        if (!_playing || gain <= 0f)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        lock (_lock)
        {
            int readPos = (_writePos - _available + CapacitySamples) % CapacitySamples;
            int toCopy = Math.Min(count, _available);
            for (int i = 0; i < toCopy; i++)
            {
                buffer[offset + i] = _buffer[readPos] * gain;
                readPos = (readPos + 1) % CapacitySamples;
            }
            _available -= toCopy;
            if (toCopy < count)
                Array.Clear(buffer, offset + toCopy, count - toCopy); // underrun: completa com silêncio
        }
        return count;
    }

    public void SetAudioState(bool playing, double volume, bool muted, double speed, double fade)
    {
        // v1: velocidade != 1x muta o áudio (sem time-stretch) — ver docs/fases/FASE-3.md T3.6.
        bool effectivePlaying = playing && !muted && Math.Abs(speed - 1.0) < 0.001;
        _gain = (float)Math.Clamp(volume * fade, 0.0, 1.0);
        if (_playing && !effectivePlaying)
            Clear(); // corta o áudio na hora, sem cauda residual
        _playing = effectivePlaying;
    }
}
