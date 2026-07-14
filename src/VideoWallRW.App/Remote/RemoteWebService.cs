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
/// produtor da fila do engine. PIN de 4 dígitos gerado por sessão (log + status bar);
/// sem PIN configurável na UI ainda (backlog).
/// </summary>
public sealed class RemoteWebService : IAsyncDisposable
{
    private readonly RenderThread _render;
    private readonly Func<object> _stateSnapshot;
    private readonly string _pin;
    private WebApplication? _app;

    public string Pin => _pin;

    public RemoteWebService(RenderThread render, Func<object> stateSnapshot)
    {
        _render = render;
        _stateSnapshot = stateSnapshot;
        _pin = Random.Shared.Next(0, 10000).ToString("D4");
    }

    public async Task StartAsync(int port = 8080)
    {
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            _app = builder.Build();
            _app.UseWebSockets();

            _app.MapGet("/", () => Results.Content(Page, "text/html; charset=utf-8"));
            _app.Map("/ws", HandleWebSocket);

            await _app.StartAsync();
            Log.Information("Controle remoto web em http://<ip-da-maquina>:{Port}  PIN: {Pin}", port, _pin);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao iniciar o controle remoto web na porta {Port} — desabilitado.", port);
        }
    }

    private async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

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
                HandleMessage(text, ref authenticated);
            }
        }
        catch (WebSocketException) { /* cliente caiu — encerra normalmente */ }
        finally
        {
            pushTimer.Dispose();
            await pushTask;
        }
    }

    private void HandleMessage(string json, ref bool authenticated)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return; }

        string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (!authenticated)
        {
            if (type == "pin" && root.TryGetProperty("value", out var pinValue) && pinValue.GetString() == _pin)
                authenticated = true;
            return;
        }

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

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.StopAsync();
    }

    private const string Page = """
    <!doctype html>
    <html lang="pt-br">
    <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>VideoWallRW — Controle remoto</title>
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
        <input id="pinInput" maxlength="4" inputmode="numeric" />
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
          if (msg.type === 'state') { state = msg.data; render(); }
        };
        ws.onclose = () => setTimeout(connect, 2000);
      }
      function sendPin() {
        const v = document.getElementById('pinInput').value;
        ws.send(JSON.stringify({type:'pin', value:v}));
        authed = true;
        document.getElementById('pinScreen').style.display = 'none';
        document.getElementById('main').style.display = 'block';
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
          l.innerHTML = `<b>${layer.name}</b>`;
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
