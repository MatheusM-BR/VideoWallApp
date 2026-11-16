using System.Globalization;
using System.Windows;
using System.Windows.Input;
using SkiaSharp;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

/// <summary>Ferramenta Seleção: marcar área, mover e esticar (ver ImageEditorViewModel.Selection).</summary>
public partial class ImageEditorWindow
{
    private enum SelectDrag { None, Create, Move, N, S, E, W, NE, NW, SE, SW }

    private SelectDrag _selectDrag;
    private SKPoint _selectStart;   // Create: canto inicial. Move/esticar: ponto onde pegou.
    private SKRect _selectOrigin;   // retângulo no começo do arraste
    private bool _updatingSelectFields;

    /// <summary>O que está sob o ponteiro: alça (8), miolo (mover) ou nada (nova seleção).</summary>
    private SelectDrag HitSelection(SKPoint pt)
    {
        if (_vm.Selection is not { } r) return SelectDrag.None;
        float tol = (float)(9 / Math.Max(CanvasScale(), 0.01));
        bool l = Math.Abs(pt.X - r.Left) <= tol, rt = Math.Abs(pt.X - r.Right) <= tol;
        bool t = Math.Abs(pt.Y - r.Top) <= tol, b = Math.Abs(pt.Y - r.Bottom) <= tol;
        bool midX = Math.Abs(pt.X - r.MidX) <= tol, midY = Math.Abs(pt.Y - r.MidY) <= tol;
        if (l && t) return SelectDrag.NW;
        if (rt && t) return SelectDrag.NE;
        if (l && b) return SelectDrag.SW;
        if (rt && b) return SelectDrag.SE;
        if (t && midX) return SelectDrag.N;
        if (b && midX) return SelectDrag.S;
        if (l && midY) return SelectDrag.W;
        if (rt && midY) return SelectDrag.E;
        return r.Contains(pt.X, pt.Y) ? SelectDrag.Move : SelectDrag.None;
    }

    private static Cursor CursorFor(SelectDrag d) => d switch
    {
        SelectDrag.NW or SelectDrag.SE => Cursors.SizeNWSE,
        SelectDrag.NE or SelectDrag.SW => Cursors.SizeNESW,
        SelectDrag.N or SelectDrag.S => Cursors.SizeNS,
        SelectDrag.E or SelectDrag.W => Cursors.SizeWE,
        SelectDrag.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private void BeginSelectDrag(SKPoint pt)
    {
        var hit = HitSelection(pt);
        if (hit == SelectDrag.None)
        {
            // Clique fora: aplica o que estava sendo esticado e começa uma área nova.
            _vm.CommitSelection();
            _selectDrag = SelectDrag.Create;
            _selectStart = ClampToCanvas(pt);
            _vm.SetSelection(null);
            return;
        }
        if (!_vm.LiftSelection()) { _selectDrag = SelectDrag.None; return; }
        _selectDrag = hit;
        _selectStart = pt;
        _selectOrigin = _vm.Selection ?? default;
    }

    private void ContinueSelectDrag(SKPoint pt)
    {
        bool keepRatio = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_selectDrag == SelectDrag.Create)
        {
            var end = ClampToCanvas(pt);
            if (keepRatio) // Shift: área quadrada
            {
                float side = Math.Max(Math.Abs(end.X - _selectStart.X), Math.Abs(end.Y - _selectStart.Y));
                end = ClampToCanvas(new SKPoint(_selectStart.X + Math.Sign(end.X - _selectStart.X) * side,
                                                _selectStart.Y + Math.Sign(end.Y - _selectStart.Y) * side));
            }
            _vm.SetSelection(new SKRect(_selectStart.X, _selectStart.Y, end.X, end.Y));
        }
        else if (_selectDrag != SelectDrag.None)
        {
            _vm.SetFloatingRect(SelectionGeometry.Transform(_selectOrigin, _selectDrag.ToString(),
                pt.X - _selectStart.X, pt.Y - _selectStart.Y, keepRatio));
        }
        UpdateSelectFields();
    }

    private void EndSelectDrag()
    {
        // Clique sem arrastar (área minúscula) só desmarca.
        if (_selectDrag == SelectDrag.Create && _vm.Selection is { } r && (r.Width < 2 || r.Height < 2))
            _vm.SetSelection(null);
        _selectDrag = SelectDrag.None;
        UpdateSelectFields();
    }

    private SKPoint ClampToCanvas(SKPoint p) => new(
        Math.Clamp(p.X, 0, _vm.Bitmap.Width), Math.Clamp(p.Y, 0, _vm.Bitmap.Height));

    /// <summary>Teclas da Seleção. Devolve true se tratou.</summary>
    private bool HandleSelectKey(Key key)
    {
        if (_vm.Tool != EditorTool.Select || _vm.Selection is not { } r) return false;
        switch (key)
        {
            case Key.Enter: _vm.CommitSelection(); break;
            case Key.Escape: _vm.CancelSelection(); break;
            case Key.Delete or Key.Back: _vm.DeleteSelection(); break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                float step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                float dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
                float dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
                if (!_vm.LiftSelection()) return true;
                r = _vm.Selection!.Value;
                _vm.SetFloatingRect(new SKRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy));
                break;
            default: return false;
        }
        UpdateSelectFields();
        Canvas.InvalidateVisual();
        return true;
    }

    private void UpdateSelectFields()
    {
        if (SelectW is null || SelectH is null) return;
        _updatingSelectFields = true;
        if (_vm.Selection is { } r)
        {
            if (!SelectW.IsKeyboardFocused) SelectW.Text = ((int)Math.Round(r.Width)).ToString(CultureInfo.InvariantCulture);
            if (!SelectH.IsKeyboardFocused) SelectH.Text = ((int)Math.Round(r.Height)).ToString(CultureInfo.InvariantCulture);
        }
        else { SelectW.Text = ""; SelectH.Text = ""; }
        _updatingSelectFields = false;
    }

    private void OnSelectFieldCommit(object sender, RoutedEventArgs e)
    {
        if (_updatingSelectFields || _vm.Selection is not { } r) return;
        if (!float.TryParse(SelectW.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float w) || w < 1) return;
        if (!float.TryParse(SelectH.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float h) || h < 1) return;
        if (Math.Abs(w - r.Width) < 0.5f && Math.Abs(h - r.Height) < 0.5f) return;
        _vm.ResizeSelectionTo(w, h);
        UpdateSelectFields();
        Canvas.InvalidateVisual();
    }

    private void OnSelectFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnSelectFieldCommit(sender, e);
        e.Handled = true;
    }

    /// <summary>Prévia do pedaço + tracejado em duas cores (aparece em foto clara ou
    /// escura) + 8 alças + tamanho (e % quando esticado).</summary>
    private void DrawSelectionOverlay(SKCanvas canvas, double scale, double offsetX, double offsetY, SKRect canvasDest)
    {
        if (_vm.IsSelectionFloating)
        {
            canvas.Save();
            canvas.ClipRect(canvasDest);
            canvas.Translate((float)offsetX, (float)offsetY);
            canvas.Scale((float)scale);
            _vm.DrawFloatingSelection(canvas);
            canvas.Restore();
        }
        if (_vm.Tool != EditorTool.Select || _vm.Selection is not { } r) return;

        var box = new SKRect((float)(offsetX + r.Left * scale), (float)(offsetY + r.Top * scale),
                             (float)(offsetX + r.Right * scale), (float)(offsetY + r.Bottom * scale));
        using (var dark = new SKPaint { Color = new SKColor(0, 0, 0, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            canvas.DrawRect(box, dark);
        using (var light = new SKPaint
        {
            Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash([5f, 5f], 0),
        })
            canvas.DrawRect(box, light);

        using var grip = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var gripEdge = new SKPaint { Color = new SKColor(0x1F, 0x6F, 0xD1), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
        const float hs = 4.5f;
        foreach (var c in new[]
        {
            new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Top),
            new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Bottom),
            new SKPoint(box.MidX, box.Top), new SKPoint(box.MidX, box.Bottom),
            new SKPoint(box.Left, box.MidY), new SKPoint(box.Right, box.MidY),
        })
        {
            var g = new SKRect(c.X - hs, c.Y - hs, c.X + hs, c.Y + hs);
            canvas.DrawRect(g, grip);
            canvas.DrawRect(g, gripEdge);
        }

        string label = $"{(int)Math.Round(r.Width)} × {(int)Math.Round(r.Height)} px";
        var src = _vm.FloatingSourceSize;
        if (_vm.IsSelectionFloating && src.Width > 0 && src.Height > 0 &&
            (Math.Abs(r.Width - src.Width) >= 1 || Math.Abs(r.Height - src.Height) >= 1))
            label += $"  ({r.Width / src.Width:P0} × {r.Height / src.Height:P0})";
        using var labelBg = new SKPaint { Color = new SKColor(20, 20, 24, 220) };
        using var labelText = new SKPaint { Color = SKColors.White, TextSize = 12, IsAntialias = true };
        float lw = labelText.MeasureText(label) + 14;
        float ly = box.Top > 28 ? box.Top - 26 : box.Bottom + 8;
        canvas.DrawRoundRect(new SKRect(box.Left, ly, box.Left + lw, ly + 20), 3, 3, labelBg);
        canvas.DrawText(label, box.Left + 7, ly + 14, labelText);
    }
}
