namespace VideoWallRW.Media.Hap;

/// <summary>Um frame HAP já descomprimido (bytes DXT prontos para upload direto na GPU).</summary>
public sealed class HapFrame
{
    public HapTextureFormat Format { get; set; }
    public byte[] DxtBytes { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public double PtsSeconds { get; set; }
    internal int Generation { get; set; }
}

/// <summary>
/// Fila limitada de frames HAP decodificados, com pool (evita GC por frame) — mesmo
/// padrão de <see cref="Decoding.FrameQueue"/>, mas frames HAP são todos keyframes
/// (não precisa achar "o de maior PTS ≤ playhead" descartando anteriores com a mesma
/// urgência; ainda assim mantemos essa política por consistência e simplicidade).
/// </summary>
public sealed class HapFrameQueue : IDisposable
{
    private const int Capacity = 6;

    private readonly object _lock = new();
    private readonly List<HapFrame> _frames = new(Capacity);
    private readonly List<HapFrame> _pool = new(Capacity * 2);
    private readonly SemaphoreSlim _space = new(Capacity, Capacity);
    private bool _disposed;

    /// <summary>Frames prontos aguardando consumo — usado pelo preroll da fonte.</summary>
    public int Count
    {
        get { lock (_lock) return _frames.Count; }
    }

    public bool TryOffer(HapFrame frame, int currentGeneration, CancellationToken ct)
    {
        if (frame.Generation != currentGeneration)
        {
            ReturnToPool(frame);
            return false;
        }
        _space.Wait(ct);
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

    public HapFrame? TryConsume(double playheadSeconds)
    {
        lock (_lock)
        {
            HapFrame? best = null;
            int bestIdx = -1;
            for (int i = 0; i < _frames.Count; i++)
            {
                if (_frames[i].PtsSeconds <= playheadSeconds && (best == null || _frames[i].PtsSeconds > best.PtsSeconds))
                {
                    best = _frames[i];
                    bestIdx = i;
                }
            }
            if (best == null) return null;

            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                if (i == bestIdx) continue;
                if (_frames[i].PtsSeconds <= best.PtsSeconds)
                {
                    _pool.Add(_frames[i]);
                    _frames.RemoveAt(i);
                    _space.Release();
                    if (i < bestIdx) bestIdx--;
                }
            }
            _frames.RemoveAt(bestIdx);
            _space.Release();
            return best;
        }
    }

    public HapFrame Rent()
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
        return new HapFrame();
    }

    public void Return(HapFrame frame)
    {
        lock (_lock) { _pool.Add(frame); }
    }

    private void ReturnToPool(HapFrame frame)
    {
        lock (_lock) { _pool.Add(frame); }
    }

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
