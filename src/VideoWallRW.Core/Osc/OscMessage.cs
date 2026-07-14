using System.Buffers.Binary;
using System.Text;

namespace VideoWallRW.Core.Osc;

/// <summary>
/// Parser mínimo de OSC 1.0 (Fase 9 T9.4) — só mensagens simples (sem bundles),
/// suficiente para receber comandos de TouchOSC/Companion/oscsend. Tipos suportados:
/// <c>i</c> (int32), <c>f</c> (float32), <c>s</c> (string), <c>b</c> (blob).
/// </summary>
public sealed record OscMessage(string Address, IReadOnlyList<object> Args)
{
    public static OscMessage Parse(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        string address = ReadOscString(data, ref pos);
        if (address.Length == 0 || address[0] != '/')
            throw new FormatException("Pacote OSC sem endereço válido.");

        var args = new List<object>();
        if (pos < data.Length && data[pos] == (byte)',')
        {
            string typeTags = ReadOscString(data, ref pos);
            foreach (char tag in typeTags.AsSpan(1)) // pula a ','
            {
                args.Add(tag switch
                {
                    'i' => ReadInt32(data, ref pos),
                    'f' => ReadFloat32(data, ref pos),
                    's' => ReadOscString(data, ref pos),
                    'b' => ReadBlob(data, ref pos),
                    _ => throw new FormatException($"Tipo OSC não suportado: '{tag}'"),
                });
            }
        }

        return new OscMessage(address, args);
    }

    private static string ReadOscString(ReadOnlySpan<byte> data, ref int pos)
    {
        int start = pos;
        int end = data[start..].IndexOf((byte)0);
        if (end < 0) throw new FormatException("String OSC sem terminador.");
        string s = Encoding.ASCII.GetString(data[start..(start + end)]);
        int total = end + 1;
        total += (4 - total % 4) % 4; // padding pro próximo múltiplo de 4
        pos = start + total;
        return s;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int pos)
    {
        int v = BinaryPrimitives.ReadInt32BigEndian(data[pos..(pos + 4)]);
        pos += 4;
        return v;
    }

    private static float ReadFloat32(ReadOnlySpan<byte> data, ref int pos)
    {
        float v = BinaryPrimitives.ReadSingleBigEndian(data[pos..(pos + 4)]);
        pos += 4;
        return v;
    }

    private static byte[] ReadBlob(ReadOnlySpan<byte> data, ref int pos)
    {
        int len = ReadInt32(data, ref pos);
        byte[] blob = data[pos..(pos + len)].ToArray();
        int total = len + (4 - len % 4) % 4;
        pos += total;
        return blob;
    }
}
