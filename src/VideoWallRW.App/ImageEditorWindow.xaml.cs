using System.Windows;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

public partial class ImageEditorWindow : Window
{
    private readonly ImageEditorViewModel _vm;
    private SKPoint? _dragStart;
    private SKPoint? _lastPaintPoint;
    private SKRect? _liveCropRect;

    public ImageEditorWindow(ImageEditorViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.BitmapChanged += () => Dispatcher.Invoke(() => Canvas.InvalidateVisual());
        Closed += (_, _) => viewModel.Dispose();
    }

    private void OnColorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ColorPicker.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string name })
            _vm.BrushColor = name switch
            {
                "Red" => SKColors.Red,
                "Black" => SKColors.Black,
                "White" => SKColors.White,
                "Yellow" => SKColors.Yellow,
                _ => SKColors.Red,
            };
    }

    /// <summary>Mapeia um ponto do controle (coordenadas WPF) pro espaço de pixels do
    /// bitmap, considerando o "letterbox" do desenho scale-to-fit em <see cref="OnPaintSurface"/>.</summary>
    private SKPoint ToImageSpace(Point p)
    {
        var bmp = _vm.Bitmap;
        double scale = Math.Min(Canvas.ActualWidth / bmp.Width, Canvas.ActualHeight / bmp.Height);
        double offsetX = (Canvas.ActualWidth - bmp.Width * scale) / 2;
        double offsetY = (Canvas.ActualHeight - bmp.Height * scale) / 2;
        return new SKPoint((float)((p.X - offsetX) / scale), (float)((p.Y - offsetY) / scale));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pt = ToImageSpace(e.GetPosition(Canvas));
        switch (_vm.Tool)
        {
            case EditorTool.Crop:
                _dragStart = pt;
                _liveCropRect = new SKRect(pt.X, pt.Y, pt.X, pt.Y);
                break;
            case EditorTool.Brush:
                _vm.BeginStroke();
                _lastPaintPoint = pt;
                break;
            case EditorTool.Text:
                _vm.ApplyText(pt);
                break;
        }
        Canvas.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pt = ToImageSpace(e.GetPosition(Canvas));

        if (_vm.Tool == EditorTool.Crop && _dragStart is { } start)
        {
            float w = pt.X - start.X;
            float h = pt.Y - start.Y;
            if (_vm.SelectedPreset?.Ratio is { } ratio)
                h = Math.Sign(h == 0 ? 1 : h) * Math.Abs(w) / (float)ratio;
            _liveCropRect = new SKRect(start.X, start.Y, start.X + w, start.Y + h);
            Canvas.InvalidateVisual();
        }
        else if (_vm.Tool == EditorTool.Brush && _lastPaintPoint is { } last)
        {
            _vm.PaintStroke(last, pt);
            _lastPaintPoint = pt;
            Canvas.InvalidateVisual();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        Canvas.ReleaseMouseCapture();
        _dragStart = null;
        _lastPaintPoint = null;
    }

    private void OnApplyCrop(object sender, RoutedEventArgs e)
    {
        if (_liveCropRect is { } rect)
        {
            _vm.ApplyCrop(SKRect.Create(
                Math.Min(rect.Left, rect.Right), Math.Min(rect.Top, rect.Bottom),
                Math.Abs(rect.Width), Math.Abs(rect.Height)));
            _liveCropRect = null;
        }
    }

    private void OnPaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x17, 0x17, 0x1A)); // mesmo InputBg do tema escuro
        var bmp = _vm.Bitmap;

        double scale = Math.Min(e.Info.Width / (double)bmp.Width, e.Info.Height / (double)bmp.Height);
        double offsetX = (e.Info.Width - bmp.Width * scale) / 2;
        double offsetY = (e.Info.Height - bmp.Height * scale) / 2;
        var dest = new SKRect((float)offsetX, (float)offsetY,
            (float)(offsetX + bmp.Width * scale), (float)(offsetY + bmp.Height * scale));
        canvas.DrawBitmap(bmp, dest);

        if (_liveCropRect is { } crop)
        {
            var screenRect = new SKRect(
                (float)(offsetX + Math.Min(crop.Left, crop.Right) * scale),
                (float)(offsetY + Math.Min(crop.Top, crop.Bottom) * scale),
                (float)(offsetX + Math.Max(crop.Left, crop.Right) * scale),
                (float)(offsetY + Math.Max(crop.Top, crop.Bottom) * scale));
            using var paint = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRect(screenRect, paint);
        }
    }
}
