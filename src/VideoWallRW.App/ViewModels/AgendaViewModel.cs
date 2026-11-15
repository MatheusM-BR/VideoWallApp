using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
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
    private readonly Action<string, string> _openImageEditor; // (path, suggestedClipName)
    private readonly Action<string> _openPdfImporter;
    private readonly Action<string>? _revealInLibrary;
    private int _eventsRequest;
    private int _detailsRequest;
    private bool _disposed;

    [ObservableProperty] private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private AgendaEventViewModel? _selectedEvent;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveBannerCommand), nameof(SaveBannerAndEditCommand), nameof(DownloadCatalogCommand), nameof(DownloadRegulationCommand))]
    private EventDetails? _details;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveBannerCommand), nameof(SaveBannerAndEditCommand), nameof(DownloadCatalogCommand), nameof(DownloadRegulationCommand))]
    private bool _loadingDetails;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveBannerCommand), nameof(SaveBannerAndEditCommand), nameof(DownloadCatalogCommand), nameof(DownloadRegulationCommand))]
    private bool _downloading;
    [ObservableProperty] private string? _bannerPreviewPath;

    public ObservableCollection<AgendaEventViewModel> Events { get; } = [];

    public AgendaViewModel(Action<string, string> openImageEditor, Action<string> openPdfImporter,
        Action<string>? revealInLibrary = null)
    {
        _openImageEditor = openImageEditor;
        _openPdfImporter = openPdfImporter;
        _revealInLibrary = revealInLibrary;
        _ = LoadAsync();
    }

    partial void OnDateChanged(DateOnly value) => _ = LoadAsync();

    partial void OnSelectedEventChanged(AgendaEventViewModel? value)
    {
        int request = ++_detailsRequest;
        Details = null;
        BannerPreviewPath = null;
        LoadingDetails = false;
        if (value is null) return;
        _ = LoadDetailsAsync(value.Model.Url, request);
    }

    [RelayCommand] private void PreviousDay() => Date = Date.AddDays(-1);
    [RelayCommand] private void NextDay() => Date = Date.AddDays(1);
    [RelayCommand] private void Today() => Date = DateOnly.FromDateTime(DateTime.Now);

    private async Task LoadAsync()
    {
        int request = ++_eventsRequest;
        Loading = true;
        StatusText = "Carregando agenda…";
        Events.Clear();
        SelectedEvent = null;
        try
        {
            var events = await _client.GetEventsAsync(Date, CancellationToken.None);
            if (_disposed || request != _eventsRequest) return;
            foreach (var e in events.OrderBy(e => e.Time))
                Events.Add(new AgendaEventViewModel(e));
            StatusText = events.Count == 0 ? "Nenhum leilão nesse dia." : "Selecione um leilão para conferir a imagem e os documentos.";
        }
        catch (SiteLayoutChangedException ex)
        {
            if (_disposed || request != _eventsRequest) return;
            StatusText = $"O site mudou de layout e o parser não reconheceu a página: {ex.Message}";
        }
        catch (Exception ex)
        {
            if (_disposed || request != _eventsRequest) return;
            StatusText = $"Falha ao carregar a agenda: {ex.Message}";
        }
        finally
        {
            if (!_disposed && request == _eventsRequest) Loading = false;
        }
    }

    private async Task LoadDetailsAsync(string eventUrl, int request)
    {
        LoadingDetails = true;
        StatusText = "Buscando imagem e documentos…";
        try
        {
            var details = await _client.GetEventAsync(eventUrl, CancellationToken.None);
            if (_disposed || request != _detailsRequest) return;
            Details = details;
            if (details.BannerUrl is not null)
                BannerPreviewPath = await DownloadBannerPreviewAsync(
                    details.BannerPreviewUrl ?? details.BannerUrl, request);
            if (_disposed || request != _detailsRequest) return;
            StatusText = details.BannerUrl is null
                ? "Evento sem banner. Confira os documentos disponíveis."
                : BannerPreviewPath is null
                    ? "Imagem encontrada, mas a prévia não pôde ser decodificada. O banner ainda pode ser salvo."
                    : "Imagem encontrada. Salve o banner ou abra no editor.";
        }
        catch (Exception ex)
        {
            if (_disposed || request != _detailsRequest) return;
            StatusText = $"Falha ao abrir o evento: {ex.Message}";
        }
        finally
        {
            if (!_disposed && request == _detailsRequest) LoadingDetails = false;
        }
    }

    private async Task<string?> DownloadBannerPreviewAsync(string url, int request)
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoWallRW", "cache", "agenda");
        Directory.CreateDirectory(cache);
        string id = Guid.NewGuid().ToString("N");
        string raw = Path.Combine(cache, $"{id}.download");
        string png = Path.Combine(cache, $"{id}.png");
        try
        {
            await _client.DownloadFileAsync(url, raw, null, CancellationToken.None);
            if (_disposed || request != _detailsRequest) return null;
            using var bitmap = SKBitmap.Decode(raw);
            if (bitmap is null) return null;
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            await File.WriteAllBytesAsync(png, encoded.ToArray());
            return png;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(raw); } catch { }
        }
    }

    private string EventFolder()
    {
        if (SelectedEvent is null) throw new InvalidOperationException();
        // Tudo vai pra pasta padrão da operação (C:\Video Wall) — um lugar só.
        return AppPaths.AgendaEventFolder(SelectedEvent.Model.Date, SelectedEvent.Model.Slug);
    }

    /// <summary>Grava o meta.json do download. O título/data vêm por PARÂMETRO, não de
    /// SelectedEvent/Details: isto roda depois do await do download, e se o operador
    /// tiver trocado de evento nesse meio-tempo o meta descreveria um leilão diferente
    /// do arquivo salvo ao lado dele.</summary>
    private void WriteMeta(string folder, string sourceUrl, string? eventTitle, DateOnly? eventDate)
    {
        var meta = new
        {
            sourceUrl,
            eventTitle,
            eventDate,
            downloadedAt = DateTimeOffset.Now,
        };
        File.WriteAllText(Path.Combine(folder, "meta.json"),
            System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private bool CanSaveBanner() => !Downloading && !LoadingDetails && SelectedEvent is not null && Details?.BannerUrl is not null;
    private bool CanDownloadCatalog() => CanDownload(DownloadKind.Catalog);
    private bool CanDownloadRegulation() => CanDownload(DownloadKind.Regulation);
    private bool CanDownload(DownloadKind kind) => !Downloading && !LoadingDetails && SelectedEvent is not null && Details?.Downloads.Any(d => d.Kind == kind) == true;

    [RelayCommand(CanExecute = nameof(CanSaveBanner))]
    private async Task SaveBanner() => await SaveBannerInternal(openEditor: false);

    [RelayCommand(CanExecute = nameof(CanSaveBanner))]
    private async Task SaveBannerAndEdit() => await SaveBannerInternal(openEditor: true);

    private async Task SaveBannerInternal(bool openEditor)
    {
        if (Details?.BannerUrl is not { } bannerUrl) return;
        bool lowRes = Details.BannerPreviewUrl is null;

        try
        {
            Downloading = true;
            DownloadProgress = 0;
            // Captura o evento uma vez: EventFolder() e o nome do arquivo abaixo têm
            // que vir do MESMO evento, mesmo que a seleção mude no meio do await.
            var ev = SelectedEvent ?? throw new InvalidOperationException();
            string folder = AppPaths.AgendaEventFolder(ev.Model.Date, ev.Model.Slug);
            // Nome do arquivo = nome do leilão, igual ao da pasta (data + slug). Nunca um
            // "banner.jpg" fixo: na lista de Recentes/Files (que só mostra o nome) todo
            // banner ficaria igual, e assim o arquivo se identifica sozinho fora da pasta.
            string dest = Path.Combine(folder, $"{Path.GetFileName(folder)}.jpg");
            string? titleAtStart = Details?.Title;
            await _client.DownloadFileAsync(bannerUrl, dest, new Progress<double>(p => DownloadProgress = p), CancellationToken.None);
            WriteMeta(folder, bannerUrl, titleAtStart, ev.Model.Date);
            StatusText = lowRes
                ? $"Banner salvo (baixa resolução — o evento não tinha alta): {dest}"
                : $"Banner salvo: {dest} — arraste até uma célula da grade";

            // titleAtStart (capturado antes do await), não Details.Title: a seleção pode
            // ter mudado durante o download, e o editor abriria com o nome de outro leilão.
            if (openEditor)
                _openImageEditor(dest, titleAtStart ?? ev.Model.Title);
            else
                _revealInLibrary?.Invoke(dest);
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

    [RelayCommand(CanExecute = nameof(CanDownloadCatalog))]
    private async Task DownloadCatalog() => await DownloadPdf(DownloadKind.Catalog);

    [RelayCommand(CanExecute = nameof(CanDownloadRegulation))]
    private async Task DownloadRegulation() => await DownloadPdf(DownloadKind.Regulation);

    private async Task DownloadPdf(DownloadKind kind)
    {
        var download = Details?.Downloads.FirstOrDefault(d => d.Kind == kind);
        if (download is null) return;

        try
        {
            Downloading = true;
            DownloadProgress = 0;
            // Mesmo cuidado do banner: fixa o evento/título ANTES do await, pra o
            // meta.json nunca descrever um leilão diferente do arquivo salvo.
            var ev = SelectedEvent ?? throw new InvalidOperationException();
            string? titleAtStart = Details?.Title;
            string folder = AppPaths.AgendaEventFolder(ev.Model.Date, ev.Model.Slug);
            string name = kind == DownloadKind.Catalog ? "catalogo.pdf" : "regulamento.pdf";
            string dest = Path.Combine(folder, name);
            await _client.DownloadFileAsync(download.Url, dest, new Progress<double>(p => DownloadProgress = p), CancellationToken.None);
            WriteMeta(folder, download.Url, titleAtStart, ev.Model.Date);
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

    public void Dispose()
    {
        _disposed = true;
        _client.Dispose();
    }
}
