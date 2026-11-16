using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

/// <summary>Conta pura do arraste das alças da Seleção (testável sem tela).</summary>
public static class SelectionGeometry
{
    /// <summary>
    /// Novo retângulo a partir do <paramref name="origin"/> do começo do arraste e do
    /// deslocamento do mouse. <paramref name="handle"/>: Move, N, S, E, W, NE, NW, SE, SW.
    /// Cantos esticam os dois eixos (com <paramref name="keepRatio"/>, na proporção do
    /// começo); laterais esticam um eixo só. Passar da borda oposta inverte o lado
    /// (normalizado), sem espelhar a imagem.
    /// </summary>
    public static SKRect Transform(SKRect origin, string handle, float dx, float dy, bool keepRatio)
    {
        float l = origin.Left, t = origin.Top, r = origin.Right, b = origin.Bottom;
        if (handle == "Move") return new SKRect(l + dx, t + dy, r + dx, b + dy);
        if (handle.Contains('W')) l += dx;
        if (handle.Contains('E')) r += dx;
        if (handle.Contains('N')) t += dy;
        if (handle.Contains('S')) b += dy;

        bool corner = handle.Length == 2;
        if (keepRatio && corner && origin.Width > 0 && origin.Height > 0)
        {
            float ratio = origin.Width / origin.Height;
            float w = Math.Abs(r - l), h = Math.Abs(b - t);
            // Vale o eixo que o mouse mais puxou; o outro acompanha na proporção.
            if (w / ratio >= h) h = w / ratio; else w = h * ratio;
            if (handle.Contains('W')) l = r - w * Math.Sign(r - l == 0 ? 1 : r - l); else r = l + w * Math.Sign(r - l == 0 ? 1 : r - l);
            if (handle.Contains('N')) t = b - h * Math.Sign(b - t == 0 ? 1 : b - t); else b = t + h * Math.Sign(b - t == 0 ? 1 : b - t);
        }
        return new SKRect(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
    }
}
