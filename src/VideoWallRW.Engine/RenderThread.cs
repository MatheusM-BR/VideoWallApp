using System.Diagnostics;
using System.Threading.Channels;
using Serilog;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Stats;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Engine.Output;
using VideoWallRW.Engine.Playback;
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

    // ── Fase 2: estado de playback compositado ────────────────────────────────
    private Core.Model.Composition? _compositionData;
    private CompositionRenderer? _compositionRenderer;
    private readonly List<LayerRenderer> _layers = [];
    private bool _masterPaused;

    // ── Fase 4: fatiamento de saída ────────────────────────────────────────────
    private Core.Model.OutputSetup? _outputSetup;

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
        using var testScene = new TestSceneRenderer(_gfx);
        var clock = Stopwatch.StartNew();
        var statsClock = Stopwatch.StartNew();
        double lastTime = 0;

        try
        {
            while (_running)
            {
                while (_commands.Reader.TryRead(out var command))
                    Handle(command, testScene);

                // As janelas de saída vivem nesta thread; o pump precisa rodar aqui.
                OutputWindow.PumpMessages();

                if (_outputs.Count == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                double time = clock.Elapsed.TotalSeconds;
                double dt = time - lastTime;
                lastTime = time;
                if (dt > 0.1) dt = 0.1; // cap: evitar salto grande após pausa do debugger

                if (!_masterPaused)
                {
                    foreach (var layer in _layers)
                        layer.Tick(dt);
                }

                if (_compositionRenderer != null && _compositionData != null)
                {
                    // Passe de composição + saídas via CompositionRenderer.
                    var outputList = _outputs
                        .Select(o => (o.Rtv, o.Monitor.Width, o.Monitor.Height, ResolveScreen(o.Monitor.DeviceName, o.Monitor.Width, o.Monitor.Height)))
                        .ToList();
                    _compositionRenderer.Render(_gfx.Context, _compositionData, _layers, outputList);
                }
                else
                {
                    // Fase 0 fallback: gradiente de teste.
                    foreach (var output in _outputs)
                        testScene.Render(_gfx.Context, output.Rtv,
                            output.Monitor.Width, output.Monitor.Height, time);
                }

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
            foreach (var layer in _layers) layer.Dispose();
            _layers.Clear();
            _compositionRenderer?.Dispose();
            _compositionRenderer = null;
            _compositionData = null;
            foreach (var output in _outputs) output.Dispose();
            _outputs.Clear();
            OutputWindow.PumpMessages();
            Log.Information("Render thread encerrada");
        }
    }

    private void Handle(EngineCommand command, TestSceneRenderer testScene)
    {
        switch (command)
        {
            case EnableOutput(var deviceName):
                Enable(deviceName);
                break;
            case DisableOutput(var deviceName):
                Disable(deviceName);
                break;
            // Comandos de Fase 2
            case SyncComposition(var data):
                SyncComp(data);
                break;
            case TriggerClip(var layerIdx, var colIdx):
                OnTriggerClip(layerIdx, colIdx);
                break;
            case TriggerColumn(var colIdx):
                OnTriggerColumn(colIdx);
                break;
            case ClearLayer(var layerIdx):
                OnClearLayer(layerIdx);
                break;
            case ClearAll:
                OnClearAll();
                break;
            case SetLayerOpacity(var layerIdx, var opacity):
                OnSetLayerOpacity(layerIdx, opacity);
                break;
            case SetLayerState(var layerIdx, var blend, var bypass, var solo, var transitionSeconds):
                OnSetLayerState(layerIdx, blend, bypass, solo, transitionSeconds);
                break;
            case UpdateClip(var layerIdx, var colIdx, var clip):
                OnUpdateClip(layerIdx, colIdx, clip);
                break;
            case SetMasterPaused(var isPaused):
                _masterPaused = isPaused;
                break;
            case SetMasterOpacity(var opacity):
                OnSetMasterOpacity(opacity);
                break;
            case SyncOutputSetup(var setup):
                _outputSetup = setup.Clone();
                Log.Information("OutputSetup sincronizado com {N} telas configuradas.", _outputSetup.Screens.Count);
                break;
            default:
                Log.Warning("Comando não tratado: {Command}", command);
                break;
        }
    }

    /// <summary>
    /// Resolve a configuração de fatiamento (Fase 4) de um monitor ativo. Sem OutputSetup
    /// configurado (ou sem tela correspondente/desabilitada), cai no comportamento padrão:
    /// um slice único esticando a composição inteira no monitor — o mesmo de antes da Fase 4.
    /// </summary>
    private Core.Model.Screen ResolveScreen(string monitorDeviceName, int monitorWidth, int monitorHeight)
    {
        var compW = _compositionData?.Width ?? monitorWidth;
        var compH = _compositionData?.Height ?? monitorHeight;

        var screen = _outputSetup?.Screens.FirstOrDefault(s => s.MonitorDeviceName == monitorDeviceName);
        if (screen is not null && screen.Enabled && screen.Slices.Count > 0)
            return screen;

        return Core.Model.OutputSetup.CreateFullscreenScreen(monitorDeviceName, compW, compH, monitorWidth, monitorHeight);
    }

    private void SyncComp(Core.Model.Composition data)
    {
        // Descarta camadas antigas
        foreach (var layer in _layers) layer.Dispose();
        _layers.Clear();
        _compositionRenderer?.Dispose();
        
        _compositionData = data.Clone();
        _compositionRenderer = new CompositionRenderer(_gfx,
            _compositionData.Width, _compositionData.Height);
        
        foreach (var layerModel in _compositionData.Layers)
        {
            _layers.Add(new LayerRenderer(layerModel));
        }
        
        Log.Information("Composition Sincronizada com {N} camadas no Motor.", _layers.Count);
    }

    private void OnTriggerClip(int layerIdx, int colIdx)
    {
        if (_compositionData == null || layerIdx < 0 || layerIdx >= _compositionData.Layers.Count) return;
        var layerModel = _compositionData.Layers[layerIdx];
        if (colIdx < 0 || colIdx >= layerModel.Clips.Length) return;
        
        var clipModel = layerModel.Clips[colIdx];
        var layerRenderer = _layers[layerIdx];
        
        if (clipModel?.Source == null)
        {
            layerRenderer.SetPlayer(null, null); // Clear layer if slot is empty
            return;
        }

        if (_clipFactory == null)
        {
            Log.Warning("ClipFactory não mapeada no Engine.");
            return;
        }

        try
        {
            // Cria player e vincula atomicamente -> cross-fade implementável a partir de agora sem black frame
            var source = _clipFactory(clipModel, _compositionData.DefaultImageDuration);
            var player = new ClipPlayer(source, clipModel);
            player.EndReached += endedClip => OnClipEnded(layerIdx, endedClip);
            bool retrigger = layerRenderer.ActiveClip?.Id == clipModel.Id;
            layerRenderer.SetPlayer(player, clipModel, retrigger);
            Log.Information("Trigger disparado: Camada {L}, Coluna {C}", layerIdx, colIdx);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erro ao disparar clipe na camada {L}", layerIdx);
        }
    }

    private void OnTriggerColumn(int colIdx)
    {
        if (_compositionData == null) return;
        for (int i = 0; i < _compositionData.Layers.Count; i++)
        {
            OnTriggerClip(i, colIdx);
        }
    }

    private void OnClearLayer(int layerIdx)
    {
        if (layerIdx >= 0 && layerIdx < _layers.Count)
            _layers[layerIdx].SetPlayer(null, null);
    }

    private void OnClearAll()
    {
        foreach (var layer in _layers)
            layer.SetPlayer(null, null);
    }

    private void OnSetLayerOpacity(int layerIdx, double opacity)
    {
        if (_compositionData != null && layerIdx >= 0 && layerIdx < _compositionData.Layers.Count)
        {
            _compositionData.Layers[layerIdx].Opacity = Math.Clamp(opacity, 0.0, 1.0);
        }
    }

    private void OnSetMasterOpacity(double opacity)
    {
        if (_compositionData is not null)
            _compositionData.MasterOpacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    private void OnSetLayerState(int layerIdx, Core.Model.BlendMode blend, bool bypass, bool solo,
        double transitionSeconds)
    {
        if (_compositionData is null || layerIdx < 0 || layerIdx >= _compositionData.Layers.Count)
            return;

        var layer = _compositionData.Layers[layerIdx];
        layer.Blend = blend;
        layer.Bypass = bypass;
        layer.Solo = solo;
        layer.TransitionSeconds = Math.Clamp(transitionSeconds, 0.0, 10.0);
    }

    private void OnUpdateClip(int layerIdx, int colIdx, Core.Model.Clip? clip)
    {
        if (_compositionData is null || layerIdx < 0 || layerIdx >= _compositionData.Layers.Count)
            return;

        var clips = _compositionData.Layers[layerIdx].Clips;
        if (colIdx < 0 || colIdx >= clips.Length)
            return;

        var updatedClip = clip?.Copy();
        clips[colIdx] = updatedClip;
        if (updatedClip is not null)
            _layers[layerIdx].UpdateClip(updatedClip);
    }

    private void OnClipEnded(int layerIdx, Core.Model.Clip endedClip)
    {
        if (_compositionData is null || layerIdx < 0 || layerIdx >= _layers.Count ||
            _layers[layerIdx].ActiveClip?.Id != endedClip.Id)
            return;

        int currentColumn = Array.FindIndex(_compositionData.Layers[layerIdx].Clips,
            clip => clip?.Id == endedClip.Id);
        int nextColumn = FindAutopilotColumn(_compositionData.Layers[layerIdx], currentColumn, endedClip.Autopilot);
        if (nextColumn >= 0)
        {
            OnTriggerClip(layerIdx, nextColumn);
            return;
        }

        if (endedClip.PlayMode == Core.Model.PlayMode.OnceClear)
            OnClearLayer(layerIdx);
    }

    private static int FindAutopilotColumn(Core.Model.Layer layer, int currentColumn,
        Core.Model.AutopilotAction action)
    {
        if (action == Core.Model.AutopilotAction.None || layer.Clips.Length == 0)
            return -1;

        var populated = Enumerable.Range(0, layer.Clips.Length)
            .Where(index => layer.Clips[index] is not null)
            .ToArray();
        if (populated.Length == 0)
            return -1;

        return action switch
        {
            Core.Model.AutopilotAction.PlayFirstClip => populated[0],
            Core.Model.AutopilotAction.PlayRandomClip => populated[Random.Shared.Next(populated.Length)],
            Core.Model.AutopilotAction.PlayNextClip => FindAdjacent(layer, currentColumn, 1),
            Core.Model.AutopilotAction.PlayPreviousClip => FindAdjacent(layer, currentColumn, -1),
            _ => -1
        };
    }

    private static int FindAdjacent(Core.Model.Layer layer, int currentColumn, int direction)
    {
        for (int offset = 1; offset <= layer.Clips.Length; offset++)
        {
            int index = (currentColumn + direction * offset) % layer.Clips.Length;
            if (index < 0) index += layer.Clips.Length;
            if (layer.Clips[index] is not null)
                return index;
        }
        return -1;
    }

    // ── Factory injetada pelo App (evita dependência Engine → Media) ──────────
    private Func<Core.Model.Clip, double, IPlayableSource>? _clipFactory;

    /// <summary>
    /// Configura a fábrica de fontes de clip. Chamar antes de <see cref="Start"/>.
    /// </summary>
    public void SetClipFactory(Func<Core.Model.Clip, double, IPlayableSource> factory)
        => _clipFactory = factory;

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
        if (output is null) return;
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

        ClipSnapshot? clipShot = null;
        var firstLayer = _layers.FirstOrDefault();
        if (firstLayer?.ActivePlayer != null && firstLayer.ActiveClip != null && firstLayer.ActiveClip.Source is Core.Model.FileVideoSource vSrc)
        {
            double decodeFps = firstLayer.ActivePlayer.Source.DecodeFps;
            clipShot = new ClipSnapshot(vSrc.Path, firstLayer.ActivePlayer.Playhead, firstLayer.ActivePlayer.Source.Duration,
                firstLayer.ActivePlayer.State, decodeFps);
        }

        StatsUpdated?.Invoke(new EngineStats(outputs, clipShot));
    }
}
