using System.Text.Json.Serialization;

namespace VideoWallRW.Core.Model;

/// <summary>
/// Descreve uma fonte de mídia (vídeo, áudio, cor, placa de captura).
/// Usando records polimórficos para ser completamente mutável, fácil de usar pattern matching e de serializar (Fase 7).
/// O discriminador "$type" é gravado no JSON do projeto (T7.1) — não renomear os
/// nomes de discriminador abaixo sem migração, projetos antigos dependem deles.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FileVideoSource), "fileVideo")]
[JsonDerivedType(typeof(FileImageSource), "fileImage")]
[JsonDerivedType(typeof(SolidColorSource), "solidColor")]
[JsonDerivedType(typeof(NdiSource), "ndi")]
public abstract record SourceDescriptor;

public sealed record FileVideoSource(string Path) : SourceDescriptor;
public sealed record FileImageSource(string Path) : SourceDescriptor;
public sealed record SolidColorSource(uint Rgba) : SourceDescriptor;

/// <summary>Fonte de rede NDI — identificada pelo nome que a rede anuncia
/// (ex.: "DESKTOP-ABC (Câmera 1)"), não por IP: a reconexão em SetClipFactory
/// resolve o nome de novo via NDI find a cada abertura do clip.</summary>
public sealed record NdiSource(string SourceName) : SourceDescriptor;
