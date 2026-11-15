using System.IO.Pipes;
using System.Windows.Threading;
using VideoWallRW.Engine;

namespace VideoWallRW.App;

/// <summary>
/// Lado do app do heartbeat do watchdog (Fase 8 T8.3). Conecta ao pipe nomeado que o
/// VideoWallRW.Watchdog cria e escreve um byte a cada 3s.
///
/// O que o batimento prova é a saúde do ENGINE, não da UI: ele sai de uma thread
/// própria e só é enviado enquanto o loop de render está avançando. Antes, o timer
/// rodava na UI — com o engine morto (TDR da GPU, Present falhando) as telas ficavam
/// pretas e o watchdog nunca reiniciava; e uma UI ocupada por 10s derrubava um
/// processo que estava no ar normalmente.
///
/// A UI ainda conta, com tolerância bem maior (<see cref="UiFrozenLimit"/>): travada
/// por um minuto inteiro, o operador perdeu o controle e reiniciar vale a pena.
/// Sem watchdog rodando (uso normal), a conexão falha e tenta de novo; nunca derruba o app.
/// </summary>
public sealed class HeartbeatClient : IDisposable
{
    private const string PipeName = "vwrw-heartbeat";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EngineStallLimit = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan UiFrozenLimit = TimeSpan.FromSeconds(60);

    private readonly RenderThread _render;
    private readonly Timer _timer;
    private readonly DispatcherTimer _uiProbe;
    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private readonly byte[] _beat = [1];

    private long _lastLoopCount = -1;
    private DateTime _lastEngineProgressUtc = DateTime.UtcNow;
    private long _lastUiTickUtcTicks = DateTime.UtcNow.Ticks;
    private bool _disposed;

    public HeartbeatClient(RenderThread render)
    {
        _render = render;
        _uiProbe = new DispatcherTimer { Interval = Interval };
        _uiProbe.Tick += (_, _) => Interlocked.Exchange(ref _lastUiTickUtcTicks, DateTime.UtcNow.Ticks);
        _uiProbe.Start();
        _timer = new Timer(_ => Beat(), null, Interval, Interval);
    }

    /// <summary>True enquanto o pipe do watchdog está conectado.</summary>
    public bool WatchdogConnected
    {
        get { lock (_gate) return _pipe is { IsConnected: true }; }
    }

    private bool IsHealthy()
    {
        if (_render.HasFailed) return false;

        var now = DateTime.UtcNow;
        long loops = _render.LoopCount;
        if (loops != _lastLoopCount)
        {
            _lastLoopCount = loops;
            _lastEngineProgressUtc = now;
        }
        if (now - _lastEngineProgressUtc > EngineStallLimit) return false;

        var lastUi = new DateTime(Interlocked.Read(ref _lastUiTickUtcTicks), DateTimeKind.Utc);
        return now - lastUi <= UiFrozenLimit;
    }

    private void Beat()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Sem batimento o watchdog conclui "travado" em 10s e reinicia o processo —
            // que é exatamente a recuperação certa pra engine morto ou parado.
            if (!IsHealthy()) return;
            try
            {
                if (_pipe is null)
                {
                    _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    _pipe.Connect(0); // não bloqueia: sem watchdog, falha imediata e tenta de novo no próximo tick
                }
                _pipe.Write(_beat);
                _pipe.Flush();
            }
            catch
            {
                _pipe?.Dispose();
                _pipe = null;
            }
        }
    }

    public void Dispose()
    {
        _uiProbe.Stop();
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            _pipe?.Dispose();
            _pipe = null;
        }
    }
}
