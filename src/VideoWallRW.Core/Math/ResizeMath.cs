namespace VideoWallRW.Core.Math;

public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

public static class ResizeMath
{
    /// <summary>
    /// Calcula o retângulo destino onde o recurso será renderizado numa composição (visor)
    /// com base na proporção, modo de escala e dimensões especificadas.
    /// Retorna um RectF com escala unificada.
    /// </summary>
    public static RectF Compute(Model.ResizeMode mode, float srcW, float srcH, float compW, float compH)
    {
        if (srcW <= 0 || srcH <= 0 || compW <= 0 || compH <= 0)
            return new RectF(0, 0, 0, 0);

        float srcAspect = srcW / srcH;
        float compAspect = compW / compH;

        float width, height, x, y;

        switch (mode)
        {
            case Model.ResizeMode.Stretch:
                return new RectF(0, 0, compW, compH);

            case Model.ResizeMode.Original:
                width = srcW;
                height = srcH;
                x = (compW - width) / 2f;
                y = (compH - height) / 2f;
                return new RectF(x, y, width, height);

            case Model.ResizeMode.Fit:
                if (srcAspect > compAspect)
                {
                    // Letterbox (barras em cima/baixo)
                    width = compW;
                    height = width / srcAspect;
                }
                else
                {
                    // Pillarbox (barras laterais)
                    height = compH;
                    width = height * srcAspect;
                }
                x = (compW - width) / 2f;
                y = (compH - height) / 2f;
                return new RectF(x, y, width, height);

            case Model.ResizeMode.Fill:
                if (srcAspect > compAspect)
                {
                    // Zoom cortando laterais
                    height = compH;
                    width = height * srcAspect;
                }
                else
                {
                    // Zoom cortando topo/baixo
                    width = compW;
                    height = width / srcAspect;
                }
                x = (compW - width) / 2f;
                y = (compH - height) / 2f;
                return new RectF(x, y, width, height);
                
            default:
                throw new System.ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }
}
