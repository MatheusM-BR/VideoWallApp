using VideoWallRW.Core.Model;
using VideoWallRW.Core.Stats;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Playback;
using Vortice.Direct3D11;
using Xunit;

namespace VideoWallRW.Core.Tests.Playback;

public sealed class ClipPlayerTests
{
    [Fact]
    public void Loop_RespectsInAndOutPoints()
    {
        var source = new FakeSource(duration: 10);
        var clip = new Clip(new FileVideoSource("loop.mp4"))
        {
            InPoint = 2,
            OutPoint = 5,
            PlayMode = PlayMode.Loop
        };
        using var player = new ClipPlayer(source, clip);

        player.Tick(3.5);

        Assert.Equal(2.5, player.Playhead, precision: 6);
        Assert.Equal([2.5], source.SeekRequests);
    }

    [Fact]
    public void OnceHold_PausesAtOutPointAndRaisesEndOnlyOnce()
    {
        var source = new FakeSource(duration: 10);
        var clip = new Clip(new FileVideoSource("hold.mp4"))
        {
            InPoint = 1,
            OutPoint = 3,
            PlayMode = PlayMode.OnceHold
        };
        using var player = new ClipPlayer(source, clip);
        int ends = 0;
        player.EndReached += _ => ends++;

        player.Tick(5);
        player.Tick(5);

        Assert.Equal(3, player.Playhead, precision: 6);
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(1, ends);
    }

    [Fact]
    public void OnceClear_StopsAtOutPointAndRaisesEnd()
    {
        var source = new FakeSource(duration: 2);
        var clip = new Clip(new FileImageSource("slide.png"))
        {
            PlayMode = PlayMode.OnceClear
        };
        using var player = new ClipPlayer(source, clip);
        bool ended = false;
        player.EndReached += _ => ended = true;

        player.Tick(2.1);

        Assert.Equal(2, player.Playhead, precision: 6);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.True(ended);
    }

    private sealed class FakeSource(double duration) : IPlayableSource
    {
        public List<double> SeekRequests { get; } = [];
        public SourceState State => SourceState.Playing;
        public string? ErrorMessage => null;
        public int Width => 1;
        public int Height => 1;
        public double Duration => duration;
        public bool SupportsSeek => true;
        public event Action? EndReached { add { } remove { } }

        public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds) => null;
        public void Seek(double seconds) => SeekRequests.Add(seconds);
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
