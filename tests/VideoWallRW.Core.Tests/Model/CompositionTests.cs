using VideoWallRW.Core.Model;
using Xunit;

namespace VideoWallRW.Core.Tests.Model;

public class CompositionTests
{
    [Fact]
    public void ColumnCount_Change_ResizesAllLayerClipsArrays()
    {
        var comp = new Composition();
        var layer1 = new Layer();
        var layer2 = new Layer();

        comp.AddLayer(layer1);
        comp.AddLayer(layer2);

        // Act
        comp.ColumnCount = 10;

        // Assert
        Assert.Equal(10, layer1.Clips.Length);
        Assert.Equal(10, layer2.Clips.Length);
        
        comp.ColumnCount = 4;
        Assert.Equal(4, layer1.Clips.Length);
        Assert.Equal(4, layer2.Clips.Length);
    }

    [Fact]
    public void AddLayer_AlignsLayerClipsLengthToComposition()
    {
        var comp = new Composition { ColumnCount = 12 };
        var layer = new Layer(); // Default is 0 length

        comp.AddLayer(layer);

        Assert.Equal(12, layer.Clips.Length);
    }

    [Fact]
    public void Clone_CreatesIndependentModelGraphForEngine()
    {
        var original = new Composition
        {
            Width = 1280,
            Height = 720,
            ColumnCount = 2
        };
        var layer = new Layer { Name = "Base", Opacity = 0.75 };
        original.AddLayer(layer);
        layer.Clips[0] = new Clip(new FileVideoSource("intro.mp4")) { Name = "Intro" };

        var clone = original.Clone();
        original.Layers[0].Opacity = 0.25;
        original.Layers[0].Clips[0]!.Name = "Changed in UI";

        Assert.Equal(1280, clone.Width);
        Assert.Equal(720, clone.Height);
        Assert.Equal(2, clone.Layers[0].Clips.Length);
        Assert.Equal(0.75, clone.Layers[0].Opacity);
        Assert.Equal("Intro", clone.Layers[0].Clips[0]!.Name);
        Assert.NotSame(original.Layers[0], clone.Layers[0]);
        Assert.NotSame(original.Layers[0].Clips[0], clone.Layers[0].Clips[0]);
        Assert.Equal(original.Layers[0].Id, clone.Layers[0].Id);
        Assert.Equal(original.Layers[0].Clips[0]!.Id, clone.Layers[0].Clips[0]!.Id);
    }
}
