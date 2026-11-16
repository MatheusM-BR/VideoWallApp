using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace VideoWallRW.App.ViewModels;

public sealed partial class FileBrowserItem : ObservableObject
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsFolder { get; init; }
    public string Icon => IsFolder ? "📁" : "🎞";

    [ObservableProperty] private string? _thumbnailPath;
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

    /// <summary>Item clicado na lista — alimenta o painel PREVIEW abaixo das pastas.</summary>
    [ObservableProperty] private FileBrowserItem? _selectedItem;

    public ObservableCollection<FileBrowserItem> Items { get; } = [];

    public FileBrowserViewModel(Action<string> sendToCell)
    {
        _sendToCell = sendToCell;
        // Padrão da operação: abre sempre em C:\Video Wall (criada se não existir).
        Navigate(AppPaths.EnsureMediaRoot());
    }

    /// <summary>O que o painel de miniatura mostra: o item clicado em Arquivos OU o
    /// arquivo clicado em Fontes → Recentes (o último clicado em qualquer um dos dois).</summary>
    [ObservableProperty] private FileBrowserItem? _previewItem;

    partial void OnSelectedItemChanged(FileBrowserItem? value)
    {
        if (value is null || value.IsFolder) return;
        PreviewItem = value;
        RequestThumbnail(value);
    }

    /// <summary>Miniatura de um arquivo fora da pasta aberta (lista Recentes).</summary>
    public void Preview(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var item = Items.FirstOrDefault(i => !i.IsFolder &&
                       string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))
                   ?? new FileBrowserItem { Name = Path.GetFileName(path), FullPath = path, IsFolder = false };
        PreviewItem = item;
        RequestThumbnail(item);
    }

    private void RequestThumbnail(FileBrowserItem item)
    {
        if (item.ThumbnailPath is not null) return;
        ThumbnailProvider.Request(item.FullPath, thumb =>
        {
            if (PreviewItem == item) // pode ter trocado a seleção enquanto gerava
                item.ThumbnailPath = thumb;
        });
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

    /// <summary>
    /// Abre a pasta do arquivo e já deixa ele selecionado (com prévia) — usado depois de
    /// salvar no editor/Agenda, pro operador achar na hora o que acabou de gerar.
    /// </summary>
    public void ShowFile(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return;
        Navigate(folder);
        SelectedItem = Items.FirstOrDefault(i => !i.IsFolder &&
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private void Navigate(string folder)
    {
        if (!Directory.Exists(folder))
            return;

        CurrentFolder = folder;
        SelectedItem = null;
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
