using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFtoImage;
using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

/// <summary>
/// Fase 10 T10.4: seleciona uma página de um PDF (catálogo/regulamento baixado da
/// Agenda) e renderiza em PNG de alta resolução na pasta do evento. Sem grid de
/// miniaturas de todas as páginas (simplificação deliberada — catálogos têm dezenas
/// de páginas; renderizar todas em baixa já seria custoso) — só o número da página e
/// uma prévia da atual.
/// </summary>
public sealed partial class PdfImportViewModel : ObservableObject
{
    private readonly string _pdfPath;
    private readonly Action<string, string> _onImageReady;
    private byte[]? _bytes;

    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private int _pageNumber = 1;
    [ObservableProperty] private SKBitmap? _preview;
    [ObservableProperty] private int _dpi = 200;
    [ObservableProperty] private string _statusText = "";

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
        await RenderPreviewAsync();
    }

    partial void OnPageNumberChanged(int value) => _ = RenderPreviewAsync();

    private async Task RenderPreviewAsync()
    {
        if (_bytes is null || PageCount == 0) return;
        int page = Math.Clamp(PageNumber - 1, 0, PageCount - 1);
        Preview?.Dispose();
        Preview = await Task.Run(() => Conversion.ToImage(_bytes, page: (Index)page,
            options: new(Dpi: 96))); // prévia em baixa — a exportação final usa o DPI configurável
    }

    [RelayCommand]
    private async Task ExportPage()
    {
        if (_bytes is null) return;
        StatusText = "Renderizando...";
        int page = Math.Clamp(PageNumber - 1, 0, PageCount - 1);

        using var bitmap = await Task.Run(() => Conversion.ToImage(_bytes, page: (Index)page,
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

        string dest = Path.Combine(Path.GetDirectoryName(_pdfPath)!, $"page-{PageNumber}.png");
        using (var image = SKImage.FromBitmap(final))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        await using (var stream = File.Create(dest))
        {
            data.SaveTo(stream);
        }
        if (!ReferenceEquals(final, bitmap)) final.Dispose();

        StatusText = $"Salvo: {Path.GetFileName(dest)}";
        _onImageReady(dest, $"Página {PageNumber}");
    }
}
