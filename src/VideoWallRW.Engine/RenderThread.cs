using System.Diagnostics;
using System.Threading.Channels;
using Serilog;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Stats;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Engine.Output;
using VideoWallRW.Engine.Rendering;

namespace VideoWallRW.Engine;

/// <summary>
/// Thread dona do render: processa comandos, renderiza e apresenta as saídas.
/// A UI só interage via <see cref="Post"/> e eventos (que chegam NA render thread —
/// marshalar para o dispatcher).
/// </summary>
public sealed class RenderThread : IDisposable
{
    private readonly GraphicsDevice _gfx;
    private readonly Channel<EngineCommand> _commands =
        Channel.CreateUnbounded<EngineCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<SwapChainOutput> _outputs = [];
    private readonly Dictionary<string, int> _frameCounts = [];
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Publicado ~2x/segundo, na render thread.</summary>
    public event Action<EngineStats>? StatsUpdated;

    /// <summary>Erro fatal (device lost etc.); as saídas foram derrubadas.</summary>
    public event Action<string>? EngineFailed;

    public RenderThread(GraphicsDevice gfx) => _gfx = gfx;

    public void Start()
    {
        if (_thread is not null)
            return;
        _running = true;
        _thread = new Thread(Loop) { Name = "RenderThread", IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Post(EngineCommand command) => _commands.Writer.TryWrite(command);

    public void Dispose() => Stop();

    private void Loop()
    {
        Log.Information("Render thread iniciada");
        using var scene = new TestSceneRenderer(_gfx);
        var clock = Stopwatch.StartNew();
        var statsClock = Stopwatch.StartNew();
        try
        {
            while (_running)
            {
                while (_commands.Reader.TryRead(out var command))
                    Handle(command);

                // As janelas de saída vivem nesta thread; o pump precisa rodar aqui.
                OutputWindow.PumpMessages();

                if (_outputs.Count == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                double time = clock.Elapsed.TotalSeconds;
                foreach (var output in _outputs)
                    scene.Render(_gfx.Context, output.Rtv, output.Monitor.Width, output.Monitor.Height, time);

                foreach (var output in _outputs)
                {
                    var result = output.Present();
                    if (result.Failure)
                        throw new InvalidOperationException(
                            $"Present falhou em {output.Monitor.DeviceName}: {result} " +
                            $"(DeviceRemovedReason: {_gfx.Device.DeviceRemovedReason})");
                    _frameCounts[output.Monitor.DeviceName] =
                        _frameCounts.GetValueOrDefault(output.Monitor.DeviceName) + 1;
                }

                if (statsClock.ElapsedMilliseconds >= 500)
                    PublishStats(statsClock);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erro fatal na render thread");
            EngineFailed?.Invoke(ex.Message);
        }
        finally
        {
            foreach (var output in _outputs)
                output.Dispose();
            _outputs.Clear();
            OutputWindow.PumpMessages();
            Log.Information("Render thread encerrada");
        }
    }

    private void Handle(EngineCommand command)
    {
        switch (command)
        {
            case EnableOutput(var deviceName):
                Enable(deviceName);
                break;
            case DisableOutput(var deviceName):
                Disable(deviceName);
                break;
            default:
                Log.Warning("Comando não tratado: {Command}", command);
                break;
        }
    }

    private void Enable(string deviceName)
    {
        if (_outputs.Any(o => o.Monitor.DeviceName == deviceName))
            return;
        var monitor = MonitorEnumerator.Enumerate(_gfx).FirstOrDefault(m => m.DeviceName == deviceName);
        if (monitor is null)
        {
            Log.Warning("Monitor {Device} não encontrado; saída ignorada", deviceName);
            return;
        }
        try
        {
            var output = new SwapChainOutput(_gfx, monitor) { IsPrimary = _outputs.Count == 0 };
            _outputs.Add(output);
            Log.Information("Saída ativada: {Monitor} (primária: {Primary})", monitor, output.IsPrimary);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao ativar saída em {Device}", deviceName);
        }
    }

    private void Disable(string deviceName)
    {
        var output = _outputs.FirstOrDefault(o => o.Monitor.DeviceName == deviceName);
        if (output is null)
            return;
        _outputs.Remove(output);
        output.Dispose();
        _frameCounts.Remove(deviceName);
        if (_outputs.Count > 0 && !_outputs.Any(o => o.IsPrimary))
            _outputs[0].IsPrimary = true;
        Log.Information("Saída desativada: {Device}", deviceName);
    }

    private void PublishStats(Stopwatch statsClock)
    {
        double seconds = statsClock.Elapsed.TotalSeconds;
        var outputs = _outputs
            .Select(o => new OutputStats(o.Monitor.DeviceName,
                _frameCounts.GetValueOrDefault(o.Monitor.DeviceName) / seconds))
            .ToList();
        _frameCounts.Clear();
        statsClock.Restart();
        StatsUpdated?.Invoke(new EngineStats(outputs));
    }
}
