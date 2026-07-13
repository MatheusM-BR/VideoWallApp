namespace VideoWallRW.Media.Decoding;

/// <summary>
/// Lançado quando o <see cref="MediaDecoder"/> não consegue abrir ou decodificar um arquivo.
/// </summary>
public sealed class MediaOpenException : Exception
{
    public int FfmpegErrorCode { get; }

    public MediaOpenException(string message, int errorCode = 0)
        : base(message)
    {
        FfmpegErrorCode = errorCode;
    }

    public MediaOpenException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
