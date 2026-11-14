namespace VideoWallRW.Media.Decoding;

/// <summary>
/// Buffer BGRA com dados de um frame decodificado. Reutilizado via pool — nunca alocar
/// um por frame. Retornar ao pool (<see cref="FramePool"/>) após consumir.
/// </summary>
public sealed class DecodedFrame
{
    /// <summary>Pixels em formato BGRA, row-major. Tamanho = Stride * Height.</summary>
    public byte[] Pixels { get; internal set; } = [];

    /// <summary>Bytes por linha (pode ser > Width*4 dependendo do sws_scale).</summary>
    public int Stride { get; internal set; }

    /// <summary>Largura em pixels.</summary>
    public int Width { get; internal set; }

    /// <summary>Altura em pixels.</summary>
    public int Height { get; internal set; }

    /// <summary>Posição de apresentação em segundos. -1 = desconhecida.</summary>
    public double PtsSeconds { get; internal set; }

    /// <summary>Counter de geração do seek — descartado se diferente do corrente.</summary>
    internal int Generation { get; set; }
}
