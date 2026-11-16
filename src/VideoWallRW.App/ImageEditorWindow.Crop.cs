using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using VideoWallRW.App.ViewModels;
using VideoWallRW.App.Controls;

namespace VideoWallRW.App;

public partial class ImageEditorWindow
{
    // ── Quadro de corte: criar, mover e redimensionar ──────────────────────────

    private SKRect? NormalizedCrop() => _liveCropRect is { } r
        ? new SKRect(Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom),
                     Math.Max(r.Left, r.Right), Math.Max(r.Top, r.Bottom))
        : null;

    private void BeginCropDrag(SKPoint pt)
    {
        pt = new SKPoint(Math.Clamp(pt.X, 0, _vm.Bitmap.Width), Math.Clamp(pt.Y, 0, _vm.Bitmap.Height));
        float tolerance = (float)(14 / Math.Max(CanvasScale(), 0.01)); // ~14px de tela

        if (NormalizedCrop() is { } rect)
        {
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

            // ARESTAS (as 4 alças do meio dos lados, como no Paint 3D): mexem em UM
            // lado só, mantendo os outros três parados. Sem isto, as alças de aresta
            // apareciam desenhadas mas não pegavam — afordância mentindo pro operador.
            bool nearLeft = Math.Abs(pt.X - rect.Left) <= tolerance;
            bool nearRight = Math.Abs(pt.X - rect.Right) <= tolerance;
            bool nearTop = Math.Abs(pt.Y - rect.Top) <= tolerance;
            bool nearBottom = Math.Abs(pt.Y - rect.Bottom) <= tolerance;
            bool insideV = pt.Y > rect.Top - tolerance && pt.Y < rect.Bottom + tolerance;
            bool insideH = pt.X > rect.Left - tolerance && pt.X < rect.Right + tolerance;

            _cropEdge = (nearLeft, nearRight, nearTop, nearBottom) switch
            {
                (true, false, false, false) when insideV => CropEdge.Left,
                (false, true, false, false) when insideV => CropEdge.Right,
                (false, false, true, false) when insideH => CropEdge.Top,
                (false, false, false, true) when insideH => CropEdge.Bottom,
                _ => CropEdge.None,
            };
            if (_cropEdge != CropEdge.None)
            {
                _cropMode = CropDragMode.ResizeEdge;
                _cropFixed = rect;   // os outros três lados ficam onde estão
                ContinueCropDrag(pt);
                return;
            }

            if (rect.Contains(pt.X, pt.Y))
            {
                _cropMode = CropDragMode.Move;
                _cropAnchor = new SKPoint(pt.X - rect.Left, pt.Y - rect.Top);
                return;
            }
        }

        _cropMode = CropDragMode.Create;
        _dragStart = pt;
        _liveCropRect = new SKRect(pt.X, pt.Y, pt.X, pt.Y);
    }

    private void ContinueCropDrag(SKPoint pt)
    {
        switch (_cropMode)
        {
            case CropDragMode.Create when _dragStart is { } start:
                _liveCropRect = ImageSelectionGeometry.FromCorner(start, pt,
                    _vm.Bitmap.Width, _vm.Bitmap.Height, CropRatio());
                break;

            case CropDragMode.Resize:
                _liveCropRect = ImageSelectionGeometry.FromCorner(_cropAnchor, pt,
                    _vm.Bitmap.Width, _vm.Bitmap.Height, CropRatio());
                break;

            case CropDragMode.ResizeEdge:
            {
                _liveCropRect = ImageSelectionGeometry.ResizeEdge(_cropFixed, _cropEdge.ToString(), pt,
                    _vm.Bitmap.Width, _vm.Bitmap.Height, CropRatio());
                break;
            }

            case CropDragMode.Move when NormalizedCrop() is { } rect:
                var bmp = _vm.Bitmap;
                float left = Math.Clamp(pt.X - _cropAnchor.X, 0, Math.Max(bmp.Width - rect.Width, 0));
                float top = Math.Clamp(pt.Y - _cropAnchor.Y, 0, Math.Max(bmp.Height - rect.Height, 0));
                _liveCropRect = new SKRect(left, top, left + rect.Width, top + rect.Height);
                break;
        }
    }

    private double? CropRatio()
        => _vm.SelectedPreset?.Ratio is > 0 ? _vm.SelectedPreset.Ratio : null;

    private float HeightFor(float w, float h)
        => _vm.LockAspect && _vm.SelectedPreset?.Ratio is { } ratio
            ? Math.Sign(h == 0 ? 1 : h) * Math.Abs(w) / (float)ratio
            : h;

    private void UpdateCropFields()
    {
        if (NormalizedCrop() is { } rect)
        {
            CropW.Text = ((int)rect.Width).ToString(CultureInfo.InvariantCulture);
            CropH.Text = ((int)rect.Height).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void OnCropFieldChanged(object sender, RoutedEventArgs e)
    {
        // Editar W/H redimensiona a seleção ancorada no canto superior esquerdo.
        if (NormalizedCrop() is not { } rect) return;
        if (!int.TryParse(CropW.Text, out int w) || w <= 0 ||
            !int.TryParse(CropH.Text, out int h) || h <= 0) return;

        bool heightEdited = ReferenceEquals(sender, CropH);
        _liveCropRect = ImageSelectionGeometry.FitSize(rect, w, h, _vm.Bitmap.Width, _vm.Bitmap.Height,
            CropRatio(), heightEdited);
        UpdateCropFields();
        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// Teclado do editor, no padrão do Paint 3D: ENTER aplica o recorte pendente,
    /// ESC descarta o quadro. Só age quando a ferramenta de recorte está ativa e há um
    /// quadro na tela — fora disso a tecla segue o caminho normal (campos de texto etc.).
    /// </summary>
}
