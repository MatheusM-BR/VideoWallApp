namespace VideoWallRW.Engine;

/// <summary>
/// Fonte de pixels que tem transporte (arquivo de vídeo/imagem).
/// </summary>
public interface IPlayableSource : IFrameSource
{
    double Duration { get; }
    bool SupportsSeek { get; }
    void Seek(double seconds);

    /// <summary>Disparado no fim natural da reprodução (não-loop); usado pelo autopilot.</summary>
    event Action? EndReached;
}
