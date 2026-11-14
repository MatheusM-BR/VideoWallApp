using System.Diagnostics;
using Serilog;
using VideoWallRW.Core.Model;
using VideoWallRW.Engine.Playback;

namespace VideoWallRW.Engine.Rendering;

/// <summary>
/// Mantém o estado de uma camada gráfica, possuindo o player ativo (e futuramente
/// o player antigo durante crossfades).
/// A camada não "desenha" a si mesma; ela provê a textura de saída e a matriz de resize.
/// </summary>
public sealed class LayerRenderer : IDisposable
{
    public readonly record struct Frame(Vortice.Direct3D11.ID3D11ShaderResourceView Srv, float Opacity);

    public Layer Model { get; }
    
    public ClipPlayer? ActivePlayer { get; private set; }
    public Clip? ActiveClip { get; private set; }
    private ClipPlayer? PendingPlayer { get; set; }
    private Clip? PendingClip { get; set; }
    private ClipPlayer? OutgoingPlayer { get; set; }
    private Clip? OutgoingClip { get; set; }
    private bool _cutPending;
    private double _transitionElapsed;
    private readonly List<Frame> _frames = [];
    private readonly Stopwatch _triggerLatency = new();

    /// <summary>
    /// Onde o descarte de um player (que fecha FFmpeg/HAP e libera texturas D3D) é
    /// executado. O RenderThread manda isso pra MESMA thread que CRIA as fontes, pra
    /// que teardown nativo nunca rode em paralelo com setup nativo — é exatamente na
    /// troca de clipe (fim de vídeo/tempo de imagem) que os dois se encontravam.
    /// Null = descarta na hora (usado em testes/shutdown).
    /// </summary>
    private readonly Action<IDisposable>? _deferDispose;

    public LayerRenderer(Layer model, Action<IDisposable>? deferDispose = null)
    {
        Model = model;
        _deferDispose = deferDispose;
    }

    private void Release(ClipPlayer? player)
    {
        if (player is null) return;
        if (_deferDispose is not null) _deferDispose(player);
        else player.Dispose();
    }

    /// <summary>
    /// Prepara a próxima fonte sem retirar a fonte atual. A promoção só acontece
    /// quando a nova fonte disponibiliza o primeiro frame, evitando um flash preto.
    /// </summary>
    public void SetPlayer(ClipPlayer? player, Clip? clip, bool cut = false)
    {
        Release(PendingPlayer);
        PendingPlayer = null;
        PendingClip = null;
        _cutPending = false;

        if (player is null || clip is null)
        {
            Release(ActivePlayer);
            Release(OutgoingPlayer);
            ActivePlayer = null;
            ActiveClip = null;
            OutgoingPlayer = null;
            OutgoingClip = null;
            _transitionElapsed = 0;
            return;
        }

        PendingPlayer = player;
        PendingClip = clip;
        _cutPending = cut;
        PendingPlayer.SetAudioFade(0);
        _triggerLatency.Restart();
    }

    public void Tick(double dt)
    {
        ActivePlayer?.Tick(dt);
        PendingPlayer?.Tick(dt);
        OutgoingPlayer?.Tick(dt);

        if (OutgoingPlayer is not null)
        {
            _transitionElapsed += dt;
            OutgoingPlayer.SetAudioFade(1.0 - TransitionProgress);
            ActivePlayer?.SetAudioFade(TransitionProgress);
            if (_transitionElapsed >= Model.TransitionSeconds)
            {
                Release(OutgoingPlayer);
                OutgoingPlayer = null;
                OutgoingClip = null;
                _transitionElapsed = 0;
            }
        }
        else
        {
            ActivePlayer?.SetAudioFade(1.0);
        }
    }

    /// <summary>
    /// Retorna o SRV atual para o ciclo de Present.
    /// Retorna null se não houver videoclipe rolando ou se a camada for despachada por bypass.
    /// </summary>
    public IReadOnlyList<Frame> AcquireFrames()
    {
        _frames.Clear();
        if (Model.Bypass)
            return _frames;

        if (PendingPlayer is not null)
        {
            // Fonte que falhou (arquivo faltando/corrompido) NUNCA entrega frame — sem
            // isto ela ficava pendente pra sempre, o clipe atual nunca era substituído e
            // a layer CONGELAVA ("não passa para o próximo vídeo"). Descarta e segue com
            // o que já está no ar; o autopilot tenta de novo no próximo fim de ciclo.
            if (PendingPlayer.Source.State == SourceState.Error)
            {
                Log.Warning("Fonte do clipe '{Name}' falhou ({Erro}) — pulando",
                    PendingClip?.Name, PendingPlayer.Source.ErrorMessage);
                Release(PendingPlayer);
                PendingPlayer = null;
                PendingClip = null;
                _cutPending = false;
            }
        }

        if (PendingPlayer is not null)
        {
            var pendingFrame = PendingPlayer.Source.AcquireFrame(PendingPlayer.Playhead);
            if (pendingFrame is not null)
            {
                Log.Debug("Trigger→ar: {Ms}ms ({Name})", _triggerLatency.ElapsedMilliseconds, PendingClip?.Name);
                var previous = ActivePlayer;
                var previousClip = ActiveClip;
                ActivePlayer = PendingPlayer;
                ActiveClip = PendingClip;
                PendingPlayer = null;
                PendingClip = null;

                if (previous is not null && !_cutPending && Model.TransitionSeconds > 0)
                {
                    Release(OutgoingPlayer);
                    OutgoingPlayer = previous;
                    OutgoingClip = previousClip;
                    _transitionElapsed = 0;
                }
                else
                {
                    Release(previous);
                }

                _cutPending = false;
                if (OutgoingPlayer is null)
                    ActivePlayer.SetAudioFade(1.0);
            }
        }

        if (OutgoingPlayer is not null)
        {
            var outgoingFrame = OutgoingPlayer.Source.AcquireFrame(OutgoingPlayer.Playhead);
            if (outgoingFrame is not null)
                // Em blend normal o novo quadro já atenua o destino por (1 - t).
                // Atenuar o antigo aqui aplicaria esse fator duas vezes e escureceria o dissolve.
                _frames.Add(new Frame(outgoingFrame, 1.0f));
        }

        if (ActivePlayer is not null)
        {
            var activeFrame = ActivePlayer.Source.AcquireFrame(ActivePlayer.Playhead);
            if (activeFrame is not null)
                _frames.Add(new Frame(activeFrame, (float)(OutgoingPlayer is null ? 1.0 : TransitionProgress)));
        }

        return _frames;
    }

    private double TransitionProgress => OutgoingPlayer is null || Model.TransitionSeconds <= 0
        ? 1.0
        : Math.Clamp(_transitionElapsed / Model.TransitionSeconds, 0.0, 1.0);

    /// <summary>Atualiza os metadados de um slot sem reiniciar a fonte em execução.</summary>
    public void UpdateClip(Clip clip)
    {
        if (ActiveClip?.Id == clip.Id)
        {
            ActiveClip = clip;
            ActivePlayer?.UpdateSettings(clip);
        }
        if (PendingClip?.Id == clip.Id)
        {
            PendingClip = clip;
            PendingPlayer?.UpdateSettings(clip);
        }
        if (OutgoingClip?.Id == clip.Id)
        {
            OutgoingClip = clip;
            OutgoingPlayer?.UpdateSettings(clip);
        }
    }

    public void Dispose()
    {
        Release(ActivePlayer);
        Release(PendingPlayer);
        Release(OutgoingPlayer);
        ActivePlayer = null;
        PendingPlayer = null;
        OutgoingPlayer = null;
        ActiveClip = null;
        PendingClip = null;
    }
}
