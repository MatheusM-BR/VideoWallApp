using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace VideoWallRW.App.ViewModels;

public sealed class FileBrowserItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsFolder { get; init; }
    public string Icon => IsFolder ? "📁" : "🎞";
}

/// <summary>
/// Painel "Files" estilo Resolume (dock direito): navega pastas do disco, arrasta
/// arquivos pra grade ou dá duplo-clique pra mandar pra célula selecionada. A última
/// pasta é lembrada entre sessões em %AppData%\VideoWallRW\browser.txt.
/// </summary>
public sealed partial class FileBrowserViewModel : ObservableObject
{
    private static readonly string[] MediaExtensions =
        [".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".png", ".jpg", ".jpeg", ".bmp"];

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoWallRW", "browser.txt");

    private readonly Action<string> _sendToCell;

    [ObservableProperty] private string _currentFolder = "";

    public ObservableCollection<FileBrowserItem> Items { get; } = [];

    public FileBrowserViewModel(Action<string> sendToCell)
    {
        _sendToCell = sendToCell;
        string start = TryLoadLastFolder()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!Directory.Exists(start))
            start = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Navigate(start);
    }

    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "Pasta de mídia" };
        if (dlg.ShowDialog() == true)
            Navigate(dlg.FolderName);
    }

    [RelayCommand]
    private void NavigateUp()
    {
        var parent = Directory.GetParent(CurrentFolder);
        if (parent is not null)
            Navigate(parent.FullName);
    }

    [RelayCommand]
    private void Refresh() => Navigate(CurrentFolder);

    /// <summary>Duplo-clique: pasta navega, arquivo vai pra célula selecionada da grade.</summary>
    public void Open(FileBrowserItem item)
    {
        if (item.IsFolder)
            Navigate(item.FullPath);
        else
            _sendToCell(item.FullPath);
    }

    private void Navigate(string folder)
    {
        if (!Directory.Exists(folder))
            return;

        CurrentFolder = folder;
        Items.Clear();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                Items.Add(new FileBrowserItem { Name = Path.GetFileName(dir), FullPath = dir, IsFolder = true });

            foreach (var file in Directory.EnumerateFiles(folder)
                         .Where(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                Items.Add(new FileBrowserItem { Name = Path.GetFileName(file), FullPath = file, IsFolder = false });
        }
        catch (UnauthorizedAccessException) { /* pasta protegida — mostra o que deu */ }

        SaveLastFolder(folder);
    }

    private static string? TryLoadLastFolder()
    {
        try { return File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : null; }
        catch { return null; }
    }

    private static void SaveLastFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, folder);
        }
        catch { /* melhor esforço */ }
    }
}
