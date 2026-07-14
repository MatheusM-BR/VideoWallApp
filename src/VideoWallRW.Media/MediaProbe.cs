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
}
