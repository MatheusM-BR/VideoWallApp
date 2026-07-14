using VideoWallRW.Core.Model;
using VideoWallRW.Core.Persistence;
using Xunit;

namespace VideoWallRW.Core.Tests.Persistence;

public class ProjectSerializerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vwrw-test-{Guid.NewGuid():N}.vwrw");

    [Fact]
    public void SaveLoad_RoundTrip_PreservaComposicaoESources()
    {
        var project = new ProjectFile();
        var layer = new Layer { Name = "Layer 1" };
        project.Composition.AddLayer(layer);
        project.Composition.Layers[0].Clips[0] = new Clip(new FileVideoSource(@"C:\media\video.mp4")) { Name = "Video" };
        project.Composition.Layers[0].Clips[1] = new Clip(new FileImageSource(@"C:\media\img.png")) { Name = "Imagem" };
        project.Composition.Layers[0].Clips[2] = new Clip(new SolidColorSource(0xFF0000FF)) { Name = "Vermelho" };

        ProjectSerializer.Save(_path, project);
        var loaded = ProjectSerializer.Load(_path);

        Assert.Equal(ProjectFile.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Single(loaded.Composition.Layers);
        var clips = loaded.Composition.Layers[0].Clips;
        Assert.IsType<FileVideoSource>(clips[0]!.Source);
        Assert.Equal(@"C:\media\video.mp4", ((FileVideoSource)clips[0]!.Source).Path);
        Assert.IsType<FileImageSource>(clips[1]!.Source);
        Assert.IsType<SolidColorSource>(clips[2]!.Source);
        Assert.Equal(0xFF0000FFu, ((SolidColorSource)clips[2]!.Source).Rgba);
    }

    [Fact]
    public void SaveLoad_RoundTrip_PreservaOutputSetupESnapshots()
    {
        var project = new ProjectFile();
        var screen = new Screen { Name = "Tela 1", MonitorDeviceName = @"\\.\DISPLAY1" };
        screen.Slices.Add(new Slice { Name = "Full", Rotation = Rotation.R90 });
        project.Output.Screens.Add(screen);
        project.Snapshots.Add(new SceneSnapshot { Name = "Abertura", Hotkey = "Ctrl+1", MasterFader = 0.8 });

        ProjectSerializer.Save(_path, project);
        var loaded = ProjectSerializer.Load(_path);

        Assert.Single(loaded.Output.Screens);
        Assert.Equal(Rotation.R90, loaded.Output.Screens[0].Slices[0].Rotation);
        Assert.Single(loaded.Snapshots);
        Assert.Equal("Abertura", loaded.Snapshots[0].Name);
        Assert.Equal(0.8, loaded.Snapshots[0].MasterFader);
    }

    [Fact]
    public void Load_CampoDesconhecido_NaoQuebra()
    {
        ProjectSerializer.Save(_path, new ProjectFile());
        string json = File.ReadAllText(_path);
        int firstBrace = json.IndexOf('{');
        json = json[..(firstBrace + 1)] + "\"campoQueNaoExisteAinda\": 42," + json[(firstBrace + 1)..];
        File.WriteAllText(_path, json);

        var loaded = ProjectSerializer.Load(_path);
        Assert.NotNull(loaded);
    }

    [Fact]
    public void Load_SchemaFuturo_Rejeita()
    {
        ProjectSerializer.Save(_path, new ProjectFile());
        string json = File.ReadAllText(_path).Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 999");
        File.WriteAllText(_path, json);

        Assert.Throws<ProjectSchemaTooNewException>(() => ProjectSerializer.Load(_path));
    }

    [Fact]
    public void Save_EscritaAtomica_NaoDeixaArquivoTmpParaTras()
    {
        ProjectSerializer.Save(_path, new ProjectFile());
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".bak");
        File.Delete(_path + ".tmp");
    }
}
