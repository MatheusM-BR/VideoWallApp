using System.IO;
using System.Text.Json;
using Serilog;

namespace VideoWallRW.App;

/// <summary>
/// Preferências do programa que valem pra máquina inteira (não pro projeto), em
/// %AppData%\VideoWallRW\settings.json. Arquivo ausente/corrompido = padrões.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Decodificar vídeo pela GPU (D3D11VA). Experimental — desligado por padrão.</summary>
    public bool HardwareDecode { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoWallRW", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "settings.json ilegível — usando os padrões");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Não foi possível salvar settings.json");
        }
    }
}
