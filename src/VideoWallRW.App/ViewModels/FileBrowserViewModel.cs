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

    private readonly Action<string> _sendToCell;

    [ObservableProperty] private string _currentFolder = "";

    public ObservableCollection<FileBrowserItem> Items { get; } = [];

    public FileBrowserViewModel(Action<string> sendToCell)
    {
        _sendToCell = sendToCell;
        // Padrão da operação: abre sempre em C:\Video Wall (criada se não existir).
        Navigate(AppPaths.EnsureMediaRoot());
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
    }
}
