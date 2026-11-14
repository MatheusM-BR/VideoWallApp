namespace VideoWallRW.Media.Ndi;

/// <summary>
/// Ponte entre a fonte NDI ao vivo (camada Media/Engine) e a UI (App), sem a UI
/// precisar de referência à instância da fonte. A cada ~30s a <see cref="NdiVideoSource"/>
/// no ar publica aqui uma cópia do frame atual (BGRA); o App assina, gera um PNG e usa
/// como miniatura da célula NDI. Reaproveita o receptor já aberto — NÃO abre conexões
/// NDI extras (que arriscariam instabilidade), então só há prévia enquanto a fonte
/// está no ar; a última prévia fica como miniatura depois.
/// </summary>
public static class NdiSnapshotHub
{
    /// <summary>(sourceName, bgra, width, height, stride). Disparado na thread de
    /// captura NDI — o assinante deve copiar o que precisar e sair rápido (encode/UI
    /// em outra thread).</summary>
    public static event Action<string, byte[], int, int, int>? SnapshotReady;

    public static void Publish(string sourceName, byte[] bgra, int width, int height, int stride)
        => SnapshotReady?.Invoke(sourceName, bgra, width, height, stride);
}
