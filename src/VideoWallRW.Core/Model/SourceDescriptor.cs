namespace VideoWallRW.Core.Model;

/// <summary>
/// Descreve uma fonte de mídia (vídeo, áudio, cor, placa de captura).
/// Usando records polimórficos para ser completamente mutável, fácil de usar pattern matching e de serializar (Fase 7).
/// </summary>
public abstract record SourceDescriptor;

public sealed record FileVideoSource(string Path) : SourceDescriptor;
public sealed record FileImageSource(string Path) : SourceDescriptor;
public sealed record SolidColorSource(uint Rgba) : SourceDescriptor;
