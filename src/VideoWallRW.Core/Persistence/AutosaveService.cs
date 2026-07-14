using System.Security.Cryptography;
using System.Text;

namespace VideoWallRW.Core.Persistence;

/// <summary>
/// Autosave e recuperação pós-crash (Fase 7 T7.3). O autosave nunca escreve em
/// cima do projeto do usuário — vai para uma cópia própria em LocalAppData,
/// nomeada por hash do caminho do projeto (ou "untitled" para projeto novo ainda
/// não salvo). Um arquivo-sentinela marca sessão em andamento: se ele existir no
/// próximo start, o encerramento anterior não foi limpo (crash) e a UI pode
/// oferecer recuperação.
/// </summary>
public static class AutosaveService
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoWallRW");

    public static string AutosaveDirectory => Path.Combine(BaseDir, "autosave");
    private static string SentinelPath => Path.Combine(BaseDir, "session.lock");

    public static string GetAutosavePath(string? projectPath)
    {
        string key = projectPath ?? "untitled";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(AutosaveDirectory, hash + ".vwrw");
    }

    public static void Save(string? projectPath, ProjectFile project)
    {
        Directory.CreateDirectory(AutosaveDirectory);
        ProjectSerializer.Save(GetAutosavePath(projectPath), project);
    }

    public static ProjectFile? TryLoadAutosave(string? projectPath)
    {
        string path = GetAutosavePath(projectPath);
        return File.Exists(path) ? ProjectSerializer.Load(path) : null;
    }

    public static void MarkSessionStart()
    {
        Directory.CreateDirectory(BaseDir);
        File.WriteAllText(SentinelPath, DateTimeOffset.Now.ToString("O"));
    }

    public static void MarkSessionClean()
    {
        if (File.Exists(SentinelPath))
            File.Delete(SentinelPath);
    }

    /// <summary>True se a sessão anterior não foi encerrada normalmente (crash/kill).</summary>
    public static bool WasUncleanShutdown() => File.Exists(SentinelPath);
}
