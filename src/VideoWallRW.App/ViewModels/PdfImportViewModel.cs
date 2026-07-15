using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFtoImage;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace VideoWallRW.App.ViewModels;

/// <summary>Miniatura de uma página do PDF na lateral.</summary>
public sealed partial class PdfPageThumbViewModel : ObservableObject
{
    public int PageNumber { get; init; }
    [ObservableProperty] private ImageSource? _image;
    public string Label => $"Página {PageNumber}";
}

/// <summary>
/// Fase 10 T10.4 (revisado): as páginas do PDF aparecem como miniaturas na lateral,
/// renderizadas progressivamente em background; clicar numa página abre a prévia
/// grande para exportar em alta. Catálogos têm dezenas de páginas — a miniatura é
/// renderizada em DPI baixo, uma a uma, sem travar a UI.
/// </summary>
public sealed partial class PdfImportViewModel : ObservableObject, IDisposable
{
    private readonly string _pdfPath;
    private readonly Action<string, string> _onImageReady;
    private readonly CancellationTokenSource _cts = new();
    private byte[]? _bytes;

    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private PdfPageThumbViewModel? _selectedPage;
    [ObservableProperty] private SKBitmap? _preview;
    [ObservableProperty] private int _dpi = 1000;
    [ObservableProperty] private string _statusText = "Carregando páginas...";

    public System.Collections.ObjectModel.ObservableCollection<PdfPageThumbViewModel> Pages { get; } = [];

    public PdfImportViewModel(string pdfPath, Action<string, string> onImageReady)
    {
        _pdfPath = pdfPath;
        _onImageReady = onImageReady;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _bytes = await File.ReadAllBytesAsync(_pdfPath);
        PageCount = Conversion.GetPageCount(_bytes);
        for (int i = 1; i <= PageCount; i++)
            Pages.Add(new PdfPageThumbViewModel { PageNumber = i });
        if (Pages.Count > 0)
            SelectedPage = Pages[0];

        // Miniaturas em background, uma a uma — a lista preenche progressivamente.
        _ = Task.Run(() => RenderThumbsAsync(_cts.Token));
    }

    private async Task RenderThumbsAsync(CancellationToken ct)
    {
        for (int i = 0; i < Pages.Count && !ct.IsCancellationRequested; i++)
        {
            int page = i;
            try
            {
                using var bmp = Conversion.ToImage(_bytes!, page: (Index)page, options: new(Dpi: 24));
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var wb = bmp.ToWriteableBitmap();
                    wb.Freeze();
                    Pages[page].Image = wb;
                });
            }
            catch (Exception)
            {
                // Página problemática não derruba o resto do catálogo.
            }
        }
        await Application.Current.Dispatcher.InvokeAsync(() => StatusText = $"{PageCount} página(s).");
    }

    partial void OnSelectedPageChanged(PdfPageThumbViewModel? value) => _ = RenderPreviewAsync();

    private async Task RenderPreviewAsync()
    {
        if (_bytes is null || SelectedPage is null) return;
        int page = SelectedPage.PageNumber - 1;
        var old = Preview;
        Preview = await Task.Run(() => Conversion.ToImage(_bytes, page: (Index)page,
            options: new(Dpi: 96))); // prévia; exportação usa o DPI configurável
        old?.Dispose();
    }

    [RelayCommand]
    private async Task ExportPage()
    {
        if (_bytes is null || SelectedPage is null) return;
        int pageNumber = SelectedPage.PageNumber;
        StatusText = "Renderizando em alta...";

        using var bitmap = await Task.Run(() => Conversion.ToImage(_bytes, page: (Index)(pageNumber - 1),
            options: new(Dpi: Dpi)));

        // Página A3 a 300 DPI explode memória — limita a maior dimensão a 4096px.
        SKBitmap final = bitmap;
        const int maxDim = 4096;
        int maxSide = Math.Max(bitmap.Width, bitmap.Height);
        if (maxSide > maxDim)
        {
            float scale = maxDim / (float)maxSide;
            final = bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)), SKFilterQuality.High);
        }

        string dest = Path.Combine(Path.GetDirectoryName(_pdfPath)!, $"page-{pageNumber}.png");
        using (var image = SKImage.FromBitmap(final))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        await using (var stream = File.Create(dest))
        {
            data.SaveTo(stream);
        }
        if (!ReferenceEquals(final, bitmap)) final.Dispose();

        StatusText = $"Salvo: {dest}";
        _onImageReady(dest, $"Página {pageNumber}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        Preview?.Dispose();
    }
}
