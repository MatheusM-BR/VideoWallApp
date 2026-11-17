using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using VideoWallRW.App.ViewModels;

namespace VideoWallRW.App;

/// <summary>Ferramenta Seleção: marcar, mover, esticar, girar, espelhar, copiar/colar
/// (ver ImageEditorViewModel.Selection).</summary>
public partial class ImageEditorWindow
{
    private enum SelectDrag { None, Create, Move, Rotate, N, S, E, W, NE, NW, SE, SW }

    private SelectDrag _selectDrag;
    private SKPoint _selectStart;   // Create: canto inicial. Demais: ponto onde pegou.
    private SKRect _selectOrigin;   // retângulo (sem giro) no começo do arraste
    private float _selectOriginAngle;
    private bool _updatingSelectFields;
    /// <summary>Pontos do laço sendo desenhado (espaço da imagem).</summary>
    private readonly List<SKPoint> _lassoPoints = new();

    /// <summary>Distância (px de tela) da alça de giro acima da borda de cima.</summary>
    private const float RotateHandleGap = 26;

    /// <summary>Leva um ponto da tela pro "eixo da área" (desfaz o giro em torno do centro).</summary>
    private static SKPoint ToSelectionFrame(SKPoint p, SKRect r, float angle)
    {
        if (angle == 0) return p;
        var v = SelectionGeometry.Rotate(new SKPoint(p.X - r.MidX, p.Y - r.MidY), -angle);
        return new SKPoint(r.MidX + v.X, r.MidY + v.Y);
    }

    /// <summary>O que está sob o ponteiro: alça de giro, alça (8), miolo (mover) ou nada.</summary>
    private SelectDrag HitSelection(SKPoint pt)
    {
        if (_vm.Selection is not { } r) return SelectDrag.None;
        double scale = Math.Max(CanvasScale(), 0.01);
        float tol = (float)(9 / scale);
        var p = ToSelectionFrame(pt, r, _vm.SelectionAngle);

        float rotY = r.Top - (float)(RotateHandleGap / scale);
        if (Math.Abs(p.X - r.MidX) <= tol && Math.Abs(p.Y - rotY) <= tol) return SelectDrag.Rotate;

        bool l = Math.Abs(p.X - r.Left) <= tol, rt = Math.Abs(p.X - r.Right) <= tol;
        bool t = Math.Abs(p.Y - r.Top) <= tol, b = Math.Abs(p.Y - r.Bottom) <= tol;
        bool midX = Math.Abs(p.X - r.MidX) <= tol, midY = Math.Abs(p.Y - r.MidY) <= tol;
        if (l && t) return SelectDrag.NW;
        if (rt && t) return SelectDrag.NE;
        if (l && b) return SelectDrag.SW;
        if (rt && b) return SelectDrag.SE;
        if (t && midX) return SelectDrag.N;
        if (b && midX) return SelectDrag.S;
        if (l && midY) return SelectDrag.W;
        if (rt && midY) return SelectDrag.E;
        return r.Contains(p.X, p.Y) ? SelectDrag.Move : SelectDrag.None;
    }

    private Cursor CursorFor(SelectDrag d)
    {
        // Com a área girada, a seta de redimensionar acompanha o giro (arredondado a 45°).
        int turn = (int)Math.Round(_vm.SelectionAngle / 45.0);
        string[] ring = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        int idx = Array.IndexOf(ring, d.ToString());
        string dir = idx < 0 ? d.ToString() : ring[((idx + turn) % 8 + 8) % 8];
        return dir switch
        {
            "NW" or "SE" => Cursors.SizeNWSE,
            "NE" or "SW" => Cursors.SizeNESW,
            "N" or "S" => Cursors.SizeNS,
            "E" or "W" => Cursors.SizeWE,
            "Move" => Cursors.SizeAll,
            "Rotate" => Cursors.Hand,
            _ => Cursors.Cross,
        };
    }

    private void BeginSelectDrag(SKPoint pt)
    {
        var hit = HitSelection(pt);
        if (hit == SelectDrag.None && _vm.SelectMode == SelectShape.Varinha)
        {
            _vm.SelectByColorAt(pt);
            _selectDrag = SelectDrag.None;
            UpdateSelectFields();
            return;
        }
        if (hit == SelectDrag.None && _vm.SelectMode == SelectShape.Laço)
        {
            _vm.SetSelection(null);
            _lassoPoints.Clear();
            _lassoPoints.Add(ClampToCanvas(pt));
            _selectDrag = SelectDrag.Create;
            return;
        }
        if (hit == SelectDrag.None)
        {
            // Clique fora: aplica o que estava sendo mexido e começa uma área nova.
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
        _selectOriginAngle = _vm.SelectionAngle;
    }

    private void ContinueSelectDrag(SKPoint pt)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_selectDrag)
        {
            case SelectDrag.Create when _lassoPoints.Count > 0:
            {
                var p = ClampToCanvas(pt);
                // Só guarda ponto quando andou ~2 px de tela (laço leve, sem milhares de pontos).
                if (SKPoint.Distance(p, _lassoPoints[^1]) * CanvasScale() >= 2) _lassoPoints.Add(p);
                break;
            }
            case SelectDrag.Create:
            {
                var end = ClampToCanvas(pt);
                if (shift) // Shift: área quadrada
                {
                    float side = Math.Max(Math.Abs(end.X - _selectStart.X), Math.Abs(end.Y - _selectStart.Y));
                    end = ClampToCanvas(new SKPoint(_selectStart.X + Math.Sign(end.X - _selectStart.X) * side,
                                                    _selectStart.Y + Math.Sign(end.Y - _selectStart.Y) * side));
                }
                _vm.SetSelection(new SKRect(_selectStart.X, _selectStart.Y, end.X, end.Y));
                break;
            }
            case SelectDrag.Rotate:
                // Shift: de 15 em 15 graus.
                _vm.SetSelectionAngle(SelectionGeometry.AngleFromCenter(
                    new SKPoint(_selectOrigin.MidX, _selectOrigin.MidY), pt, shift));
                break;
            case SelectDrag.None:
                break;
            default:
                _vm.SetFloatingRect(SelectionGeometry.TransformRotated(_selectOrigin, _selectDrag.ToString(),
                    pt.X - _selectStart.X, pt.Y - _selectStart.Y, shift, _selectOriginAngle));
                break;
        }
        UpdateSelectFields();
    }

    private void EndSelectDrag()
    {
        if (_selectDrag == SelectDrag.Create && _lassoPoints.Count > 0)
        {
            if (_lassoPoints.Count >= 3)
            {
                using var path = new SKPath();
                path.AddPoly(_lassoPoints.ToArray(), close: true);
                _vm.SetSelectionPath(path);
            }
            _lassoPoints.Clear();
            _selectDrag = SelectDrag.None;
            UpdateSelectFields();
            return;
        }
        // Clique sem arrastar (área minúscula) só desmarca.
        if (_selectDrag == SelectDrag.Create && _vm.Selection is { } r && (r.Width < 2 || r.Height < 2))
            _vm.SetSelection(null);
        _selectDrag = SelectDrag.None;
        UpdateSelectFields();
    }

    private SKPoint ClampToCanvas(SKPoint p) => new(
        Math.Clamp(p.X, 0, _vm.Bitmap.Width), Math.Clamp(p.Y, 0, _vm.Bitmap.Height));

    /// <summary>H / V / R (Shift+R) com uma camada selecionada nas ferramentas de camada.</summary>
    private bool HandleLayerTransformKey(Key key)
    {
        if (_vm.Tool is not (EditorTool.Layers or EditorTool.Shape or EditorTool.Text) || _vm.ActiveLayer is null) return false;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return false;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (key)
        {
            case Key.H: _vm.FlipLayerCommand.Execute("h"); break;
            case Key.V: _vm.FlipLayerCommand.Execute("v"); break;
            case Key.R: _vm.RotateLayerCommand.Execute(shift ? "anti" : "horario"); break;
            default: return false;
        }
        Canvas.InvalidateVisual();
        return true;
    }

    /// <summary>Teclas da Seleção (sem Ctrl). Devolve true se tratou.</summary>
    private bool HandleSelectKey(Key key)
    {
        if (_vm.Tool != EditorTool.Select || _vm.Selection is not { } r) return false;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return false;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (key)
        {
            case Key.Enter: _vm.CommitSelection(); break;
            case Key.Escape: _vm.CancelSelection(); break;
            case Key.Delete or Key.Back: _vm.DeleteSelection(); break;
            case Key.H: _vm.FlipSelection(horizontal: true); break;
            case Key.V: _vm.FlipSelection(horizontal: false); break;
            case Key.R: _vm.RotateSelection90(clockwise: !shift); break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                float step = shift ? 10 : 1;
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

    // ── Copiar / recortar / colar (com a área de transferência do Windows) ──────

    /// <summary>Ctrl+C / Ctrl+X / Ctrl+V. Devolve true se tratou.</summary>
    private bool HandleClipboardKey(Key key)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return false;
        if (key == Key.V || (key is Key.C or Key.X && _vm.Selection is not null))
        {
            RunClipboardAction(key);
            return true;
        }
        return false;
    }

    private void OnCopySelectionClick(object sender, RoutedEventArgs e) => RunClipboardAction(Key.C);
    private void OnCutSelectionClick(object sender, RoutedEventArgs e) => RunClipboardAction(Key.X);
    private void OnPasteClick(object sender, RoutedEventArgs e) => RunClipboardAction(Key.V);

    private void RunClipboardAction(Key key)
    {
        switch (key)
        {
            case Key.C: if (_vm.CopySelection() is { } copy) PutOnSystemClipboard(copy); break;
            case Key.X: if (_vm.CutSelection() is { } cut) PutOnSystemClipboard(cut); break;
            case Key.V: PasteFromClipboard(); break;
        }
        UpdateSelectFields();
        Canvas.InvalidateVisual();
    }

    /// <summary>Cola a imagem da área de transferência do Windows (print, imagem copiada
    /// de outro programa) ou, se não houver, a última área copiada no editor.</summary>
    private void PasteFromClipboard()
    {
        SKBitmap? fromSystem = null;
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } source)
                fromSystem = ToSkBitmap(source);
        }
        catch { /* área de transferência ocupada por outro programa: usa a cópia interna */ }

        bool ok = _vm.PasteImage(fromSystem);
        fromSystem?.Dispose();
        if (!ok) _vm.StatusText = "Nada para colar — copie uma área (Ctrl+C) ou uma imagem de outro programa.";
    }

    private static void PutOnSystemClipboard(SKBitmap bitmap)
    {
        try
        {
            var wb = new WriteableBitmap(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, bitmap.Width, bitmap.Height), bitmap.GetPixels(),
                bitmap.ByteCount, bitmap.RowBytes);
            wb.Freeze();
            Clipboard.SetImage(wb);
        }
        catch { /* sem a área de transferência do Windows, a cópia interna continua valendo */ }
    }

    private static SKBitmap ToSkBitmap(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        // Muitos programas (e o Print Screen) mandam a imagem com alfa zerado: trata como opaca.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        return bmp;
    }

    // ── Campos do painel ────────────────────────────────────────────────────────

    private void UpdateSelectFields()
    {
        if (SelectW is null || SelectH is null || SelectAngle is null) return;
        _updatingSelectFields = true;
        if (_vm.Selection is { } r)
        {
            if (!SelectW.IsKeyboardFocused) SelectW.Text = ((int)Math.Round(r.Width)).ToString(CultureInfo.InvariantCulture);
            if (!SelectH.IsKeyboardFocused) SelectH.Text = ((int)Math.Round(r.Height)).ToString(CultureInfo.InvariantCulture);
            if (!SelectAngle.IsKeyboardFocused) SelectAngle.Text = Math.Round(_vm.SelectionAngle, 1).ToString(CultureInfo.InvariantCulture);
        }
        else { SelectW.Text = ""; SelectH.Text = ""; SelectAngle.Text = ""; }
        _updatingSelectFields = false;
    }

    private void OnSelectFieldCommit(object sender, RoutedEventArgs e)
    {
        if (_updatingSelectFields || _vm.Selection is not { } r) return;
        if (ReferenceEquals(sender, SelectAngle))
        {
            if (float.TryParse(SelectAngle.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float deg) &&
                Math.Abs(deg - _vm.SelectionAngle) >= 0.05f)
                _vm.SetSelectionAngle(deg);
        }
        else
        {
            if (!float.TryParse(SelectW.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float w) || w < 1) return;
            if (!float.TryParse(SelectH.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float h) || h < 1) return;
            if (Math.Abs(w - r.Width) < 0.5f && Math.Abs(h - r.Height) < 0.5f) return;
            _vm.ResizeSelectionTo(w, h);
        }
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
    /// escura) + 8 alças + alça de giro + tamanho (e % / ângulo).</summary>
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
        if (_vm.Tool != EditorTool.Select) return;

        // Laço sendo desenhado: linha tracejada seguindo o mouse.
        if (_lassoPoints.Count > 1)
        {
            using var live = new SKPath();
            live.AddPoly(_lassoPoints.Select(p => new SKPoint((float)(offsetX + p.X * scale), (float)(offsetY + p.Y * scale))).ToArray(), close: false);
            using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            using var light = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true, PathEffect = SKPathEffect.CreateDash([5f, 5f], 0) };
            canvas.DrawPath(live, dark);
            canvas.DrawPath(live, light);
        }
        if (_vm.Selection is not { } r) return;

        // Laço/varinha: contorno real da área (as alças continuam na caixa em volta).
        if (_vm.SelectionOutline is { } outline && !_vm.IsSelectionFloating)
        {
            using var screenPath = new SKPath(outline);
            screenPath.Transform(SKMatrix.CreateScaleTranslation((float)scale, (float)scale, (float)offsetX, (float)offsetY));
            using var tint = new SKPaint { Color = new SKColor(0x4D, 0xA3, 0xFF, 50), Style = SKPaintStyle.Fill };
            using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            using var light = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true, PathEffect = SKPathEffect.CreateDash([5f, 5f], 0) };
            canvas.DrawPath(screenPath, tint);
            canvas.DrawPath(screenPath, dark);
            canvas.DrawPath(screenPath, light);
        }

        var box = new SKRect((float)(offsetX + r.Left * scale), (float)(offsetY + r.Top * scale),
                             (float)(offsetX + r.Right * scale), (float)(offsetY + r.Bottom * scale));
        float angle = _vm.SelectionAngle;
        canvas.Save();
        if (angle != 0) canvas.RotateDegrees(angle, box.MidX, box.MidY);

        using (var dark = new SKPaint { Color = new SKColor(0, 0, 0, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            canvas.DrawRect(box, dark);
        using (var light = new SKPaint
        {
            Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash([5f, 5f], 0),
        })
            canvas.DrawRect(box, light);

        using var grip = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var gripEdge = new SKPaint { Color = new SKColor(0x1F, 0x6F, 0xD1), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
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
        // Alça de giro: bolinha acima do meio da borda de cima, ligada por uma haste.
        var knob = new SKPoint(box.MidX, box.Top - RotateHandleGap);
        canvas.DrawLine(box.MidX, box.Top, knob.X, knob.Y + 5, gripEdge);
        canvas.DrawCircle(knob, 5.5f, grip);
        canvas.DrawCircle(knob, 5.5f, gripEdge);
        canvas.Restore();

        string label = $"{(int)Math.Round(r.Width)} × {(int)Math.Round(r.Height)} px";
        var src = _vm.FloatingSourceSize;
        if (_vm.IsSelectionFloating && src.Width > 0 && src.Height > 0 &&
            (Math.Abs(r.Width - src.Width) >= 1 || Math.Abs(r.Height - src.Height) >= 1))
            label += $"  ({r.Width / src.Width:P0} × {r.Height / src.Height:P0})";
        if (angle != 0) label += $"  {angle:0.#}°";
        using var labelBg = new SKPaint { Color = new SKColor(20, 20, 24, 220) };
        using var labelText = new SKPaint { Color = SKColors.White, TextSize = 12, IsAntialias = true };
        var bounds = ImageEditorViewModel.RotatedBounds(box, angle);
        float lw = labelText.MeasureText(label) + 14;
        // O rótulo fica fora da alça de giro: acima dela (sem giro) ou acima do contorno girado.
        float ly = bounds.Top > 56 ? bounds.Top - (angle == 0 ? 54 : 26) : bounds.Bottom + 8;
        canvas.DrawRoundRect(new SKRect(bounds.Left, ly, bounds.Left + lw, ly + 20), 3, 3, labelBg);
        canvas.DrawText(label, bounds.Left + 7, ly + 14, labelText);
    }
}
