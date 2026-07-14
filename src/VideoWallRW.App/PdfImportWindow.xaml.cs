using System.ComponentModel;
using System.Windows;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

public partial class PdfImportWindow : Window
{
    private readonly PdfImportViewModel _vm;

    public PdfImportWindow(PdfImportViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfImportViewModel.Preview))
            Dispatcher.Invoke(() => PreviewElement.InvalidateVisual());
    }

    private void OnPaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x17, 0x17, 0x1A)); // mesmo InputBg do tema escuro
        if (_vm.Preview is not { } bitmap) return;

        float scale = Math.Min((float)e.Info.Width / bitmap.Width, (float)e.Info.Height / bitmap.Height);
        var dest = new SKRect(0, 0, bitmap.Width * scale, bitmap.Height * scale);
        canvas.DrawBitmap(bitmap, dest);
    }
}
