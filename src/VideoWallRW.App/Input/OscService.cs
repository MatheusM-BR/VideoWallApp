using System.Net.Sockets;
using System.Text.RegularExpressions;
using Serilog;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Osc;
using VideoWallRW.Engine;

namespace VideoWallRW.App.Input;

/// <summary>
/// Entrada OSC (Fase 9 T9.4): escuta UDP e traduz endereços em <see cref="EngineCommand"/>
/// já existentes — o listener é só mais um produtor da fila do engine (mesma arquitetura
/// do KeymapService/UI). Endereços documentados em docs/OSC.md.
///
/// DESLIGADA por padrão: OSC não tem senha, então qualquer um na rede mandaria blackout.
/// O operador liga em Ferramentas → "Entrada OSC (porta 7000)" quando for usar
/// (Companion, TouchOSC…); fechar o programa desliga.
/// </summary>
public sealed partial class OscService : IDisposable
{
    private readonly RenderThread _render;
    private readonly Func<string, Guid?> _resolveScene;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;

    [GeneratedRegex(@"^/vwrw/layer/(\d+)/clip/(\d+)/trigger$")] private static partial Regex ClipTriggerRe();
    [GeneratedRegex(@"^/vwrw/layer/(\d+)/opacity$")] private static partial Regex LayerOpacityRe();
    [GeneratedRegex(@"^/vwrw/column/(\d+)/trigger$")] private static partial Regex ColumnTriggerRe();
    [GeneratedRegex(@"^/vwrw/scene/(.+)/trigger$")] private static partial Regex SceneTriggerRe();

    public OscService(RenderThread render, Func<string, Guid?> resolveScene)
    {
        _render = render;
        _resolveScene = resolveScene;
    }

    public const int DefaultPort = 7000;

    public bool IsRunning => _client is not null;

    /// <summary>Motivo da última falha ao ligar (porta ocupada etc.), ou null.</summary>
    public string? LastError { get; private set; }

    public bool Start(int port = DefaultPort)
    {
        if (_client is not null) return true;
        try
        {
            LastError = null;
            _client = new UdpClient(port);
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ListenLoop(_cts.Token));
            Log.Information("OSC LIGADO na porta UDP {Port}", port);
            return true;
        }
        catch (Exception ex)
        {
            _client = null;
            LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                ? $"A porta {port} já está em uso por outro programa."
                : ex.Message;
            Log.Warning(ex, "Falha ao ligar OSC na porta {Port}", port);
            return false;
        }
    }

    public void Stop()
    {
        if (_client is null) return;
        _cts?.Cancel();
        _client.Dispose();
        _client = null;
        _cts?.Dispose();
        _cts = null;
        Log.Information("OSC DESLIGADO");
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client!.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Erro ao receber pacote OSC");
                continue;
            }

            try
            {
                Route(OscMessage.Parse(result.Buffer));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pacote OSC inválido descartado");
            }
        }
    }

    private void Route(OscMessage msg)
    {
        Match m;
        if ((m = ClipTriggerRe().Match(msg.Address)).Success)
        {
            _render.Post(new TriggerClip(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }
        else if ((m = LayerOpacityRe().Match(msg.Address)).Success && msg.Args is [float f])
        {
            _render.Post(new SetLayerOpacity(int.Parse(m.Groups[1].Value), f));
        }
        else if ((m = ColumnTriggerRe().Match(msg.Address)).Success)
        {
            _render.Post(new TriggerColumn(int.Parse(m.Groups[1].Value)));
        }
        else if ((m = SceneTriggerRe().Match(msg.Address)).Success)
        {
            var sceneId = _resolveScene(m.Groups[1].Value);
            if (sceneId is { } id)
                _render.Post(new TriggerScene(id));
        }
        else if (msg.Address == "/vwrw/master/fader" && msg.Args is [float mf])
        {
            _render.Post(new SetMasterOpacity(mf));
        }
        else if (msg.Address == "/vwrw/blackout" && msg.Args is [int bo, ..])
        {
            _render.Post(new SetBlackout(bo != 0));
        }
        else if (msg.Address == "/vwrw/pause" && msg.Args is [int p, ..])
        {
            _render.Post(new SetMasterPaused(p != 0));
        }
        else
        {
            Log.Debug("OSC: endereço não mapeado {Address}", msg.Address);
        }
    }

    public void Dispose() => Stop();
}
