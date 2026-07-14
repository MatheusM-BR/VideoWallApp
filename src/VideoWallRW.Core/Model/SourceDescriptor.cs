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
public abstract record SourceDescriptor;

public sealed record FileVideoSource(string Path) : SourceDescriptor;
public sealed record FileImageSource(string Path) : SourceDescriptor;
public sealed record SolidColorSource(uint Rgba) : SourceDescriptor;
