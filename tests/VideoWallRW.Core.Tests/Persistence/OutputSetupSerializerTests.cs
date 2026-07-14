using VideoWallRW.Core.Math;
using VideoWallRW.Core.Model;
using VideoWallRW.Core.Persistence;
using Xunit;

namespace VideoWallRW.Core.Tests.Persistence;

public class OutputSetupSerializerTests : IDisposable
{
    private readonly string _presetName = "test-preset-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void SaveLoad_RoundTrip_PreservaSlicesETelas()
    {
        var setup = new OutputSetup();
        var screen = new Screen
        {
            Name = "Tela 1",
            MonitorDeviceName = @"\\.\DISPLAY2",
            Enabled = true,
            Color = new ColorCorrection { Brightness = 0.1, Contrast = 1.2, Gamma = 0.9 },
        };
        screen.Slices.Add(new Slice
        {
            Name = "Esquerda",
            InputRect = new RectF(0, 0, 1920, 1080),
            OutputRect = new RectF(0, 0, 960, 1080),
            Rotation = Rotation.R90,
            FlipH = true,
        });
        setup.Screens.Add(screen);

        OutputSetupSerializer.Save(_presetName, setup);
        var loaded = OutputSetupSerializer.Load(_presetName);

        Assert.NotNull(loaded);
        Assert.Single(loaded.Screens);
        Assert.Equal("Tela 1", loaded.Screens[0].Name);
        Assert.Equal(@"\\.\DISPLAY2", loaded.Screens[0].MonitorDeviceName);
        Assert.Equal(0.1, loaded.Screens[0].Color.Brightness, 3);
        Assert.Single(loaded.Screens[0].Slices);
        var slice = loaded.Screens[0].Slices[0];
        Assert.Equal(Rotation.R90, slice.Rotation);
        Assert.True(slice.FlipH);
        Assert.Equal(1920, slice.InputRect.Width);
        Assert.Equal(960, slice.OutputRect.Width);
    }

    [Fact]
    public void Load_PresetInexistente_RetornaNull()
    {
        Assert.Null(OutputSetupSerializer.Load("preset-que-nao-existe-" + Guid.NewGuid()));
    }

    [Fact]
    public void ListPresetNames_IncluiPresetSalvo()
    {
        OutputSetupSerializer.Save(_presetName, new OutputSetup());
        Assert.Contains(_presetName, OutputSetupSerializer.ListPresetNames());
    }

    public void Dispose() => OutputSetupSerializer.Delete(_presetName);
}
