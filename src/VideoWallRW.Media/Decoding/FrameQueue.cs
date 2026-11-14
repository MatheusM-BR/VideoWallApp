using System.Diagnostics;
using Serilog;

namespace VideoWallRW.Media.Decoding;

/// <summary>
/// Fila limitada de frames decodificados com pool de buffers (evita GC por frame).
/// Produtor (decode thread): <see cref="TryOffer"/> bloqueia quando cheia.
/// Consumidor (render thread): <see cref="TryConsume"/> retorna o frame de maior PTS ≤ playhead.
/// </summary>
public sealed class FrameQueue : IDisposable
{
    private const int Capacity = 6;

    private readonly object _lock = new();
    private readonly List<DecodedFrame> _frames = new(Capacity);
    private readonly List<DecodedFrame> _pool = new(Capacity * 2);
    private readonly SemaphoreSlim _space = new(Capacity, Capacity);
    private bool _disposed;

    // ──────────────────────────────────────────────────────── Produtor

    /// <summary>
    /// Enfileira um frame; bloqueia se a fila estiver cheia. Retorna false se descartado
    /// (generation diferente — frame de seek antigo).
    /// </summary>
    public bool TryOffer(DecodedFrame frame, int currentGeneration, CancellationToken ct)
    {
        if (frame.Generation != currentGeneration)
        {
            ReturnToPool(frame);
            return false;
        }
        _space.Wait(ct); // bloqueia até haver espaço
        lock (_lock)
        {
            if (frame.Generation != currentGeneration)
            {
                _space.Release();
                ReturnToPool(frame);
                return false;
            }
            _frames.Add(frame);
        }
        return true;
    }

    /// <summary>Frames prontos aguardando consumo — usado pelo preroll (a fonte só
    /// libera reprodução com N frames no buffer, evitando enrosco no início/loop).</summary>
    public int Count
    {
        get { lock (_lock) return _frames.Count; }
    }

    // ──────────────────────────────────────────────────────── Consumidor (render thread)

    /// <summary>
    /// Retorna a SRV mais recente com PTS ≤ <paramref name="playheadSeconds"/>,
    /// descartando frames mais antigos. Retorna null se não há frame elegível.
    /// </summary>
    public DecodedFrame? TryConsume(double playheadSeconds)
    {
        lock (_lock)
        {
            DecodedFrame? best = null;
            int bestIdx = -1;

            for (int i = 0; i < _frames.Count; i++)
            {
                var f = _frames[i];
                if (f.PtsSeconds <= playheadSeconds)
                {
                    if (best == null || f.PtsSeconds > best.PtsSeconds)
                    {
                        best = f;
                        bestIdx = i;
                    }
                }
            }

            if (best == null) return null;

            // Descartar todos anteriores ao melhor.
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                if (i == bestIdx) continue;
                if (_frames[i].PtsSeconds <= best.PtsSeconds)
                {
                    ReturnToPoolUnsafe(_frames[i]);
                    _frames.RemoveAt(i);
                    _space.Release();
                    if (i < bestIdx) bestIdx--;
                }
            }

            // Remover o frame escolhido da fila (vai para o chamador temporariamente,
            // que devolve ao pool depois do upload).
            _frames.RemoveAt(bestIdx);
            _space.Release();
            return best;
        }
    }

    // ──────────────────────────────────────────────────────── Pool

    /// <summary>Aloca ou reutiliza um frame do pool.</summary>
    public DecodedFrame Rent()
    {
        lock (_lock)
        {
            if (_pool.Count > 0)
            {
                var f = _pool[^1];
                _pool.RemoveAt(_pool.Count - 1);
                return f;
            }
        }
        return new DecodedFrame();
    }

    /// <summary>Devolve um frame ao pool após o consumidor ter feito upload para a GPU.</summary>
    public void Return(DecodedFrame frame)
    {
        lock (_lock) { _pool.Add(frame); }
    }

    private void ReturnToPool(DecodedFrame frame)
    {
        lock (_lock) { _pool.Add(frame); }
    }

    private void ReturnToPoolUnsafe(DecodedFrame frame) // já dentro de _lock
    {
        _pool.Add(frame);
    }

    /// <summary>Drena a fila inteira e devolve todos os frames ao pool (após seek).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var f in _frames) { _pool.Add(f); _space.Release(); }
            _frames.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _space.Dispose();
    }
}
