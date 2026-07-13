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

    public LayerRenderer(Layer model)
    {
        Model = model;
    }

    /// <summary>
    /// Prepara a próxima fonte sem retirar a fonte atual. A promoção só acontece
    /// quando a nova fonte disponibiliza o primeiro frame, evitando um flash preto.
    /// </summary>
    public void SetPlayer(ClipPlayer? player, Clip? clip, bool cut = false)
    {
        PendingPlayer?.Dispose();
        PendingPlayer = null;
        PendingClip = null;
        _cutPending = false;

        if (player is null || clip is null)
        {
            ActivePlayer?.Dispose();
            OutgoingPlayer?.Dispose();
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
                OutgoingPlayer.Dispose();
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
            var pendingFrame = PendingPlayer.Source.AcquireFrame(PendingPlayer.Playhead);
            if (pendingFrame is not null)
            {
                var previous = ActivePlayer;
                var previousClip = ActiveClip;
                ActivePlayer = PendingPlayer;
                ActiveClip = PendingClip;
                PendingPlayer = null;
                PendingClip = null;

                if (previous is not null && !_cutPending && Model.TransitionSeconds > 0)
                {
                    OutgoingPlayer?.Dispose();
                    OutgoingPlayer = previous;
                    OutgoingClip = previousClip;
                    _transitionElapsed = 0;
                }
                else
                {
                    previous?.Dispose();
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
        ActivePlayer?.Dispose();
        PendingPlayer?.Dispose();
        OutgoingPlayer?.Dispose();
        ActivePlayer = null;
        PendingPlayer = null;
        OutgoingPlayer = null;
        ActiveClip = null;
        PendingClip = null;
    }
}
