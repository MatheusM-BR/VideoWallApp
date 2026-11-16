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
    // ── Pintura ─────────────────────────────────────────────────────────────────

    private void OnPaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x17, 0x17, 0x1A)); // mesmo InputBg do tema escuro
        var bmp = _vm.Bitmap;
        Dispatcher.BeginInvoke(UpdateScrollBars, System.Windows.Threading.DispatcherPriority.Render);

        // Mesma transformação usada no hit-test (ViewTransform) — desenho e mouse
        // SEMPRE alinhados, com zoom manual da roda incluído.
        var (scale, offsetX, offsetY) = ViewTransform(e.Info.Width, e.Info.Height);
        var dest = new SKRect((float)offsetX, (float)offsetY,
            (float)(offsetX + bmp.Width * scale), (float)(offsetY + bmp.Height * scale));

        // Na ferramenta Tela, mostra a área que será criada antes de aplicar.
        if (_vm.Tool == EditorTool.Canvas && _liveCanvasSize is { } previewSize)
        {
            float extraX = (previewSize.X - bmp.Width) / 2f;
            float extraY = (previewSize.Y - bmp.Height) / 2f;
            var previewTopLeft = new SKPoint((float)(offsetX - extraX * scale), (float)(offsetY - extraY * scale));
            var previewBottomRight = new SKPoint((float)(offsetX + (bmp.Width + extraX) * scale), (float)(offsetY + (bmp.Height + extraY) * scale));
            using var previewFill = new SKPaint { Color = _vm.TransparentCanvas ? new SKColor(70, 70, 76) : SKColors.White };
            canvas.DrawRect(new SKRect(previewTopLeft.X, previewTopLeft.Y, previewBottomRight.X, previewBottomRight.Y), previewFill);
        }
        canvas.DrawBitmap(bmp, dest);

        SKPoint ToScreen(SKPoint img) => new(
            (float)(offsetX + img.X * scale), (float)(offsetY + img.Y * scale));

        // CAMADAS por cima da arte base (mesma ordem da lista: a última é a mais alta).
        // Desenhadas no espaço de tela — por isso o canvas é transformado pra escala
        // da visualização enquanto elas são compostas.
        if (_vm.Layers.Count > 0)
        {
            // Recortadas na área da tela: o que passa da borda NÃO vai pro arquivo
            // salvo, então também não aparece aqui (antes, depois de um recorte a foto
            // continuava "vazando" e parecia que o recorte não tinha funcionado).
            canvas.Save();
            canvas.ClipRect(dest);
            canvas.Translate((float)offsetX, (float)offsetY);
            canvas.Scale((float)scale);
            foreach (var layer in _vm.Layers) layer.Draw(canvas);
            canvas.Restore();
        }

        // Moldura + alças da camada selecionada (Camadas, e Forma/Texto pra camada do tipo).
        if (_vm.ActiveLayer is { } active &&
            (_vm.Tool == EditorTool.Layers || (ToolLayerKind() is { } k && active.Kind == k)))
        {
            var b = active.Bounds;
            var p1 = ToScreen(new SKPoint(b.Left, b.Top));
            var p2 = ToScreen(new SKPoint(b.Right, b.Bottom));
            var box = new SKRect(p1.X, p1.Y, p2.X, p2.Y);
            using (var pen = new SKPaint { Color = new SKColor(0x9A, 0xCD, 0x32), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                canvas.DrawRect(box, pen);
            using var grip = new SKPaint { Color = new SKColor(0x9A, 0xCD, 0x32), Style = SKPaintStyle.Fill };
            const float hs = 5;
            foreach (var c in new[]
            {
                new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Top),
                new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Bottom),
            })
                canvas.DrawRect(new SKRect(c.X - hs, c.Y - hs, c.X + hs, c.Y + hs), grip);
        }

        if (_vm.Tool == EditorTool.Crop && _liveCropRect is { } crop)
        {
            var a = ToScreen(new SKPoint(Math.Min(crop.Left, crop.Right), Math.Min(crop.Top, crop.Bottom)));
            var b = ToScreen(new SKPoint(Math.Max(crop.Left, crop.Right), Math.Max(crop.Top, crop.Bottom)));
            var sel = new SKRect(a.X, a.Y, b.X, b.Y);

            // VÉU sobre o que será DESCARTADO (estilo Paint 3D): o resto da imagem
            // continua visível, só escurecido. É isso que dá a leitura imediata do
            // enquadramento — só o contorno verde não mostrava o que se perde.
            using (var veil = new SKPaint { Color = new SKColor(0, 0, 0, 140) })
            {
                canvas.Save();
                canvas.ClipRect(sel, SKClipOperation.Difference);  // tudo MENOS a seleção
                canvas.DrawRect(dest, veil);
                canvas.Restore();
            }

            // Moldura clara + guias de terços (ajudam a enquadrar, como no Paint 3D).
            using (var border = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                canvas.DrawRect(sel, border);
            using (var thirds = new SKPaint { Color = new SKColor(255, 255, 255, 90), Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            {
                for (int i = 1; i <= 2; i++)
                {
                    float x = sel.Left + sel.Width * i / 3f;
                    float y = sel.Top + sel.Height * i / 3f;
                    canvas.DrawLine(x, sel.Top, x, sel.Bottom, thirds);
                    canvas.DrawLine(sel.Left, y, sel.Right, y, thirds);
                }
            }

            // 8 alças (4 cantos + 4 arestas), como no Paint 3D — brancas com contorno
            // escuro pra ficarem visíveis tanto sobre foto clara quanto escura.
            using var grip = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            using var gripEdge = new SKPaint { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            const float hs = 5;
            float midX = (sel.Left + sel.Right) / 2, midY = (sel.Top + sel.Bottom) / 2;
            foreach (var c in new[]
            {
                new SKPoint(sel.Left, sel.Top), new SKPoint(sel.Right, sel.Top),
                new SKPoint(sel.Left, sel.Bottom), new SKPoint(sel.Right, sel.Bottom),
                new SKPoint(midX, sel.Top), new SKPoint(midX, sel.Bottom),
                new SKPoint(sel.Left, midY), new SKPoint(sel.Right, midY),
            })
            {
                var r = new SKRect(c.X - hs, c.Y - hs, c.X + hs, c.Y + hs);
                canvas.DrawRect(r, grip);
                canvas.DrawRect(r, gripEdge);
            }

            using var labelBg = new SKPaint { Color = new SKColor(20, 20, 24, 220) };
            using var labelText = new SKPaint { Color = SKColors.White, TextSize = 12, IsAntialias = true };
            string dimensions = $"{Math.Max((int)Math.Round(Math.Abs(crop.Width)), 1)} × {Math.Max((int)Math.Round(Math.Abs(crop.Height)), 1)} px";
            float labelWidth = labelText.MeasureText(dimensions) + 14;
            float labelY = sel.Top > 28 ? sel.Top - 26 : sel.Top + 8;
            canvas.DrawRoundRect(new SKRect(sel.Left, labelY, sel.Left + labelWidth, labelY + 20), 3, 3, labelBg);
            canvas.DrawText(dimensions, sel.Left + 7, labelY + 14, labelText);
        }

        // Guias da TELA (estilo Paint): contorno provisório CENTRADO na imagem atual —
        // o resultado final sempre centraliza (ApplyCanvasResize), então a prévia já
        // mostra pra onde a arte vai, não importa de que lado você arrastou a alça.
        if (_vm.Tool == EditorTool.Canvas)
        {
            var size = _liveCanvasSize ?? new SKPoint(bmp.Width, bmp.Height);
            float offX = (size.X - bmp.Width) / 2f;
            float offY = (size.Y - bmp.Height) / 2f;
            var o = ToScreen(new SKPoint(-offX, -offY));
            var c2 = ToScreen(new SKPoint(bmp.Width + offX, bmp.Height + offY));
            using var outline = new SKPaint
            {
                Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                PathEffect = SKPathEffect.CreateDash([4f, 4f], 0),
            };
            canvas.DrawRect(new SKRect(o.X, o.Y, c2.X, c2.Y), outline);

            using var handle = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            const float hs = 5;
            float midX = (o.X + c2.X) / 2, midY = (o.Y + c2.Y) / 2;
            foreach (var p in new[]
            {
                new SKPoint(c2.X, midY), new SKPoint(o.X, midY),   // direita, esquerda
                new SKPoint(midX, c2.Y), new SKPoint(midX, o.Y),   // inferior, cima
                new SKPoint(c2.X, c2.Y), new SKPoint(o.X, c2.Y),   // cantos inferiores
                new SKPoint(c2.X, o.Y), new SKPoint(o.X, o.Y),     // cantos superiores
            })
                canvas.DrawRect(new SKRect(p.X - hs, p.Y - hs, p.X + hs, p.Y + hs), handle);
        }

        // Prévia da forma sendo arrastada (vira camada ao soltar).
        if (_liveShape is { } shape)
        {
            using var paint = new SKPaint
            {
                Color = _vm.BrushColor.WithAlpha(180),
                Style = _vm.ShapeFilled && _vm.Shape != ShapeKind.Linha ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                StrokeWidth = (float)(_vm.BrushSize * scale),
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
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

        // Prévia da caixa de texto: o texto já aparece no tamanho que vai entrar.
        if (_liveTextBox is { } tb && tb.Height > 0)
        {
            var a = ToScreen(new SKPoint(tb.Left, tb.Top));
            var b = ToScreen(new SKPoint(tb.Right, tb.Bottom));
            using var dash = new SKPaint
            {
                Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                PathEffect = SKPathEffect.CreateDash([4f, 4f], 0),
            };
            canvas.DrawRect(new SKRect(a.X, a.Y, b.X, b.Y), dash);
            using var text = new SKPaint
            {
                Color = _vm.BrushColor.WithAlpha(200), IsAntialias = true,
                TextSize = (b.Y - a.Y) / EditorLayer.LineHeightPerPoint(),
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
            };
            canvas.DrawText(string.IsNullOrWhiteSpace(_vm.TextInput) ? "Texto" : _vm.TextInput, a.X, a.Y - text.FontMetrics.Ascent, text);
        }

        // Ferramentas de pintura: contorno tracejado da camada que recebe a tinta quando
        // o clique começa dentro dela. Fora desse contorno a tinta vai pro fundo (tela).
        if (_vm.IsPaintingTool && _vm.Tool != EditorTool.Eyedropper && _vm.PaintLayer is { } paintTarget)
        {
            var b = paintTarget.Bounds;
            var p1 = ToScreen(new SKPoint(b.Left, b.Top));
            var p2 = ToScreen(new SKPoint(b.Right, b.Bottom));
            using var dashed = new SKPaint
            {
                Color = new SKColor(0x4D, 0xA3, 0xFF, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
            };
            canvas.DrawRect(new SKRect(p1.X, p1.Y, p2.X, p2.Y), dashed);
        }

        // Guias dos ímãs (só enquanto a camada está grudada em algo).
        if (_snapGuidesX.Count > 0 || _snapGuidesY.Count > 0)
        {
            using var guide = new SKPaint { Color = new SKColor(0xFF, 0x3D, 0xD6), StrokeWidth = 1, IsAntialias = false };
            foreach (float gx in _snapGuidesX)
            {
                var top = ToScreen(new SKPoint(gx, 0));
                canvas.DrawLine(top.X, 0, top.X, e.Info.Height, guide);
            }
            foreach (float gy in _snapGuidesY)
            {
                var left = ToScreen(new SKPoint(0, gy));
                canvas.DrawLine(0, left.Y, e.Info.Width, left.Y, guide);
            }
        }
    }
}
