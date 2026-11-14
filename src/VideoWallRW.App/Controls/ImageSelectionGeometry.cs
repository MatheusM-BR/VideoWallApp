using SkiaSharp;

namespace VideoWallRW.App.Controls;

/// <summary>Geometria do recorte em pixels da imagem, independente do zoom da interface.</summary>
public static class ImageSelectionGeometry
{
    public static SKRect FitSize(SKRect rect, float width, float height, float imageWidth, float imageHeight, double? ratio = null, bool heightEdited = false)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (ratio is > 0)
        {
            if (heightEdited) width = height * (float)ratio;
            else height = width / (float)ratio;
        }
        float factor = Math.Min(1, Math.Min(imageWidth / width, imageHeight / height));
        width *= factor;
        height *= factor;
        float left = Math.Clamp(rect.Left, 0, imageWidth - width);
        float top = Math.Clamp(rect.Top, 0, imageHeight - height);
        return new SKRect(left, top, left + width, top + height);
    }

    public static SKRect FromCorner(SKPoint anchor, SKPoint pointer, float imageWidth, float imageHeight, double? ratio)
    {
        pointer = new SKPoint(Math.Clamp(pointer.X, 0, imageWidth), Math.Clamp(pointer.Y, 0, imageHeight));
        float sx = pointer.X < anchor.X ? -1 : 1, sy = pointer.Y < anchor.Y ? -1 : 1;
        float width = Math.Abs(pointer.X - anchor.X), height = Math.Abs(pointer.Y - anchor.Y);
        if (ratio is > 0)
        {
            float r = (float)ratio;
            // Projeta nos dois eixos: um movimento vertical também redimensiona.
            width = (width * r * r + height * r) / (r * r + 1);
            float maxWidth = sx > 0 ? imageWidth - anchor.X : anchor.X;
            float maxHeight = sy > 0 ? imageHeight - anchor.Y : anchor.Y;
            width = Math.Min(width, Math.Min(maxWidth, maxHeight * r));
            height = width / r;
        }
        float x = anchor.X + sx * width, y = anchor.Y + sy * height;
        return new SKRect(Math.Min(anchor.X, x), Math.Min(anchor.Y, y), Math.Max(anchor.X, x), Math.Max(anchor.Y, y));
    }

    public static SKRect ResizeEdge(SKRect rect, string edge, SKPoint pointer, float imageWidth, float imageHeight, double? ratio)
    {
        bool horizontal = edge is "Left" or "Right";
        float centerX = rect.MidX, centerY = rect.MidY;
        float width = rect.Width, height = rect.Height;
        if (horizontal)
        {
            float maxWidth = edge == "Left" ? rect.Right : imageWidth - rect.Left;
            if (ratio is > 0) maxWidth = Math.Min(maxWidth, 2 * Math.Min(centerY, imageHeight - centerY) * (float)ratio);
            width = Math.Clamp(edge == "Left" ? rect.Right - pointer.X : pointer.X - rect.Left, Math.Min(1, maxWidth), maxWidth);
            if (ratio is > 0) height = width / (float)ratio;
            float left = edge == "Left" ? rect.Right - width : rect.Left;
            float top = ratio is > 0 ? centerY - height / 2 : rect.Top;
            return new SKRect(left, top, left + width, top + height);
        }
        float maxHeight = edge == "Top" ? rect.Bottom : imageHeight - rect.Top;
        if (ratio is > 0) maxHeight = Math.Min(maxHeight, 2 * Math.Min(centerX, imageWidth - centerX) / (float)ratio);
        height = Math.Clamp(edge == "Top" ? rect.Bottom - pointer.Y : pointer.Y - rect.Top, Math.Min(1, maxHeight), maxHeight);
        if (ratio is > 0) width = height * (float)ratio;
        float x = ratio is > 0 ? centerX - width / 2 : rect.Left;
        float y = edge == "Top" ? rect.Bottom - height : rect.Top;
        return new SKRect(x, y, x + width, y + height);
    }
}
