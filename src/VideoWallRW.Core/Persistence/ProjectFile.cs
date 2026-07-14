using VideoWallRW.Core.Model;

namespace VideoWallRW.Core.Persistence;

public sealed class ProjectMeta
{
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public string? OutputPresetName { get; set; }
}

/// <summary>
/// Snapshot de cena (Fase 7 T7.5): captura o estado inteiro da grade — clip ativo
/// por layer, opacidades — para disparar como preset com um atalho.
/// </summary>
public sealed class SceneSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Cena";
    public string? Hotkey { get; set; }

    /// <summary>layerId (Guid) → coluna ativa (-1 = nenhuma).</summary>
    public Dictionary<Guid, int> ActiveColumns { get; set; } = [];

    /// <summary>layerId (Guid) → opacidade.</summary>
    public Dictionary<Guid, double> LayerOpacities { get; set; } = [];

    public double MasterFader { get; set; } = 1.0;
}

/// <summary>
/// Conteúdo completo de um projeto .vwrw — composição, output, cenas e metadados.
/// <see cref="SchemaVersion"/> permite migração de projetos antigos (ver
/// <see cref="ProjectSerializer"/>); um SchemaVersion MAIOR que o suportado pelo
/// app atual é recusado com mensagem clara, nunca carregado parcialmente.
/// </summary>
public sealed class ProjectFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Composition Composition { get; set; } = new();
    public OutputSetup Output { get; set; } = new();
    public List<SceneSnapshot> Snapshots { get; set; } = [];
    public ProjectMeta Meta { get; set; } = new();
    public InstallationConfig Installation { get; set; } = new();
}
