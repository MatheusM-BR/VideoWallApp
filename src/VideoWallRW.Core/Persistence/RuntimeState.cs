using System.Text.Json;

namespace VideoWallRW.Core.Persistence;

/// <summary>
/// Estado de execução (não de projeto) — o que estava "no ar" quando o programa
/// fechou, para o modo instalação (Fase 8) retomar sozinho ao reiniciar via
/// <c>--kiosk</c>. Gravado atomicamente a cada poucos segundos, só quando muda.
/// </summary>
public sealed class RuntimeState
{
    public string? ProjectPath { get; set; }

    /// <summary>layerId (Guid) → coluna ativa.</summary>
    public Dictionary<Guid, int> ActiveColumns { get; set; } = [];

    public double MasterFader { get; set; } = 1.0;
    public bool SequencerRunning { get; set; }
    public int SequencerStep { get; set; }
}

public static class RuntimeStateService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoWallRW", "runtime-state.json");

    public static void Save(RuntimeState state)
    {
        string dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        File.Move(tmp, Path, overwrite: true);
    }

    public static RuntimeState? TryLoad()
    {
        if (!File.Exists(Path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(Path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
