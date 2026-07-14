using System.Text;
using VideoWallRW.Core.Osc;
using Xunit;

namespace VideoWallRW.Core.Tests.Osc;

public class OscMessageTests
{
    private static byte[] PadString(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s + "\0");
        int pad = (4 - bytes.Length % 4) % 4;
        return [.. bytes, .. new byte[pad]];
    }

    private static byte[] BuildPacket(string address, string typeTags, params byte[][] argBytes)
    {
        var parts = new List<byte>();
        parts.AddRange(PadString(address));
        parts.AddRange(PadString("," + typeTags));
        foreach (var arg in argBytes) parts.AddRange(arg);
        return [.. parts];
    }

    private static byte[] Int32Be(int v)
    {
        var b = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return b;
    }

    private static byte[] FloatBe(float v)
    {
        var b = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return b;
    }

    [Fact]
    public void Parse_SemArgumentos_RetornaEnderecoSoValores()
    {
        var packet = BuildPacket("/vwrw/column/3/trigger", "");
        var msg = OscMessage.Parse(packet);

        Assert.Equal("/vwrw/column/3/trigger", msg.Address);
        Assert.Empty(msg.Args);
    }

    [Fact]
    public void Parse_ArgumentoFloat_DecodificaCorretamente()
    {
        var packet = BuildPacket("/vwrw/master/fader", "f", FloatBe(0.75f));
        var msg = OscMessage.Parse(packet);

        Assert.Equal("/vwrw/master/fader", msg.Address);
        Assert.Single(msg.Args);
        Assert.Equal(0.75f, (float)msg.Args[0], 3);
    }

    [Fact]
    public void Parse_ArgumentoInt_DecodificaCorretamente()
    {
        var packet = BuildPacket("/vwrw/blackout", "i", Int32Be(1));
        var msg = OscMessage.Parse(packet);

        Assert.Equal(1, (int)msg.Args[0]);
    }

    [Fact]
    public void Parse_ArgumentoString_DecodificaCorretamente()
    {
        var packet = BuildPacket("/vwrw/scene/trigger", "s", PadString("Abertura"));
        var msg = OscMessage.Parse(packet);

        Assert.Equal("Abertura", (string)msg.Args[0]);
    }

    [Fact]
    public void Parse_MultiplosArgumentos_DecodificaNaOrdem()
    {
        var packet = BuildPacket("/vwrw/layer/1/opacity", "if", Int32Be(2), FloatBe(0.5f));
        var msg = OscMessage.Parse(packet);

        Assert.Equal(2, (int)msg.Args[0]);
        Assert.Equal(0.5f, (float)msg.Args[1], 3);
    }

    [Fact]
    public void Parse_EnderecoInvalido_Lanca()
    {
        var bad = PadString("nao-comeca-com-barra");
        Assert.Throws<FormatException>(() => OscMessage.Parse(bad));
    }
}
