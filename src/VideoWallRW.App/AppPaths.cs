using System.IO;

namespace VideoWallRW.App;

/// <summary>
/// Pasta padrão de mídia da operação: o painel Files abre nela e toda imagem gerada
/// pelo programa (banner da Agenda, página de PDF, edição do editor) é salva nela —
/// um lugar só para a equipe achar tudo.
/// </summary>
public static class AppPaths
{
    public const string MediaRoot = @"C:\Video Wall";

    public static string EnsureMediaRoot()
    {
        Directory.CreateDirectory(MediaRoot);
        return MediaRoot;
    }

    /// <summary>Subpasta de um evento da Agenda dentro da pasta padrão.</summary>
    public static string AgendaEventFolder(DateOnly date, string slug)
    {
        string folder = Path.Combine(EnsureMediaRoot(), "Agenda", $"{date:yyyy-MM-dd}-{slug}");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
