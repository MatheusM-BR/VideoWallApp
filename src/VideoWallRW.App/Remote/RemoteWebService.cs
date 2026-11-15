using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using VideoWallRW.Core.Commands;
using VideoWallRW.Engine;

namespace VideoWallRW.App.Remote;

/// <summary>
/// Controle remoto web (Fase 9 T9.5): Kestrel embutido serve uma página única
/// (HTML+JS vanilla) que troca estado/comandos por WebSocket. Reusa exatamente os
/// mesmos <see cref="EngineCommand"/> que a UI local usa — o navegador é só mais um
/// produtor da fila do engine.
///
/// DESLIGADO por padrão: só sobe quando o operador liga na janela "Controle remoto"
/// (botão no topo da tela principal), que mostra o endereço da máquina e o PIN. Cada
/// vez que liga, sorteia um PIN novo de 6 dígitos; desligar derruba quem estiver conectado. Tentativas erradas são limitadas por IP
/// (<see cref="MaxFailedAttempts"/> erros → bloqueio de <see cref="LockoutDuration"/>):
/// sem isso, as combinações do PIN saíam por força bruta em segundos pelo WebSocket.
/// </summary>
public sealed class RemoteWebService : IAsyncDisposable
{
    private readonly RenderThread _render;
    private readonly Func<object> _stateSnapshot;
    private WebApplication? _app;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _clients;

    internal const int MaxFailedAttempts = 5;
    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailedAttemptDelay = TimeSpan.FromSeconds(1);
    private readonly PinAttemptLimiter _limiter = new(MaxFailedAttempts, LockoutDuration);

    public const int DefaultPort = 8080;

    /// <summary>PIN da sessão atual (troca a cada vez que o controle é ligado).</summary>
    public string Pin { get; private set; } = "";
    public int Port { get; private set; } = DefaultPort;
    public bool IsRunning => _app is not null;
    /// <summary>Motivo da última falha ao ligar (porta ocupada etc.), ou null.</summary>
    public string? LastError { get; private set; }
    /// <summary>Navegadores conectados e autenticados agora.</summary>
    public int ConnectedClients => Volatile.Read(ref _clients);

    /// <summary>Ligou, desligou, conectou ou desconectou alguém. Pode vir de qualquer thread.</summary>
    public event Action? StateChanged;

    public RemoteWebService(RenderThread render, Func<object> stateSnapshot)
    {
        _render = render;
        _stateSnapshot = stateSnapshot;
    }

    public async Task<bool> StartAsync(int port = DefaultPort)
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is not null) return true;
            LastError = null;
            Pin = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            Port = port;
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            var app = builder.Build();
            app.UseWebSockets();

            app.MapGet("/", () => Results.Content(Page, "text/html; charset=utf-8"));
            app.Map("/ws", HandleWebSocket);

            await app.StartAsync();
            _app = app;
            Log.Information("Controle remoto web LIGADO em http://<ip-da-maquina>:{Port}", port);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex is System.IO.IOException || ex.InnerException is System.Net.Sockets.SocketException
                ? $"A porta {port} já está em uso por outro programa."
                : ex.Message;
            Log.Warning(ex, "Falha ao ligar o controle remoto web na porta {Port}", port);
            return false;
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is null) return;
            var app = _app;
            _app = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
            Interlocked.Exchange(ref _clients, 0);
            Log.Information("Controle remoto web DESLIGADO");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Erro ao desligar o controle remoto web");
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    private async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "?";
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        bool authenticated = false;
        var buffer = new byte[4096];

        using var pushTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var pushTask = Task.Run(async () =>
        {
            while (await pushTimer.WaitForNextTickAsync())
            {
                if (!authenticated || socket.State != WebSocketState.Open) continue;
                var json = JsonSerializer.Serialize(new { type = "state", data = _stateSnapshot() });
                try { await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { /* socket fechou entre a checagem e o envio */ }
            }
        });

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (!authenticated)
                {
                    bool ok = TryAuthenticate(text, clientKey, out bool locked);
                    if (!ok) await Task.Delay(FailedAttemptDelay); // freia tentativa em série
                    authenticated = ok;
                    if (ok)
                    {
                        Interlocked.Increment(ref _clients);
                        StateChanged?.Invoke();
                    }
                    var reply = JsonSerializer.Serialize(new { type = "auth", ok, locked });
                    await socket.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                HandleMessage(text);
            }
        }
        catch (WebSocketException) { /* cliente caiu — encerra normalmente */ }
        finally
        {
            pushTimer.Dispose();
            await pushTask;
            if (authenticated && Interlocked.Decrement(ref _clients) < 0)
                Interlocked.Exchange(ref _clients, 0);
            if (authenticated) StateChanged?.Invoke();
        }
    }

    private bool TryAuthenticate(string json, string clientKey, out bool locked)
    {
        locked = _limiter.IsLocked(clientKey);
        if (locked) return false;

        string? value = null;
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.TryGetProperty("type", out var t) && t.GetString() == "pin" &&
                root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                value = v.GetString();
        }
        catch (JsonException) { }

        bool ok = value is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(Pin));
        if (ok)
        {
            _limiter.Reset(clientKey);
            Log.Information("Controle remoto: {Client} autenticado", clientKey);
            return true;
        }

        locked = _limiter.RegisterFailure(clientKey);
        if (locked)
            Log.Warning("Controle remoto: {Client} bloqueado por {Min} min após {N} PINs errados",
                clientKey, LockoutDuration.TotalMinutes, MaxFailedAttempts);
        return false;
    }

    private void HandleMessage(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return; }

        string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        // Mensagem malformada de um cliente autenticado (campo faltando, tipo errado)
        // é descartada — não pode derrubar a conexão nem o serviço.
        try
        {
            Dispatch(type, root);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        {
            Log.Debug(ex, "Controle remoto: mensagem '{Type}' inválida ignorada", type);
        }
    }

    private void Dispatch(string? type, JsonElement root)
    {
        switch (type)
        {
            case "trigger_clip":
                _render.Post(new TriggerClip(root.GetProperty("layer").GetInt32(), root.GetProperty("column").GetInt32()));
                break;
            case "trigger_column":
                _render.Post(new TriggerColumn(root.GetProperty("column").GetInt32()));
                break;
            case "trigger_scene":
                _render.Post(new TriggerScene(Guid.Parse(root.GetProperty("id").GetString()!)));
                break;
            case "opacity":
                _render.Post(new SetLayerOpacity(root.GetProperty("layer").GetInt32(), root.GetProperty("value").GetDouble()));
                break;
            case "fader":
                _render.Post(new SetMasterOpacity(root.GetProperty("value").GetDouble()));
                break;
            case "blackout":
                _render.Post(new SetBlackout(root.GetProperty("value").GetBoolean()));
                break;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private const string Page = """
    <!doctype html>
    <html lang="pt-br">
    <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>RWVideoWall — Controle remoto</title>
    <style>
      body { font-family: sans-serif; background:#111; color:#eee; margin:0; padding:12px; }
      #pinScreen input { font-size:24px; width:120px; text-align:center; }
      button { font-size:14px; padding:8px; margin:2px; border-radius:4px; border:1px solid #555; background:#222; color:#eee; }
      button.active { background:#2a6; }
      .layer { border:1px solid #333; margin:8px 0; padding:6px; }
      .row { display:flex; flex-wrap:wrap; gap:4px; align-items:center; }
      #blackoutBtn.on { background:#c00; }
      input[type=range] { width:150px; }
    </style>
    </head>
    <body>
      <div id="pinScreen">
        <h3>PIN</h3>
        <input id="pinInput" maxlength="6" inputmode="numeric" />
        <div id="pinError" style="color:#f66;margin-top:8px"></div>
        <button onclick="sendPin()">Entrar</button>
      </div>
      <div id="main" style="display:none">
        <div class="row">
          <button id="blackoutBtn" onclick="toggleBlackout()">BLACKOUT</button>
          <label>Master <input id="fader" type="range" min="0" max="1" step="0.01" oninput="sendFader(this.value)" /></label>
        </div>
        <div id="layers"></div>
      </div>
    <script>
      let ws, authed = false, state = null;
      function connect() {
        ws = new WebSocket(`ws://${location.host}/ws`);
        ws.onmessage = (ev) => {
          const msg = JSON.parse(ev.data);
          if (msg.type === 'auth') onAuth(msg);
          if (msg.type === 'state') { state = msg.data; render(); }
        };
        ws.onopen = () => { if (authed && lastPin) ws.send(JSON.stringify({type:'pin', value:lastPin})); };
        ws.onclose = () => setTimeout(connect, 2000);
      }
      let lastPin = null;
      function sendPin() {
        lastPin = document.getElementById('pinInput').value;
        ws.send(JSON.stringify({type:'pin', value:lastPin}));
      }
      function onAuth(msg) {
        authed = msg.ok;
        document.getElementById('pinError').textContent = msg.ok ? '' :
          (msg.locked ? 'Muitas tentativas erradas. Aguarde 2 minutos.' : 'PIN incorreto.');
        document.getElementById('pinScreen').style.display = msg.ok ? 'none' : 'block';
        document.getElementById('main').style.display = msg.ok ? 'block' : 'none';
      }
      function trigger(layer, column) { ws.send(JSON.stringify({type:'trigger_clip', layer, column})); }
      function toggleBlackout() {
        const btn = document.getElementById('blackoutBtn');
        const on = !btn.classList.contains('on');
        btn.classList.toggle('on', on);
        ws.send(JSON.stringify({type:'blackout', value:on}));
      }
      function sendFader(v) { ws.send(JSON.stringify({type:'fader', value:parseFloat(v)})); }
      function render() {
        if (!state) return;
        const div = document.getElementById('layers');
        div.innerHTML = '';
        state.layers.forEach((layer, li) => {
          const l = document.createElement('div');
          l.className = 'layer';
          const title = document.createElement('b');
          title.textContent = layer.name;
          l.appendChild(title);
          const row = document.createElement('div');
          row.className = 'row';
          layer.clips.forEach((clip, ci) => {
            const b = document.createElement('button');
            b.textContent = clip || '·';
            b.onclick = () => trigger(li, ci);
            row.appendChild(b);
          });
          l.appendChild(row);
          div.appendChild(l);
        });
      }
      connect();
    </script>
    </body>
    </html>
    """;
}
