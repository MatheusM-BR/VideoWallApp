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

    // Interação com o quadro de corte depois de criado: mover (arrastar por dentro)
    // ou redimensionar (arrastar um canto). Fora do quadro = começa seleção nova.
    private enum CropDragMode { None, Create, Move, Resize }
    private CropDragMode _cropMode;
    private SKPoint _cropAnchor; // Resize: canto oposto fixo · Move: offset do clique ao canto sup-esq.

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
                BeginCropDrag(pt);
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

        if (_vm.Tool == EditorTool.Crop && _cropMode != CropDragMode.None)
        {
            ContinueCropDrag(pt);
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
        _cropMode = CropDragMode.None; // o quadro de corte permanece, editável
        Canvas.InvalidateVisual();
    }

    // ── Quadro de corte: criar, mover e redimensionar ──────────────────────────

    private double CanvasScale()
    {
        var bmp = _vm.Bitmap;
        return Math.Min(Canvas.ActualWidth / bmp.Width, Canvas.ActualHeight / bmp.Height);
    }

    private SKRect? NormalizedCrop() => _liveCropRect is { } r
        ? new SKRect(Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom),
                     Math.Max(r.Left, r.Right), Math.Max(r.Top, r.Bottom))
        : null;

    private void BeginCropDrag(SKPoint pt)
    {
        float tolerance = (float)(14 / Math.Max(CanvasScale(), 0.01)); // ~14px de tela

        if (NormalizedCrop() is { } rect)
        {
            // Perto de um canto → redimensiona (o canto oposto fica fixo).
            SKPoint[] corners =
            [
                new(rect.Left, rect.Top), new(rect.Right, rect.Top),
                new(rect.Left, rect.Bottom), new(rect.Right, rect.Bottom),
            ];
            for (int i = 0; i < corners.Length; i++)
            {
                if (SKPoint.Distance(pt, corners[i]) <= tolerance)
                {
                    _cropMode = CropDragMode.Resize;
                    _cropAnchor = corners[3 - i]; // canto oposto
                    ContinueCropDrag(pt);
                    return;
                }
            }

            // Dentro do quadro → move inteiro.
            if (rect.Contains(pt.X, pt.Y))
            {
                _cropMode = CropDragMode.Move;
                _cropAnchor = new SKPoint(pt.X - rect.Left, pt.Y - rect.Top);
                return;
            }
        }

        // Fora → começa uma seleção nova.
        _cropMode = CropDragMode.Create;
        _dragStart = pt;
        _liveCropRect = new SKRect(pt.X, pt.Y, pt.X, pt.Y);
    }

    private void ContinueCropDrag(SKPoint pt)
    {
        switch (_cropMode)
        {
            case CropDragMode.Create when _dragStart is { } start:
                _liveCropRect = new SKRect(start.X, start.Y,
                    start.X + WidthFor(pt.X - start.X),
                    start.Y + HeightFor(pt.X - start.X, pt.Y - start.Y));
                break;

            case CropDragMode.Resize:
                float w = pt.X - _cropAnchor.X;
                _liveCropRect = new SKRect(_cropAnchor.X, _cropAnchor.Y,
                    _cropAnchor.X + WidthFor(w),
                    _cropAnchor.Y + HeightFor(w, pt.Y - _cropAnchor.Y));
                break;

            case CropDragMode.Move when NormalizedCrop() is { } rect:
                var bmp = _vm.Bitmap;
                float left = Math.Clamp(pt.X - _cropAnchor.X, 0, Math.Max(bmp.Width - rect.Width, 0));
                float top = Math.Clamp(pt.Y - _cropAnchor.Y, 0, Math.Max(bmp.Height - rect.Height, 0));
                _liveCropRect = new SKRect(left, top, left + rect.Width, top + rect.Height);
                break;
        }
    }

    private static float WidthFor(float w) => w;

    private float HeightFor(float w, float h)
        => _vm.SelectedPreset?.Ratio is { } ratio
            ? Math.Sign(h == 0 ? 1 : h) * Math.Abs(w) / (float)ratio
            : h;

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

            // Alças nos cantos: arrastar redimensiona; arrastar por dentro move.
            using var handle = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Fill };
            const float hs = 5;
            foreach (var c in new[] { a, new SKPoint(b.X, a.Y), new SKPoint(a.X, b.Y), b })
                canvas.DrawRect(new SKRect(c.X - hs, c.Y - hs, c.X + hs, c.Y + hs), handle);
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
