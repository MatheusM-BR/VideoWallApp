using System.Text.Json;
using VideoWallRW.Core.Model;

namespace VideoWallRW.Core.Persistence;

/// <summary>
/// Presets de Advanced Output salvos separados do projeto (a instalação física não
/// muda por show) — %AppData%\VideoWallRW\outputs\{nome}.json.
/// </summary>
public static class OutputSetupSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PresetsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoWallRW", "outputs");

    public static void Save(string name, OutputSetup setup)
    {
        Directory.CreateDirectory(PresetsDirectory);
        File.WriteAllText(PathFor(name), JsonSerializer.Serialize(setup, Options));
    }

    public static OutputSetup? Load(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<OutputSetup>(File.ReadAllText(path), Options);
    }

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static IReadOnlyList<string> ListPresetNames()
    {
        if (!Directory.Exists(PresetsDirectory))
            return [];
        return Directory.GetFiles(PresetsDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string PathFor(string name)
    {
        string safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "preset";
        return Path.Combine(PresetsDirectory, safe + ".json");
    }
}
