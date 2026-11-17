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

    /// <summary>
    /// Igual a <see cref="Transform"/>, mas com a área girada <paramref name="angle"/> graus
    /// em torno do centro: o deslocamento do mouse é levado pro "eixo da área" (assim a
    /// alça lateral estica na direção em que a área está virada) e o lado oposto à alça
    /// fica parado na tela. Devolve o retângulo SEM giro (centro + tamanho).
    /// </summary>
    public static SKRect TransformRotated(SKRect origin, string handle, float dx, float dy, bool keepRatio, float angle)
    {
        if (angle == 0 || handle == "Move") return Transform(origin, handle, dx, dy, keepRatio);
        var local = Rotate(new SKPoint(dx, dy), -angle);
        var n = Transform(origin, handle, local.X, local.Y, keepRatio);
        // Quanto o centro andou, no eixo da área → de volta pra tela.
        var shift = Rotate(new SKPoint(n.MidX - origin.MidX, n.MidY - origin.MidY), angle);
        float cx = origin.MidX + shift.X, cy = origin.MidY + shift.Y;
        return SKRect.Create(cx - n.Width / 2, cy - n.Height / 2, n.Width, n.Height);
    }

    /// <summary>Ângulo (graus, horário, 0 = alça pra cima) do ponto em relação ao centro.
    /// Com <paramref name="snap"/>, arredonda pra múltiplos de 15°.</summary>
    public static float AngleFromCenter(SKPoint center, SKPoint p, bool snap)
    {
        float deg = (float)(Math.Atan2(p.Y - center.Y, p.X - center.X) * 180 / Math.PI) + 90;
        if (snap) deg = MathF.Round(deg / 15f) * 15f;
        deg %= 360; if (deg > 180) deg -= 360; if (deg <= -180) deg += 360;
        return deg;
    }

    /// <summary>Gira um vetor (graus, sentido horário na tela).</summary>
    public static SKPoint Rotate(SKPoint v, float degrees)
    {
        double rad = degrees * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
        return new SKPoint((float)(v.X * c - v.Y * s), (float)(v.X * s + v.Y * c));
    }
}
