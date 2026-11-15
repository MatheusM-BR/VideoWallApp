namespace VideoWallRW.App.Remote;

/// <summary>
/// Conta PINs errados por cliente (IP) e bloqueia por um tempo depois do limite.
/// Thread-safe: várias conexões WebSocket autenticam em paralelo.
/// </summary>
internal sealed class PinAttemptLimiter(int maxFailures, TimeSpan lockout, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, (int Failures, DateTime LockedUntil)> _clients = [];
    private readonly object _gate = new();

    public bool IsLocked(string client)
    {
        lock (_gate)
            return _clients.TryGetValue(client, out var c) && c.LockedUntil > _now();
    }

    /// <summary>Registra um erro; devolve true se o cliente acabou de ser bloqueado.</summary>
    public bool RegisterFailure(string client)
    {
        lock (_gate)
        {
            var now = _now();
            var c = _clients.GetValueOrDefault(client);
            if (c.LockedUntil != default && c.LockedUntil <= now)
                c = default; // bloqueio venceu: recomeça a contagem
            c.Failures++;
            if (c.Failures >= maxFailures)
                c.LockedUntil = now + lockout;
            _clients[client] = c;
            return c.LockedUntil > now;
        }
    }

    public void Reset(string client)
    {
        lock (_gate) _clients.Remove(client);
    }
}
