using VideoWallRW.Media.Decoding;

namespace VideoWallRW.Media;

/// <summary>
/// Sondagem leve de metadados de mídia (sem decodificar frames): usada no import de
/// clips para preencher a duração total, que alimenta a barra de trim e os defaults
/// de In/Out na UI.
/// </summary>
public static class MediaProbe
{
    public static double? TryGetDuration(string path)
    {
        try
        {
            using var decoder = MediaDecoder.Open(path);
            return decoder.Duration > 0 ? decoder.Duration : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dimensões nativas (px) do vídeo — mesmo Open leve usado por TryGetDuration.</summary>
    public static (int Width, int Height)? TryGetVideoDimensions(string path)
    {
        try
        {
            using var decoder = MediaDecoder.Open(path);
            return decoder.Width > 0 && decoder.Height > 0 ? (decoder.Width, decoder.Height) : null;
        }
        catch
        {
            return null;
        }
    }
}
