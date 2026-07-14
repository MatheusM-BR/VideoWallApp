using System.Windows;
using System.Windows.Controls;
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
    private (SKPoint A, SKPoint B)? _liveShape;

    public ImageEditorWindow(ImageEditorViewModel viewModel)
    {
        // _vm ANTES do InitializeComponent: eventos de XAML podem disparar durante o parse.
        _vm = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.BitmapChanged += () => Dispatcher.Invoke(() => Canvas.InvalidateVisual());
        Closed += (_, _) => viewModel.Dispose();
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string hex })
            _vm.SetColorHex(hex);
    }

    private void OnCanvasSize(object sender, RoutedEventArgs e)
    {
        var dlg = new CanvasSizeWindow(_vm.Bitmap.Width, _vm.Bitmap.Height) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.StretchImage)
            _vm.ApplyResize(dlg.NewWidth, dlg.NewHeight);
        else
            _vm.ApplyCanvasResize(dlg.NewWidth, dlg.NewHeight);
    }

    /// <summary>Mapeia um ponto do controle (coordenadas WPF) pro espaço de pixels do
    /// bitmap, considerando o "letterbox" do desenho scale-to-fit.</summary>
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
            case EditorTool.Eraser:
                _vm.BeginStroke();
                _lastPaintPoint = pt;
                break;
            case EditorTool.Eyedropper:
                _vm.PickColorAt(pt);
                break;
            case EditorTool.Bucket:
                _vm.FloodFillAt(pt);
                break;
            case EditorTool.Shape:
                _dragStart = pt;
                _liveShape = (pt, pt);
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
        else if (_vm.Tool is EditorTool.Brush or EditorTool.Eraser && _lastPaintPoint is { } last)
        {
            _vm.PaintStroke(last, pt);
            _lastPaintPoint = pt;
            Canvas.InvalidateVisual();
        }
        else if (_vm.Tool == EditorTool.Shape && _dragStart is { } shapeStart)
        {
            _liveShape = (shapeStart, pt);
            Canvas.InvalidateVisual();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        Canvas.ReleaseMouseCapture();

        if (_vm.Tool == EditorTool.Shape && _liveShape is { } shape &&
            (Math.Abs(shape.B.X - shape.A.X) > 2 || Math.Abs(shape.B.Y - shape.A.Y) > 2))
        {
            _vm.ApplyShape(shape.A, shape.B);
        }

        _dragStart = null;
        _lastPaintPoint = null;
        _liveShape = null;
        Canvas.InvalidateVisual();
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

        SKPoint ToScreen(SKPoint img) => new(
            (float)(offsetX + img.X * scale), (float)(offsetY + img.Y * scale));

        if (_liveCropRect is { } crop)
        {
            var a = ToScreen(new SKPoint(Math.Min(crop.Left, crop.Right), Math.Min(crop.Top, crop.Bottom)));
            var b = ToScreen(new SKPoint(Math.Max(crop.Left, crop.Right), Math.Max(crop.Top, crop.Bottom)));
            using var paint = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRect(new SKRect(a.X, a.Y, b.X, b.Y), paint);
        }

        if (_liveShape is { } shape)
        {
            using var paint = new SKPaint
            {
                Color = _vm.BrushColor.WithAlpha(180),
                Style = _vm.ShapeFilled && _vm.Shape != ShapeKind.Linha ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                StrokeWidth = (float)(_vm.BrushSize * scale),
                IsAntialias = true,
            };
            var a = ToScreen(shape.A);
            var b = ToScreen(shape.B);
            var rect = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            switch (_vm.Shape)
            {
                case ShapeKind.Retângulo: canvas.DrawRect(rect, paint); break;
                case ShapeKind.Elipse: canvas.DrawOval(rect, paint); break;
                case ShapeKind.Linha: canvas.DrawLine(a, b, paint); break;
            }
        }
    }
}
