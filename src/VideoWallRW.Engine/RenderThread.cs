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
    private int _dropCount; // T6.7: frames com frametime > 1.5x o alvo (~60fps), acumulado entre publicações de stats

    // ── Fase 2: estado de playback compositado ────────────────────────────────
    private Core.Model.Composition? _compositionData;
    private CompositionRenderer? _compositionRenderer;
    private readonly List<LayerRenderer> _layers = [];
    private bool _masterPaused;

    // ── Fase 4: fatiamento de saída ────────────────────────────────────────────
    private Core.Model.OutputSetup? _outputSetup;
    private bool _identify;

    // Criação assíncrona de players: a factory (abre FFmpeg, demuxa, cria texturas)
    // roda em Task.Run; o resultado volta pra render thread por esta fila. A geração
    // por layer descarta anexos obsoletos (operador disparou outro clip no meio).
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _deferred = new();
    private readonly Dictionary<int, int> _triggerGeneration = [];

    // ── Fase 7: cenas ───────────────────────────────────────────────────────────
    private List<Core.Persistence.SceneSnapshot> _scenes = [];

    // ── Fase 8: modo instalação fixa (sequenciador + agendador) ───────────────
    private Core.Model.InstallationConfig _installation = new();
    private bool _sequencerRunning;
    private int _sequencerStep;
    private double _sequencerElapsed;
    private double _schedulerAccum;
    public event Action<int, bool>? SequencerStepChanged; // (stepIndex, running) — para a UI acompanhar

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

                // Executa criações de player concluídas em background (fix do engasgo:
                // abrir FFmpeg do próximo clip fora da render thread). Antes do check de
                // saídas — o clip anexa mesmo sem monitor ativo, como era no fluxo síncrono.
                while (_deferred.TryDequeue(out var action))
                    action();

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
                if (dt > 1.0 / 40.0) _dropCount++; // frametime > 1.5x o alvo de 60fps (~16.7ms)
                if (dt > 0.1) dt = 0.1; // cap: evitar salto grande após pausa do debugger

                // Pausa global = pausa a SEQUÊNCIA (autopilot/sequenciador), não o
                // conteúdo: os vídeos continuam tocando no telão.
                foreach (var layer in _layers)
                    layer.Tick(dt);
                if (!_masterPaused && _sequencerRunning)
                    TickSequencer(dt);

                _schedulerAccum += dt;
                if (_schedulerAccum >= 10.0)
                {
                    _schedulerAccum = 0;
                    EvaluateSchedule();
                }

                if (_compositionRenderer != null && _compositionData != null)
                {
                    // Passe de composição + saídas via CompositionRenderer.
                    var outputList = _outputs
                        .Select(o => (o.Rtv, o.Monitor.Width, o.Monitor.Height, ResolveScreen(o.Monitor.DeviceName, o.Monitor.Width, o.Monitor.Height)))
                        .ToList();
                    _compositionRenderer.Render(_gfx.Context, _compositionData, _layers, outputList, _identify);
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
            case SetIdentify(var enabled):
                _identify = enabled;
                break;
            case SetBlackout(var enabled):
                if (_compositionData is not null)
                    _compositionData.Blackout = enabled;
                break;
            case LoadProject(var project):
                OnLoadProject(project);
                break;
            case SyncScenes(var scenes):
                _scenes = scenes;
                break;
            case TriggerScene(var sceneId):
                OnTriggerScene(sceneId);
                break;
            case SyncInstallation(var installation):
                _installation = installation.Clone();
                break;
            case SetSequencerRunning(var running):
                OnSetSequencerRunning(running);
                break;
            case SetLayerAutopilot(var layerIdx, var enabled):
                if (_compositionData is not null && layerIdx >= 0 && layerIdx < _compositionData.Layers.Count)
                    _compositionData.Layers[layerIdx].AutopilotEnabled = enabled;
                break;
            case ReloadClip(var layerIdx, var colIdx):
                // Reabre a fonte do zero (o ClipFactory lê o arquivo do disco de novo) —
                // o editor de imagem (Fase 10) salva em cima do arquivo e chama isso pra
                // o telão atualizar sem frame preto, mesmo fluxo de crossfade do trigger normal.
                OnTriggerClip(layerIdx, colIdx);
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

    /// <summary>
    /// Aplica um projeto carregado inteiro de uma vez: composição (nova grade,
    /// engine reconstruído do zero — nenhum clip fica "no ar" automaticamente,
    /// carregar projeto é sempre um estado parado), output setup e cenas.
    /// </summary>
    private void OnLoadProject(Core.Persistence.ProjectFile project)
    {
        SyncComp(project.Composition);
        _outputSetup = project.Output.Clone();
        _scenes = [.. project.Snapshots];
        _installation = project.Installation.Clone();
        _sequencerRunning = false;
        _sequencerStep = 0;
        _sequencerElapsed = 0;
        Log.Information("Projeto carregado: {Layers} layers, {Scenes} cenas.", _layers.Count, _scenes.Count);
    }

    /// <summary>
    /// Liga/desliga o sequenciador (Fase 8 T8.4). Ao ligar, dispara o passo atual
    /// imediatamente (não espera a duração do passo para o primeiro clipe aparecer).
    /// </summary>
    private void OnSetSequencerRunning(bool running)
    {
        _sequencerRunning = running;
        if (!running)
        {
            SequencerStepChanged?.Invoke(_sequencerStep, false);
            return;
        }
        if (_installation.Sequencer.Steps.Count == 0)
        {
            _sequencerRunning = false;
            return;
        }
        _sequencerStep = 0;
        _sequencerElapsed = 0;
        OnTriggerColumn(_installation.Sequencer.Steps[0].Column);
        SequencerStepChanged?.Invoke(_sequencerStep, true);
    }

    private void TickSequencer(double dt)
    {
        var steps = _installation.Sequencer.Steps;
        if (steps.Count == 0)
        {
            _sequencerRunning = false;
            return;
        }

        _sequencerElapsed += dt;
        double duration = steps[_sequencerStep].Seconds;
        if (_sequencerElapsed < duration)
            return;

        _sequencerElapsed = 0;
        int next = _sequencerStep + 1;
        if (next >= steps.Count)
        {
            if (!_installation.Sequencer.Loop)
            {
                _sequencerRunning = false;
                SequencerStepChanged?.Invoke(_sequencerStep, false);
                return;
            }
            next = 0;
        }

        _sequencerStep = next;
        OnTriggerColumn(steps[_sequencerStep].Column);
        SequencerStepChanged?.Invoke(_sequencerStep, true);
    }

    /// <summary>
    /// Avaliador do agendador (Fase 8 T8.5), chamado a cada ~10s. Casa dia da semana +
    /// hora:minuto; <see cref="ScheduleRule.LastRunDate"/> evita disparar de novo no
    /// mesmo dia (a janela de 10s pode cair várias vezes dentro do mesmo minuto).
    /// </summary>
    private void EvaluateSchedule()
    {
        if (_installation.ScheduleRules.Count == 0)
            return;

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var nowMinute = new TimeOnly(now.Hour, now.Minute);

        foreach (var rule in _installation.ScheduleRules)
        {
            if (!rule.Enabled || rule.LastRunDate == today)
                continue;
            if (Array.IndexOf(rule.Days, now.DayOfWeek) < 0)
                continue;
            if (new TimeOnly(rule.Time.Hour, rule.Time.Minute) != nowMinute)
                continue;

            ApplyScheduleAction(rule);
            rule.LastRunDate = today;
            Log.Information("Regra de agenda disparada: {Name}", rule.Name);
        }
    }

    private void ApplyScheduleAction(Core.Model.ScheduleRule rule)
    {
        switch (rule.Action)
        {
            case Core.Model.ScheduleActionType.TriggerColumn:
                OnTriggerColumn(rule.ColumnOrParam);
                break;
            case Core.Model.ScheduleActionType.TriggerScene:
                if (rule.SceneId is { } sceneId)
                    OnTriggerScene(sceneId);
                break;
            case Core.Model.ScheduleActionType.StartSequencer:
                OnSetSequencerRunning(true);
                break;
            case Core.Model.ScheduleActionType.StopSequencer:
                OnSetSequencerRunning(false);
                break;
            case Core.Model.ScheduleActionType.BlackoutOn:
                if (_compositionData is not null) _compositionData.Blackout = true;
                break;
            case Core.Model.ScheduleActionType.BlackoutOff:
                if (_compositionData is not null) _compositionData.Blackout = false;
                break;
        }
    }

    /// <summary>
    /// Dispara uma cena salva: aplica o clip ativo e a opacidade de cada layer e o
    /// master fader de uma vez. Cada layer usa seu próprio crossfade (se configurado)
    /// — nunca aparece um buraco preto mesmo trocando várias layers juntas.
    /// </summary>
    private void OnTriggerScene(Guid sceneId)
    {
        var scene = _scenes.FirstOrDefault(s => s.Id == sceneId);
        if (scene is null || _compositionData is null)
            return;

        for (int i = 0; i < _compositionData.Layers.Count; i++)
        {
            var layer = _compositionData.Layers[i];
            if (scene.ActiveColumns.TryGetValue(layer.Id, out int column))
            {
                if (column < 0)
                    OnClearLayer(i);
                else
                    OnTriggerClip(i, column);
            }
            if (scene.LayerOpacities.TryGetValue(layer.Id, out double opacity))
                OnSetLayerOpacity(i, opacity);
        }

        _compositionData.MasterOpacity = Math.Clamp(scene.MasterFader, 0.0, 1.0);
        Log.Information("Cena disparada: {Name}", scene.Name);
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

        // A factory abre arquivo/FFmpeg (dezenas~centenas de ms) — em background, senão
        // o frame atual engasga na troca de clip. O anexo volta pela fila _deferred e é
        // descartado se outro trigger da mesma layer aconteceu no meio (geração).
        int generation = _triggerGeneration.GetValueOrDefault(layerIdx) + 1;
        _triggerGeneration[layerIdx] = generation;
        var factory = _clipFactory;
        double defaultImageDuration = _compositionData.DefaultImageDuration;

        Task.Run(() =>
        {
            try
            {
                var source = factory(clipModel, defaultImageDuration);
                _deferred.Enqueue(() =>
                {
                    if (_triggerGeneration.GetValueOrDefault(layerIdx) != generation ||
                        layerIdx >= _layers.Count)
                    {
                        source.Dispose(); // trigger obsoleto — outro clip ganhou a corrida
                        return;
                    }
                    var player = new ClipPlayer(source, clipModel);
                    player.EndReached += endedClip => OnClipEnded(layerIdx, endedClip);
                    bool retrigger = _layers[layerIdx].ActiveClip?.Id == clipModel.Id;
                    _layers[layerIdx].SetPlayer(player, clipModel, retrigger);
                    Log.Information("Trigger disparado: Camada {L}, Coluna {C}", layerIdx, colIdx);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Erro ao abrir clipe da camada {L}", layerIdx);
            }
        });
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
        // Pausa global = segurar a sequência: o clip que terminou fica no último
        // frame e o autopilot não avança até o operador retomar.
        if (_masterPaused)
            return;

        if (_compositionData is null || layerIdx < 0 || layerIdx >= _layers.Count ||
            _layers[layerIdx].ActiveClip?.Id != endedClip.Id)
            return;

        // Autopilot da layer desligado: clip que terminou segura no último frame.
        if (!_compositionData.Layers[layerIdx].AutopilotEnabled)
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

        int activeClips = _layers.Count(l => l.ActivePlayer is not null);

        // Posição de cada clip no ar — a UI desenha a agulha na barra de trim.
        var positions = new List<LayerClipPosition>();
        for (int i = 0; i < _layers.Count; i++)
        {
            if (_layers[i].ActivePlayer is { } player && _layers[i].ActiveClip is { } clip)
                positions.Add(new LayerClipPosition(i, clip.Id, player.Playhead));
        }

        StatsUpdated?.Invoke(new EngineStats(outputs, clipShot, _dropCount, activeClips, positions));
        _dropCount = 0;
    }
}
