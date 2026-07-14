using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoWallRW.Core.Persistence;

public sealed class ProjectSchemaTooNewException(int found, int supported)
    : Exception($"Este projeto foi salvo por uma versão mais nova do VideoWallRW " +
        $"(schema {found}, suportado até {ProjectFile.CurrentSchemaVersion}). Atualize o programa.")
{
    public int FoundVersion { get; } = found;
    public int SupportedVersion { get; } = supported;
}

/// <summary>
/// Serialização do projeto (.vwrw = JSON). Gravação sempre atômica (escreve em
/// .tmp e substitui — nunca deixa um arquivo truncado se o processo morrer no meio).
/// </summary>
public static class ProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string path, ProjectFile project)
    {
        project.SchemaVersion = ProjectFile.CurrentSchemaVersion;
        project.Meta.SavedAt = DateTimeOffset.Now;

        string json = JsonSerializer.Serialize(project, Options);
        string tmpPath = path + ".tmp";
        string bakPath = path + ".bak";

        File.WriteAllText(tmpPath, json);
        if (File.Exists(path))
            File.Replace(tmpPath, path, bakPath);
        else
            File.Move(tmpPath, path);
    }

    /// <summary>
    /// Carrega e aplica migrações se necessário. Lança <see cref="ProjectSchemaTooNewException"/>
    /// se o arquivo for de uma versão futura — nunca carrega parcialmente.
    /// </summary>
    public static ProjectFile Load(string path)
    {
        string json = File.ReadAllText(path);
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException($"'{path}' não é um projeto VideoWallRW válido.");

        // "SchemaVersion" (PascalCase) — sem PropertyNamingPolicy configurado, o
        // serializer usa o nome da propriedade C# exatamente como está.
        int foundVersion = node["SchemaVersion"]?.GetValue<int>() ?? 1;
        if (foundVersion > ProjectFile.CurrentSchemaVersion)
            throw new ProjectSchemaTooNewException(foundVersion, ProjectFile.CurrentSchemaVersion);

        ProjectMigrations.Apply(node, foundVersion);

        var project = node.Deserialize<ProjectFile>(Options)
            ?? throw new InvalidDataException($"Falha ao desserializar '{path}'.");
        return project;
    }
}

/// <summary>
/// Pipeline de migração entre versões de schema — vazio por ora (só existe a
/// versão 1), mas já preparado: quando SchemaVersion 2 existir, adiciona um
/// case aqui transformando o JsonObject antes da desserialização.
/// </summary>
internal static class ProjectMigrations
{
    public static void Apply(JsonObject node, int fromVersion)
    {
        // Nenhuma migração necessária ainda — schema 1 é o único que existe.
    }
}
