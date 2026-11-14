using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoWallRW.App.Controls;

/// <summary>
/// Gera uma imagem de teste (barras de cor + grade de terços + mira central) usada
/// como referência visual no Advanced Output quando não há captura ao vivo da
/// composição — ajuda o operador a enquadrar/conferir proporção e cor de cada tela
/// sem precisar ligar o Composition Monitor (que custa leitura de GPU por frame).
/// </summary>
public static class TestPatternGenerator
{
    private static readonly Color[] Bars =
    [
        Color.FromRgb(0xC0, 0xC0, 0xC0), Color.FromRgb(0xC0, 0xC0, 0x00),
        Color.FromRgb(0x00, 0xC0, 0xC0), Color.FromRgb(0x00, 0xC0, 0x00),
        Color.FromRgb(0xC0, 0x00, 0xC0), Color.FromRgb(0xC0, 0x00, 0x00),
        Color.FromRgb(0x00, 0x00, 0xC0),
    ];

    public static BitmapSource Create(int width, int height)
    {
        width = System.Math.Max(width, 16);
        height = System.Math.Max(height, 16);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Barras de cor cobrindo 70% da altura (estilo SMPTE simplificado).
            double barW = width / (double)Bars.Length;
            double barH = height * 0.7;
            for (int i = 0; i < Bars.Length; i++)
            {
                var brush = new SolidColorBrush(Bars[i]);
                dc.DrawRectangle(brush, null, new Rect(i * barW, 0, barW + 1, barH));
            }

            // Faixa inferior escura, pra mira/legendas não brigarem com as barras.
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), null,
                new Rect(0, barH, width, height - barH));

            // Grade de terços (regra dos terços) em toda a imagem.
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1);
            for (int i = 1; i <= 2; i++)
            {
                double x = width * i / 3.0;
                double y = height * i / 3.0;
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, height));
                dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
            }

            // Mira central.
            var crossPen = new Pen(Brushes.White, 2);
            double cx = width / 2.0, cy = height / 2.0;
            double armX = width * 0.04, armY = height * 0.04;
            dc.DrawLine(crossPen, new Point(cx - armX, cy), new Point(cx + armX, cy));
            dc.DrawLine(crossPen, new Point(cx, cy - armY), new Point(cx, cy + armY));

            // Borda + rótulo de resolução (ajuda a confirmar orientação/corte na hora).
            dc.DrawRectangle(null, new Pen(Brushes.White, 2), new Rect(1, 1, width - 2, height - 2));
            var text = new FormattedText($"{width}×{height}", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), System.Math.Max(height * 0.08, 12),
                Brushes.White, 96);
            dc.DrawText(text, new Point(width * 0.02, height - barH < 0 ? 4 : barH + (height - barH - text.Height) / 2));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
