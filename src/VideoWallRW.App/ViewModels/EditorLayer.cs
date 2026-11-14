using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

/// <summary>
/// Camada sobreposta do editor de imagem (o "adesivo" do Paint 3D): uma imagem que
/// fica POR CIMA da arte base, com posição, escala e opacidade próprias, e que só vira
/// pixel da base quando o operador manda mesclar (ou ao salvar).
///
/// É isso que dá a dinâmica pedida: montar banner = arte de fundo + logo + selo, cada
/// um reposicionável à vontade, sem "queimar" nada até estar do jeito certo. As
/// ferramentas de pintura continuam agindo na base — camada é composição, não desenho.
/// </summary>
public sealed partial class EditorLayer : ObservableObject, IDisposable
{
    /// <summary>Pixels da camada (tamanho original do arquivo importado).</summary>
    public SKBitmap Bitmap { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _visible = true;

    /// <summary>0..1 — multiplica o alfa na hora de compor.</summary>
    [ObservableProperty] private double _opacity = 1.0;

    /// <summary>Canto superior esquerdo, em coordenadas da imagem BASE.</summary>
    [ObservableProperty] private float _x;
    [ObservableProperty] private float _y;

    /// <summary>Escala uniforme (1 = tamanho original do arquivo).</summary>
    [ObservableProperty] private float _scale = 1f;

    public EditorLayer(SKBitmap bitmap, string name, float x, float y)
    {
        Bitmap = bitmap;
        _name = name;
        _x = x;
        _y = y;
    }

    /// <summary>Retângulo que a camada ocupa na imagem base, já com a escala.</summary>
    public SKRect Bounds => new(X, Y, X + Bitmap.Width * Scale, Y + Bitmap.Height * Scale);

    /// <summary>Desenha esta camada num canvas que já está no espaço da imagem base.</summary>
    public void Draw(SKCanvas canvas)
    {
        if (!Visible || Opacity <= 0) return;
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(Opacity * 255, 0, 255)),
            FilterQuality = SKFilterQuality.High,
        };
        canvas.DrawBitmap(Bitmap, Bounds, paint);
    }

    /// <summary>
    /// Redimensiona a partir de um canto mantendo o canto OPOSTO cravado no lugar —
    /// é assim que o Paint 3D (e qualquer editor) se comporta: você puxa a alça e a
    /// imagem cresce NA DIREÇÃO do cursor.
    ///
    /// Antes os quatro cantos faziam a mesma conta (delta = dx + dy, âncora sempre no
    /// canto superior esquerdo). O resultado: puxar o canto superior esquerdo pra fora
    /// ENCOLHIA a camada, e os dois cantos mistos (superior direito / inferior esquerdo)
    /// quase não respondiam, porque dx e dy se cancelavam. As quatro alças apareciam
    /// desenhadas, mas só a inferior direita funcionava de verdade.
    ///
    /// A escala é uniforme por construção: projeta o cursor na diagonal original, então
    /// o logo/selo nunca deforma, não importa a direção do arrasto.
    /// </summary>
    /// <param name="grabbed">Canto que o operador pegou.</param>
    /// <param name="originPos">X/Y da camada quando o arrasto começou.</param>
    /// <param name="originScale">Escala da camada quando o arrasto começou.</param>
    /// <param name="cursor">Posição atual do cursor, em coordenadas da imagem base.</param>
    public void ResizeFromCorner(LayerCorner grabbed, SKPoint originPos, float originScale, SKPoint cursor)
    {
        float w = Bitmap.Width * originScale;
        float h = Bitmap.Height * originScale;

        // Âncora = canto OPOSTO ao agarrado; fica parado o arrasto inteiro.
        bool anchorIsLeft = grabbed is LayerCorner.TopRight or LayerCorner.BottomRight;
        bool anchorIsTop = grabbed is LayerCorner.BottomLeft or LayerCorner.BottomRight;
        float ax = anchorIsLeft ? originPos.X : originPos.X + w;
        float ay = anchorIsTop ? originPos.Y : originPos.Y + h;

        // Diagonal original (âncora → canto agarrado) e vetor atual (âncora → cursor).
        float dx = (anchorIsLeft ? w : -w);
        float dy = (anchorIsTop ? h : -h);
        float denom = dx * dx + dy * dy;
        if (denom <= float.Epsilon) return;

        float t = ((cursor.X - ax) * dx + (cursor.Y - ay) * dy) / denom;
        float newScale = Math.Clamp(originScale * t, MinScale, MaxScale);

        Scale = newScale;
        float nw = Bitmap.Width * newScale;
        float nh = Bitmap.Height * newScale;
        X = anchorIsLeft ? ax : ax - nw;
        Y = anchorIsTop ? ay : ay - nh;
    }

    public const float MinScale = 0.02f;
    public const float MaxScale = 20f;

    public EditorLayer Clone() => new(Bitmap.Copy(), Name, X, Y)
    {
        Scale = Scale,
        Opacity = Opacity,
        Visible = Visible,
    };

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>Canto da camada agarrado pra redimensionar (ver <see cref="EditorLayer.ResizeFromCorner"/>).</summary>
public enum LayerCorner { TopLeft, TopRight, BottomLeft, BottomRight }
