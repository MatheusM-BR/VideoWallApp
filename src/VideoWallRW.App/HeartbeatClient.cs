using System.IO.Pipes;
using System.Windows.Threading;
using Serilog;

namespace VideoWallRW.App;

/// <summary>
/// Lado do app do heartbeat do watchdog (Fase 8 T8.3). Conecta ao pipe nomeado que o
/// VideoWallRW.Watchdog cria e escreve um byte a cada 3s, na dispatcher timer da UI —
/// se a UI travar, o timer para de disparar e o watchdog detecta em até 10s. Sem
/// watchdog rodando (uso normal em desenvolvimento), a conexão simplesmente falha e
/// fica tentando de novo em segundo plano; nunca derruba o app.
/// </summary>
public sealed class HeartbeatClient : IDisposable
{
    private const string PipeName = "vwrw-heartbeat";
    private readonly DispatcherTimer _timer;
    private NamedPipeClientStream? _pipe;
    private readonly byte[] _beat = [1];

    public HeartbeatClient()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Beat();
        _timer.Start();
    }

    private void Beat()
    {
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

    public void Dispose()
    {
        _timer.Stop();
        _pipe?.Dispose();
    }
}
