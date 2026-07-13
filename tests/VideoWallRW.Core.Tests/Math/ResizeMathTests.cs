using VideoWallRW.Core.Math;
using VideoWallRW.Core.Model;
using Xunit;

namespace VideoWallRW.Core.Tests.Math;

public class ResizeMathTests
{
    [Fact]
    public void Compute_Original_ReturnsCenteredWithoutScaling()
    {
        var rect = ResizeMath.Compute(ResizeMode.Original, 800, 600, 1920, 1080);
        Assert.Equal(800, rect.Width);
        Assert.Equal(600, rect.Height);
        Assert.Equal((1920 - 800) / 2f, rect.X);
        Assert.Equal((1080 - 600) / 2f, rect.Y);
    }

    [Fact]
    public void Compute_Stretch_FillsEntireComposition()
    {
        var rect = ResizeMath.Compute(ResizeMode.Stretch, 800, 600, 1920, 1080);
        Assert.Equal(1920, rect.Width);
        Assert.Equal(1080, rect.Height);
        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
    }

    [Fact]
    public void Compute_Fit_AddsPillarboxToTallVideo()
    {
        // Video 9:16 portrait in 16:9 monitor
        var rect = ResizeMath.Compute(ResizeMode.Fit, 1080, 1920, 1920, 1080);
        Assert.Equal(1080, rect.Height); // Height matched
        Assert.Equal(1080 * (1080 / 1920f), rect.Width, 2);
        Assert.True(rect.X > 0);
        Assert.Equal(0, rect.Y);
    }

    [Fact]
    public void Compute_Fit_AddsLetterboxToWideVideo()
    {
        // Video 21:9 in 16:9 monitor
        var rect = ResizeMath.Compute(ResizeMode.Fit, 2560, 1080, 1920, 1080);
        Assert.Equal(1920, rect.Width); // Width matched
        Assert.Equal(1920 * (1080 / 2560f), rect.Height, 2);
        Assert.Equal(0, rect.X);
        Assert.True(rect.Y > 0);
    }

    [Fact]
    public void Compute_Fill_CropsWideVideoVertically()
    {
        // Video 21:9 in 16:9 monitor: Fill forces aspect zoom. Height matches, width overflows screen.
        var rect = ResizeMath.Compute(ResizeMode.Fill, 2560, 1080, 1920, 1080);
        Assert.Equal(1080, rect.Height);
        Assert.Equal(2560, rect.Width);
        Assert.Equal((1920 - 2560) / 2f, rect.X);
        Assert.Equal(0, rect.Y);
    }

    [Fact]
    public void Compute_Fill_CropsTallVideoHorizontally()
    {
        // Video 9:16 in 16:9 monitor: Fill forces aspect zoom. Width matches, height overflows.
        var rect = ResizeMath.Compute(ResizeMode.Fill, 1080, 1920, 1920, 1080);
        Assert.Equal(1920, rect.Width);
        Assert.Equal(1920 * (1920 / 1080f), rect.Height, 2);
        Assert.Equal(0, rect.X);
        Assert.True(rect.Y < 0);
    }
}
