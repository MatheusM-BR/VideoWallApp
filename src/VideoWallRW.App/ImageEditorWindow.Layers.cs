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
    // ── Camadas: adicionar, mover e redimensionar ─────────────────────────────

    private enum LayerDragMode { None, Move, Scale }
    private LayerDragMode _layerDrag;
    private SKPoint _layerDragStart;
    private SKRect _layerOrigin;
    private LayerCorner _layerGrip;

    private void OnAddLayer(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Adicionar camada",
            Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp|Todos os arquivos|*.*",
            InitialDirectory = AppPaths.EnsureMediaRoot(),
        };
        if (dlg.ShowDialog() == true)
        {
            _vm.AddImageLayer(dlg.FileName);
            Canvas.InvalidateVisual();
        }
    }

    /// <summary>
    /// Pega uma camada: canto = redimensiona, dentro = move. Na ferramenta Camadas,
    /// clicar fora seleciona outra camada sob o cursor. Em Forma/Texto
    /// (<paramref name="kind"/>), só camadas daquele tipo são pegas; fora delas devolve
    /// false e a ferramenta cria uma nova.
    /// </summary>
    private bool BeginLayerDrag(SKPoint pt, EditorLayerKind? kind, bool createOutside = false)
    {
        _layerDrag = LayerDragMode.None;
        bool Eligible(EditorLayer l) => l.Visible && (kind is null || l.Kind == kind);
        EditorLayer? TopmostAt(SKPoint p) => _vm.Layers.LastOrDefault(l => Eligible(l) && l.Bounds.Contains(p.X, p.Y));

        float tol = (float)(12 / Math.Max(CanvasScale(), 0.01));
        var layer = _vm.ActiveLayer is { } active && Eligible(active) ? active : null;

        // Qual das quatro alças foi pega (a mais próxima dentro da tolerância) — cada
        // canto ancora no oposto, então precisa saber QUAL, não só "perto de um canto".
        LayerCorner? grip = null;
        if (layer is not null)
        {
            var b = layer.Bounds;
            float best = tol;
            foreach (var (corner, at) in new (LayerCorner, SKPoint)[]
            {
                (LayerCorner.TopLeft, new SKPoint(b.Left, b.Top)),
                (LayerCorner.TopRight, new SKPoint(b.Right, b.Top)),
                (LayerCorner.BottomLeft, new SKPoint(b.Left, b.Bottom)),
                (LayerCorner.BottomRight, new SKPoint(b.Right, b.Bottom)),
            })
            {
                float d = SKPoint.Distance(pt, at);
                if (d <= best) { best = d; grip = corner; }
            }
        }

        if (layer is not null && grip is { } g) { _layerDrag = LayerDragMode.Scale; _layerGrip = g; }
        else if (layer is not null && layer.Bounds.Contains(pt.X, pt.Y)) _layerDrag = LayerDragMode.Move;
        else if (TopmostAt(pt) is { } hit)
        {
            // Clique em outra camada: seleciona a mais alta sob o cursor e já move.
            _vm.ActiveLayer = hit;
            layer = hit;
            _layerDrag = LayerDragMode.Move;
        }
        else
        {
            if (!createOutside) { _vm.ActiveLayer = null; Canvas.InvalidateVisual(); }
            return false;
        }

        _layerDragStart = pt;
        _layerOrigin = layer.Bounds;
        _vm.BeginLayerTransform();
        return true;
    }

    private void ContinueLayerDrag(SKPoint pt)
    {
        if (_vm.ActiveLayer is not { } layer || _layerDrag == LayerDragMode.None) return;

        if (_layerDrag == LayerDragMode.Move)
        {
            float x = _layerOrigin.Left + (pt.X - _layerDragStart.X);
            float y = _layerOrigin.Top + (pt.Y - _layerDragStart.Y);
            (x, y) = ApplySnap(layer, x, y);
            // Mantém ao menos uma faixa da camada acessível para ela nunca se perder fora da tela.
            float visibleX = Math.Min(24 / (float)Math.Max(CanvasScale(), .01), layer.Width);
            float visibleY = Math.Min(24 / (float)Math.Max(CanvasScale(), .01), layer.Height);
            layer.X = Math.Clamp(x, -layer.Width + visibleX, _vm.Bitmap.Width - visibleX);
            layer.Y = Math.Clamp(y, -layer.Height + visibleY, _vm.Bitmap.Height - visibleY);
        }
        else
        {
            // Canto OPOSTO parado. Imagem/texto não deformam; forma é livre — Shift inverte.
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            bool free = layer.KeepsAspect ? shift : !shift;
            layer.ResizeFromCorner(_layerGrip, _layerOrigin, pt, free);
        }
    }

    /// <summary>
    /// Ímãs: a camada em movimento gruda quando a borda ou o CENTRO dela chega perto
    /// do centro/bordas da tela ou de outra camada (~8px de tela). Alt segura desliga.
    /// </summary>
    private (float X, float Y) ApplySnap(EditorLayer layer, float x, float y)
    {
        _snapGuidesX.Clear();
        _snapGuidesY.Clear();
        if (!_vm.SnapEnabled || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return (x, y);

        float tol = (float)(8 / Math.Max(CanvasScale(), 0.01));
        float w = _vm.Bitmap.Width, h = _vm.Bitmap.Height;
        var targetsX = new List<float> { 0, w / 2, w };
        var targetsY = new List<float> { 0, h / 2, h };
        foreach (var other in _vm.Layers)
        {
            if (ReferenceEquals(other, layer) || !other.Visible) continue;
            var b = other.Bounds;
            targetsX.AddRange([b.Left, b.MidX, b.Right]);
            targetsY.AddRange([b.Top, b.MidY, b.Bottom]);
        }

        static float? BestDelta(float start, float size, List<float> targets, float tol, out float guide)
        {
            guide = 0;
            float? best = null;
            foreach (float t in targets)
                foreach (float edge in new[] { start, start + size / 2, start + size })
                {
                    float d = t - edge;
                    if (Math.Abs(d) <= tol && (best is null || Math.Abs(d) < Math.Abs(best.Value)))
                    {
                        best = d;
                        guide = t;
                    }
                }
            return best;
        }

        if (BestDelta(x, layer.Width, targetsX, tol, out float gx) is { } dx) { x += dx; _snapGuidesX.Add(gx); }
        if (BestDelta(y, layer.Height, targetsY, tol, out float gy) is { } dy) { y += dy; _snapGuidesY.Add(gy); }
        return (x, y);
    }

    private void OnSnapToggled(object sender, RoutedEventArgs e) => Canvas.InvalidateVisual();

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (_vm.Tool == EditorTool.Crop && NormalizedCrop() is { } crop)
        {
            if (e.Key == Key.Enter) OnApplyCrop(sender, e);
            else if (e.Key == Key.Escape) OnCropCancel(sender, e);
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                float step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                float dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                float dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                float left = Math.Clamp(crop.Left + dx, 0, _vm.Bitmap.Width - crop.Width);
                float top = Math.Clamp(crop.Top + dy, 0, _vm.Bitmap.Height - crop.Height);
                _liveCropRect = new SKRect(left, top, left + crop.Width, top + crop.Height);
                UpdateCropFields();
                Canvas.InvalidateVisual();
            }
            else return;
            e.Handled = true;
        }
        else if (_vm.Tool is EditorTool.Layers or EditorTool.Shape or EditorTool.Text && _vm.ActiveLayer is { } selected &&
                 e.Key == Key.Delete)
        {
            _vm.RemoveLayerCommand.Execute(selected);
            e.Handled = true;
        }
        else if (_vm.Tool is EditorTool.Layers or EditorTool.Shape or EditorTool.Text && _vm.ActiveLayer is { } layer &&
                 e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            float step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            layer.X += e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            layer.Y += e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            Canvas.InvalidateVisual();
            e.Handled = true;
        }
    }

    private void OnSelectBaseLayer(object sender, RoutedEventArgs e)
    {
        _vm.SelectBackground();
        Canvas.InvalidateVisual();
    }

    private void OnCropCancel(object sender, RoutedEventArgs e)
    {
        _liveCropRect = new SKRect(0, 0, _vm.Bitmap.Width, _vm.Bitmap.Height);
        UpdateCropFields();
        Canvas.InvalidateVisual();
    }

    private void OnSelectFullCrop(object sender, RoutedEventArgs e) => OnCropCancel(sender, e);

    private void OnApplyCrop(object sender, RoutedEventArgs e)
    {
        if (NormalizedCrop() is { } rect)
        {
            _vm.ApplyCrop(rect);
            _liveCropRect = null;
            CropW.Text = "";
            CropH.Text = "";
            InitializeToolOverlay();
        }
    }

}
