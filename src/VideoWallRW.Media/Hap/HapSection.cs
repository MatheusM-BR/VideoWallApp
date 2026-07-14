using Snappier;

namespace VideoWallRW.Media.Hap;

public enum HapTextureFormat : byte
{
    Bc1 = 0x0B,       // RGB DXT1 — Hap1
    Bc3 = 0x0E,       // RGBA DXT5 — Hap5
    Bc3YCoCg = 0x0F,  // YCoCg DXT5 — HapQ
}

internal enum HapCompressor : byte
{
    None = 0xA0,
    Snappy = 0xB0,
    Complex = 0xC0,
}

/// <summary>
/// Parser do formato de seção do HAP (spec Vidvox), validado empiricamente contra
/// arquivos gerados pelo encoder `hap` do FFmpeg (ver docs/fases/FASE-6.md T6.4):
/// cabeçalho de 4 bytes (3 de tamanho LE24 + 1 de tipo), ou 8 bytes quando o tamanho
/// LE24 é zero (tamanho real em LE32 nos 4 bytes seguintes). O byte de tipo combina
/// compressor (nibble alto) e formato de textura (nibble baixo).
///
/// Só os compressores None e Snappy são suportados (o caso comum — <c>chunks=1</c>,
/// o default do encoder). O compressor "Complex" (multi-chunk, usado para encode
/// paralelo) tem uma estrutura aninhada adicional fora do escopo desta versão —
/// detectado e rejeitado com erro claro, nunca lido às cegas.
/// </summary>
public static class HapSection
{
    public static (HapTextureFormat Format, byte[] DxtBytes) Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4)
            throw new HapDecodeException("Pacote HAP menor que o cabeçalho mínimo (4 bytes).");

        int len24 = packet[0] | (packet[1] << 8) | (packet[2] << 16);
        byte type = packet[3];
        int headerSize, sectionLen;
        if (len24 == 0)
        {
            if (packet.Length < 8)
                throw new HapDecodeException("Pacote HAP com tamanho estendido truncado.");
            sectionLen = packet[4] | (packet[5] << 8) | (packet[6] << 16) | (packet[7] << 24);
            headerSize = 8;
        }
        else
        {
            sectionLen = len24;
            headerSize = 4;
        }

        if (headerSize + sectionLen > packet.Length)
            throw new HapDecodeException(
                $"Seção HAP declara {sectionLen} bytes além do cabeçalho, mas o pacote só tem {packet.Length}.");

        var compressor = (HapCompressor)(type & 0xF0);
        var format = (HapTextureFormat)(type & 0x0F);
        var section = packet.Slice(headerSize, sectionLen);

        byte[] dxtBytes = compressor switch
        {
            HapCompressor.None => section.ToArray(),
            HapCompressor.Snappy => Snappy.DecompressToArray(section),
            HapCompressor.Complex => throw new HapDecodeException(
                "HAP multi-chunk (\"Complex\", encode paralelo) não é suportado nesta versão. " +
                "Reexporte com chunks=1 (o default da maioria dos encoders, incluindo o do FFmpeg)."),
            _ => throw new HapDecodeException($"Compressor HAP desconhecido: 0x{(byte)compressor:X2}."),
        };

        if (format is not (HapTextureFormat.Bc1 or HapTextureFormat.Bc3 or HapTextureFormat.Bc3YCoCg))
            throw new HapDecodeException($"Formato de textura HAP não suportado: 0x{(byte)format:X2}.");

        return (format, dxtBytes);
    }
}

public sealed class HapDecodeException(string message) : Exception(message);
