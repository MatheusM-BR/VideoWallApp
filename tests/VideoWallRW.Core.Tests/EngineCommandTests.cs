using VideoWallRW.Core.Commands;
using Xunit;

namespace VideoWallRW.Core.Tests;

public class EngineCommandTests
{
    [Fact]
    public void Comandos_com_mesmo_monitor_sao_iguais()
    {
        Assert.Equal(new EnableOutput(@"\\.\DISPLAY1"), new EnableOutput(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void Comandos_de_tipos_diferentes_nao_sao_iguais()
    {
        Assert.NotEqual<EngineCommand>(
            new EnableOutput(@"\\.\DISPLAY1"),
            new DisableOutput(@"\\.\DISPLAY1"));
    }
}
