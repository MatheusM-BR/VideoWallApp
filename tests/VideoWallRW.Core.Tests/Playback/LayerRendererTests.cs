using System.Runtime.CompilerServices;
using VideoWallRW.Core.Model;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Playback;
using VideoWallRW.Engine.Rendering;
using Vortice.Direct3D11;
using Xunit;

namespace VideoWallRW.Core.Tests.Playback;

/// <summary>
/// Troca de clipe na camada — onde os bugs reais de operação aconteceram (flash preto,
/// camada congelada por fonte com erro, crash no descarte, autopilot em tempestade).
/// Sem GPU: as fontes devolvem uma "textura" de mentira que nunca chega ao D3D11.
/// </summary>
public sealed class LayerRendererTests
{
    /// <summary>Textura falsa: só a identidade importa (ponteiro inválido, nunca usado).</summary>
    private static ID3D11ShaderResourceView FakeSrv()
    {
        var srv = (ID3D11ShaderResourceView)RuntimeHelpers.GetUninitializedObject(typeof(ID3D11ShaderResourceView));
        GC.SuppressFinalize(srv); // não deixar o finalizador do COM tocar num ponteiro falso
        return srv;
    }

    private sealed class Source(string name, double duration = 10) : IPlayableSource
    {
        public string Name { get; } = name;
        public ID3D11ShaderResourceView Srv { get; } = FakeSrv();
        public bool HasFrame { get; set; } = true;
        public SourceState State { get; set; } = SourceState.Playing;
        public string? ErrorMessage => State == SourceState.Error ? "falhou" : null;
        public int Width => 1920;
        public int Height => 1080;
        public double Duration => duration;
        public bool SupportsSeek => true;
        public bool Disposed { get; private set; }
        public event Action? EndReached { add { } remove { } }
        public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds) => HasFrame ? Srv : null;
        public void Seek(double seconds) { }
        public void Start() { }
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }

    private static Clip NewClip() => new(new FileVideoSource("x.mp4")) { PlayMode = PlayMode.Loop };

    private static (LayerRenderer Layer, List<IDisposable> Deferred) NewLayer(double transition = 0)
    {
        var deferred = new List<IDisposable>();
        var layer = new LayerRenderer(new Layer { TransitionSeconds = transition }, deferred.Add);
        return (layer, deferred);
    }

    private static Source Trigger(LayerRenderer layer, string name, bool hasFrame = true, bool cut = false)
    {
        var source = new Source(name) { HasFrame = hasFrame };
        var clip = NewClip();
        layer.SetPlayer(new ClipPlayer(source, clip), clip, cut);
        return source;
    }

    [Fact]
    public void NewClip_GoesOnAirOnlyWhenItsFirstFrameIsReady_PreviousStaysUntilThen()
    {
        var (layer, _) = NewLayer();
        var a = Trigger(layer, "A");
        Assert.Same(a.Srv, Assert.Single(layer.AcquireFrames()).Srv);

        var b = Trigger(layer, "B", hasFrame: false); // abrindo (preroll)
        Assert.Same(a.Srv, Assert.Single(layer.AcquireFrames()).Srv); // sem flash preto

        b.HasFrame = true;
        Assert.Same(b.Srv, Assert.Single(layer.AcquireFrames()).Srv);
        Assert.Same(b, layer.ActivePlayer!.Source);
    }

    [Fact]
    public void SourceInError_IsDroppedAndTheLayerKeepsWhatIsOnAir()
    {
        var (layer, deferred) = NewLayer();
        var a = Trigger(layer, "A");
        layer.AcquireFrames();

        var broken = Trigger(layer, "quebrado", hasFrame: false);
        broken.State = SourceState.Error;
        Assert.Same(a.Srv, Assert.Single(layer.AcquireFrames()).Srv);

        // Antes isto ficava pendente pra sempre e a camada congelava; agora o próximo entra.
        var c = Trigger(layer, "C");
        Assert.Same(c.Srv, Assert.Single(layer.AcquireFrames()).Srv);
        Assert.Contains(deferred, d => ((ClipPlayer)d).Source == broken);
    }

    [Fact]
    public void Dissolve_ShowsBothClipsThenReleasesTheOldOneAfterTransition()
    {
        var (layer, deferred) = NewLayer(transition: 1.0);
        var a = Trigger(layer, "A");
        layer.AcquireFrames();
        var b = Trigger(layer, "B");

        var frames = layer.AcquireFrames();
        Assert.Equal(2, frames.Count);
        Assert.Same(a.Srv, frames[0].Srv);
        Assert.Same(b.Srv, frames[1].Srv);

        layer.Tick(0.5);
        frames = layer.AcquireFrames();
        Assert.Equal(0.5f, frames[1].Opacity, 3);

        layer.Tick(0.6); // passou do tempo de transição
        Assert.Same(b.Srv, Assert.Single(layer.AcquireFrames()).Srv);
        Assert.Contains(deferred, d => ((ClipPlayer)d).Source == a);
    }

    [Fact]
    public void Cut_IgnoresTransitionAndSwapsImmediately()
    {
        var (layer, deferred) = NewLayer(transition: 2.0);
        var a = Trigger(layer, "A");
        layer.AcquireFrames();
        var b = Trigger(layer, "B", cut: true);

        Assert.Same(b.Srv, Assert.Single(layer.AcquireFrames()).Srv);
        Assert.Contains(deferred, d => ((ClipPlayer)d).Source == a);
    }

    [Fact]
    public void RetriggerDuringPreroll_ReleasesTheAbandonedPendingSource()
    {
        var (layer, deferred) = NewLayer();
        Trigger(layer, "A");
        layer.AcquireFrames();
        var slow = Trigger(layer, "lento", hasFrame: false);
        var c = Trigger(layer, "C");

        Assert.Contains(deferred, d => ((ClipPlayer)d).Source == slow);
        Assert.Same(c.Srv, Assert.Single(layer.AcquireFrames()).Srv);
    }

    [Fact]
    public void Disposal_GoesToTheCreationThreadNotInline()
    {
        var (layer, deferred) = NewLayer();
        var a = Trigger(layer, "A");
        layer.AcquireFrames();
        Trigger(layer, "B");
        layer.AcquireFrames();

        // Descarte nativo (FFmpeg/D3D) não roda na render thread: vai pra fila.
        Assert.False(a.Disposed);
        Assert.Single(deferred);
    }

    [Fact]
    public void ClearLayer_ReleasesEverything()
    {
        var (layer, deferred) = NewLayer(transition: 1.0);
        Trigger(layer, "A");
        layer.AcquireFrames();
        Trigger(layer, "B");
        layer.AcquireFrames();
        Trigger(layer, "C", hasFrame: false);

        layer.SetPlayer(null, null);

        Assert.Empty(layer.AcquireFrames());
        Assert.Null(layer.ActivePlayer);
        Assert.Equal(3, deferred.Count);
    }

    [Fact]
    public void Bypass_ShowsNothingButKeepsThePlayer()
    {
        var (layer, _) = NewLayer();
        Trigger(layer, "A");
        layer.Model.Bypass = true;
        Assert.Empty(layer.AcquireFrames());
        layer.Model.Bypass = false;
        Assert.Single(layer.AcquireFrames());
    }
}
