using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.Agenda;

namespace VideoWallRW.App.ViewModels;

public sealed partial class AgendaEventViewModel(AgendaEvent model) : ObservableObject
{
    public AgendaEvent Model { get; } = model;
    public string Title => Model.Title;
    public string Subtitle => $"{Model.Time:HH\\:mm}  {Model.Category}  —  {Model.City}".Trim();
}

/// <summary>
/// VM da janela "Agenda de Leilões" (Fase 10 T10.2/T10.3): navegação por data, lista
/// do dia, detalhe do evento e download de banner/PDFs pra pasta de assets do projeto.
/// </summary>
public sealed partial class AgendaViewModel : ObservableObject, IDisposable
{
    private readonly AgendaClient _client = new();
    private readonly Func<string> _projectFolder;
    private readonly Action<string, string> _openImageEditor; // (path, suggestedClipName)
    private readonly Action<string> _openPdfImporter;
    private readonly Action<string, string> _sendToCell; // (path, clipName)

    [ObservableProperty] private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private AgendaEventViewModel? _selectedEvent;
    [ObservableProperty] private EventDetails? _details;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _downloading;

    public ObservableCollection<AgendaEventViewModel> Events { get; } = [];

    public AgendaViewModel(Func<string> projectFolder, Action<string, string> openImageEditor,
        Action<string> openPdfImporter, Action<string, string> sendToCell)
    {
        _projectFolder = projectFolder;
        _openImageEditor = openImageEditor;
        _openPdfImporter = openPdfImporter;
        _sendToCell = sendToCell;
        _ = LoadAsync();
    }

    partial void OnDateChanged(DateOnly value) => _ = LoadAsync();

    partial void OnSelectedEventChanged(AgendaEventViewModel? value)
    {
        Details = null;
        if (value is null) return;
        _ = LoadDetailsAsync(value.Model.Url);
    }

    [RelayCommand] private void PreviousDay() => Date = Date.AddDays(-1);
    [RelayCommand] private void NextDay() => Date = Date.AddDays(1);
    [RelayCommand] private void Today() => Date = DateOnly.FromDateTime(DateTime.Now);

    private async Task LoadAsync()
    {
        Loading = true;
        StatusText = "";
        Events.Clear();
        SelectedEvent = null;
        try
        {
            var events = await _client.GetEventsAsync(Date, CancellationToken.None);
            foreach (var e in events.OrderBy(e => e.Time))
                Events.Add(new AgendaEventViewModel(e));
            if (events.Count == 0)
                StatusText = "Nenhum leilão nesse dia.";
        }
        catch (SiteLayoutChangedException ex)
        {
            StatusText = $"O site mudou de layout e o parser não reconheceu a página: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"Falha ao carregar a agenda: {ex.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    private async Task LoadDetailsAsync(string eventUrl)
    {
        try
        {
            Details = await _client.GetEventAsync(eventUrl, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"Falha ao abrir o evento: {ex.Message}";
        }
    }

    private string EventFolder()
    {
        if (SelectedEvent is null) throw new InvalidOperationException();
        string root = Path.Combine(_projectFolder(), "Assets", "Agenda",
            $"{SelectedEvent.Model.Date:yyyy-MM-dd}-{SelectedEvent.Model.Slug}");
        Directory.CreateDirectory(root);
        return root;
    }

    private void WriteMeta(string folder, string sourceUrl)
    {
        var meta = new
        {
            sourceUrl,
            eventTitle = Details?.Title,
            eventDate = SelectedEvent?.Model.Date,
            downloadedAt = DateTimeOffset.Now,
        };
        File.WriteAllText(Path.Combine(folder, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private enum AfterSave { Nothing, Edit, SendToCell }

    [RelayCommand]
    private async Task SaveBanner() => await SaveBannerInternal(AfterSave.Nothing);

    [RelayCommand]
    private async Task SaveBannerAndEdit() => await SaveBannerInternal(AfterSave.Edit);

    [RelayCommand]
    private async Task SaveBannerAndSend() => await SaveBannerInternal(AfterSave.SendToCell);

    private async Task SaveBannerInternal(AfterSave then)
    {
        if (Details?.BannerUrl is not { } bannerUrl) return;
        bool lowRes = Details.BannerPreviewUrl is null;

        try
        {
            Downloading = true;
            DownloadProgress = 0;
            string folder = EventFolder();
            string dest = Path.Combine(folder, "banner.jpg");
            await _client.DownloadFileAsync(bannerUrl, dest, new Progress<double>(p => DownloadProgress = p), CancellationToken.None);
            WriteMeta(folder, bannerUrl);
            StatusText = lowRes
                ? $"Banner salvo (baixa resolução — o evento não tinha alta): {dest}"
                : $"Banner salvo: {dest}";

            switch (then)
            {
                case AfterSave.Edit:
                    _openImageEditor(dest, Details.Title);
                    break;
                case AfterSave.SendToCell:
                    _sendToCell(dest, Details.Title);
                    StatusText = $"Banner salvo e enviado para a grade: {dest}";
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha ao baixar o banner:\n{ex.Message}", "Agenda", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Downloading = false;
        }
    }

    [RelayCommand]
    private async Task DownloadCatalog() => await DownloadPdf(DownloadKind.Catalog);

    [RelayCommand]
    private async Task DownloadRegulation() => await DownloadPdf(DownloadKind.Regulation);

    private async Task DownloadPdf(DownloadKind kind)
    {
        var download = Details?.Downloads.FirstOrDefault(d => d.Kind == kind);
        if (download is null) return;

        try
        {
            Downloading = true;
            DownloadProgress = 0;
            string folder = EventFolder();
            string name = kind == DownloadKind.Catalog ? "catalogo.pdf" : "regulamento.pdf";
            string dest = Path.Combine(folder, name);
            await _client.DownloadFileAsync(download.Url, dest, new Progress<double>(p => DownloadProgress = p), CancellationToken.None);
            WriteMeta(folder, download.Url);
            StatusText = $"{download.Label}: salvo.";
            _openPdfImporter(dest);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha ao baixar o PDF:\n{ex.Message}", "Agenda", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Downloading = false;
        }
    }

    public void Dispose() => _client.Dispose();
}
