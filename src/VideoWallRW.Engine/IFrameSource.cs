using Vortice.Direct3D11;

namespace VideoWallRW.Engine;

/// <summary>
/// Estado de uma fonte de pixels: Arming = inicializando; Ready = pronto mas parado;
/// Playing = produzindo frames; Error = falhou.
/// </summary>
public enum SourceState { Arming, Ready, Playing, Error }

/// <summary>
/// Fonte genérica de pixels (arquivo, NDI, captura...).
/// Implementado por VideoFileSource, SolidColorSource, etc.
/// O método <see cref="AcquireFrame"/> é chamado exclusivamente pela render thread.
/// </summary>
public interface IFrameSource : IDisposable
{
    SourceState State { get; }
    string? ErrorMessage { get; }

    /// <summary>
    /// Retorna a SRV com o frame atual (pode repetir a anterior se não chegou novo).
    /// Null = ainda sem frame (não desenhar).
    /// Chamado exclusivamente pela render thread.
    /// </summary>
    ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds);

    int Width { get; }
    int Height { get; }

    /// <summary>FPS de decodificação (0 para fontes sem decode, ex: imagens estáticas).</summary>
    double DecodeFps => 0;

    void Start();
    void Stop();
}

/// <summary>Capacidade opcional de áudio de uma fonte, controlada pelo ClipPlayer.</summary>
public interface IAudioControllable
{
    void SetAudioState(bool playing, double volume, bool muted, double speed, double fade);
}
