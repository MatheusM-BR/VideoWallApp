using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace VideoWallRW.App.ViewModels;

public enum EditorLayerKind { Image, Text, Shape }

/// <summary>
/// Camada do editor de imagem (o "adesivo" do Paint 3D): fica POR CIMA do fundo, com
/// posição, tamanho e opacidade próprios, e só vira pixel do fundo quando o operador
/// manda mesclar (ou, na cópia, ao salvar).
///
/// Três tipos:
///  - <see cref="EditorLayerKind.Image"/>: pixels (a imagem original aberta no editor
///    também é uma camada destas — dá pra mover/redimensionar sobre o fundo, e os
///    pincéis pintam nela quando ela está selecionada);
///  - <see cref="EditorLayerKind.Text"/> e <see cref="EditorLayerKind.Shape"/>: vetoriais,
///    redesenhados no tamanho atual — redimensionar não borra.
/// </summary>
public sealed partial class EditorLayer : ObservableObject, IDisposable
{
    public EditorLayerKind Kind { get; }

    /// <summary>Pixels da camada (só <see cref="EditorLayerKind.Image"/>; mutável — pincel pinta aqui).</summary>
    public SKBitmap? Bitmap { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _visible = true;

    /// <summary>0..1 — multiplica o alfa na hora de compor.</summary>
    [ObservableProperty] private double _opacity = 1.0;

    /// <summary>Canto superior esquerdo e tamanho exibido, em coordenadas da imagem (tela).</summary>
    [ObservableProperty] private float _x;
    [ObservableProperty] private float _y;
    [ObservableProperty] private float _width;
    [ObservableProperty] private float _height;

    // ── Texto ──
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private float _fontSize = 48;

    // ── Forma ──
    [ObservableProperty] private ShapeKind _shape;
    [ObservableProperty] private bool _filled = true;
    [ObservableProperty] private float _strokeWidth = 8;
    /// <summary>Linha: true = sobe da esquerda-baixo pra direita-cima; false = desce.</summary>
    [ObservableProperty] private bool _lineRising;

    /// <summary>Cor do texto/forma.</summary>
    [ObservableProperty] private SKColor _color = SKColors.Red;

    /// <summary>Giro em graus (horário) em torno do centro, e espelhos — valem pra
    /// qualquer tipo de camada. <see cref="Bounds"/> continua sendo a caixa SEM giro.</summary>
    [ObservableProperty] private float _rotation;
    [ObservableProperty] private bool _flipX;
    [ObservableProperty] private bool _flipY;

    public bool IsTransformed => Rotation != 0 || FlipX || FlipY;

    /// <summary>Ponto da tela → "eixo da camada" (desfaz o giro em torno do centro).
    /// Serve pra pegar alças e saber se o clique caiu dentro da camada girada.</summary>
    public SKPoint ToFrame(SKPoint p)
    {
        if (Rotation == 0) return p;
        var b = Bounds;
        double rad = -Rotation * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
        float dx = p.X - b.MidX, dy = p.Y - b.MidY;
        return new SKPoint(b.MidX + (float)(dx * c - dy * s), b.MidY + (float)(dx * s + dy * c));
    }

    /// <summary>O clique caiu dentro da camada (girada ou não)?</summary>
    public bool Contains(SKPoint p)
    {
        var f = ToFrame(p);
        return Bounds.Contains(f.X, f.Y);
    }

    /// <summary>Contorno alinhado aos eixos da camada girada.</summary>
    public SKRect DisplayBounds => ImageEditorViewModel.RotatedBounds(Bounds, Rotation);

    private EditorLayer(EditorLayerKind kind, SKBitmap? bitmap, string name)
    {
        Kind = kind;
        Bitmap = bitmap;
        _name = name;
    }

    public static EditorLayer FromImage(SKBitmap bitmap, string name, float x, float y, float scale = 1f)
    {
        // Premul BGRA: a borracha precisa conseguir abrir transparência até em JPEG.
        var pixels = bitmap.ColorType == SKColorType.Bgra8888 && bitmap.AlphaType == SKAlphaType.Premul
            ? bitmap
            : bitmap.Copy(SKColorType.Bgra8888) ?? throw new InvalidOperationException("Formato de pixel não suportado.");
        if (!ReferenceEquals(pixels, bitmap)) bitmap.Dispose();
        if (pixels.AlphaType != SKAlphaType.Premul)
        {
            var premul = new SKBitmap(new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var c = new SKCanvas(premul)) c.DrawBitmap(pixels, 0, 0);
            pixels.Dispose();
            pixels = premul;
        }
        return new EditorLayer(EditorLayerKind.Image, pixels, name)
        {
            X = x, Y = y, Width = pixels.Width * scale, Height = pixels.Height * scale,
        };
    }

    public static EditorLayer FromText(string text, float fontSize, SKColor color, float x, float y)
    {
        var layer = new EditorLayer(EditorLayerKind.Text, null, TextLayerName(text))
        {
            Text = text, FontSize = fontSize, Color = color, X = x, Y = y,
        };
        layer.FitTextBox();
        return layer;
    }

    public static EditorLayer FromShape(ShapeKind shape, SKRect rect, bool lineRising, bool filled,
        float strokeWidth, SKColor color) => new(EditorLayerKind.Shape, null, shape.ToString())
    {
        Shape = shape, Filled = filled, StrokeWidth = strokeWidth, Color = color, LineRising = lineRising,
        X = rect.Left, Y = rect.Top, Width = Math.Max(rect.Width, 1), Height = Math.Max(rect.Height, 1),
    };

    private static string TextLayerName(string text)
        => "Texto: " + (text.Length > 24 ? text[..24] + "…" : text);

    public string KindIcon => Kind switch
    {
        EditorLayerKind.Text => "T",
        EditorLayerKind.Shape => "◆",
        _ => "🖼",
    };

    /// <summary>Imagem e texto redimensionam sem deformar; forma é livre.</summary>
    public bool KeepsAspect => Kind != EditorLayerKind.Shape;

    /// <summary>Retângulo que a camada ocupa na imagem.</summary>
    public SKRect Bounds => new(X, Y, X + Width, Y + Height);

    // ── Texto: tamanho natural ──

    private static SKPaint TextPaint(float size, SKColor color) => new()
    {
        Color = color, IsAntialias = true, TextSize = size,
        Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
    };

    /// <summary>Largura/altura do texto no <see cref="FontSize"/> atual, sem escala.</summary>
    public SKSize NaturalTextSize()
    {
        using var paint = TextPaint(FontSize, Color);
        var m = paint.FontMetrics;
        float w = Math.Max(paint.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text), 1);
        return new SKSize(w, Math.Max(m.Descent - m.Ascent, 1));
    }

    /// <summary>Quanto de altura de caixa cada ponto de fonte ocupa (pra caber num retângulo arrastado).</summary>
    public static float LineHeightPerPoint()
    {
        using var paint = TextPaint(100, SKColors.Black);
        var m = paint.FontMetrics;
        return (m.Descent - m.Ascent) / 100f;
    }

    /// <summary>Caixa volta ao tamanho natural do texto (depois de mudar texto/tamanho).</summary>
    public void FitTextBox()
    {
        var size = NaturalTextSize();
        Width = size.Width;
        Height = size.Height;
    }

    partial void OnTextChanged(string value)
    {
        if (Kind == EditorLayerKind.Text) Name = TextLayerName(value);
    }

    public void Draw(SKCanvas canvas)
    {
        if (!Visible || Opacity <= 0 || Width <= 0 || Height <= 0) return;
        if (!IsTransformed) { DrawUntransformed(canvas); return; }
        var b = Bounds;
        canvas.Save();
        canvas.Translate(b.MidX, b.MidY);
        if (Rotation != 0) canvas.RotateDegrees(Rotation);
        canvas.Scale(FlipX ? -1 : 1, FlipY ? -1 : 1);
        canvas.Translate(-b.MidX, -b.MidY);
        DrawUntransformed(canvas);
        canvas.Restore();
    }

    private void DrawUntransformed(SKCanvas canvas)
    {
        byte alpha = (byte)Math.Clamp(Opacity * 255, 0, 255);
        switch (Kind)
        {
            case EditorLayerKind.Image:
            {
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha), FilterQuality = SKFilterQuality.High };
                canvas.DrawBitmap(Bitmap!, Bounds, paint);
                break;
            }
            case EditorLayerKind.Text:
            {
                using var paint = TextPaint(FontSize, Color.WithAlpha((byte)(Color.Alpha * alpha / 255)));
                var natural = NaturalTextSize();
                canvas.Save();
                canvas.Translate(X, Y);
                canvas.Scale(Width / natural.Width, Height / natural.Height);
                canvas.DrawText(Text, 0, -paint.FontMetrics.Ascent, paint);
                canvas.Restore();
                break;
            }
            case EditorLayerKind.Shape:
            {
                bool fill = Filled && Shape != ShapeKind.Linha;
                using var paint = new SKPaint
                {
                    Color = Color.WithAlpha((byte)(Color.Alpha * alpha / 255)),
                    IsAntialias = true,
                    Style = fill ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                    StrokeWidth = StrokeWidth,
                    StrokeCap = SKStrokeCap.Round,
                };
                // Contorno por DENTRO do retângulo: a borda não vaza pra fora da caixa.
                var r = fill ? Bounds : SKRect.Inflate(Bounds, -StrokeWidth / 2, -StrokeWidth / 2);
                switch (Shape)
                {
                    case ShapeKind.Retângulo: canvas.DrawRect(r, paint); break;
                    case ShapeKind.Elipse: canvas.DrawOval(r, paint); break;
                    case ShapeKind.Linha:
                        if (LineRising) canvas.DrawLine(Bounds.Left, Bounds.Bottom, Bounds.Right, Bounds.Top, paint);
                        else canvas.DrawLine(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom, paint);
                        break;
                }
                break;
            }
        }
    }

    /// <summary>Ponto da imagem → pixel do bitmap desta camada (só imagem).</summary>
    public SKPoint ToLocal(SKPoint imagePoint)
    {
        if (Bitmap is null || Width <= 0 || Height <= 0) return imagePoint;
        // Desfaz giro e espelho: o pincel cai no pixel que aparece sob o ponteiro.
        var p = ToFrame(imagePoint);
        var b = Bounds;
        if (FlipX) p.X = 2 * b.MidX - p.X;
        if (FlipY) p.Y = 2 * b.MidY - p.Y;
        return new SKPoint((p.X - X) * Bitmap.Width / Width, (p.Y - Y) * Bitmap.Height / Height);
    }

    /// <summary>Quantos pixels do bitmap desta camada cabem em 1 pixel da imagem.</summary>
    public float LocalScale => Bitmap is null || Width <= 0 ? 1f : Bitmap.Width / Width;

    /// <summary>
    /// Redimensiona a partir de um canto mantendo o canto OPOSTO cravado no lugar
    /// (puxar a alça faz a camada crescer NA DIREÇÃO do cursor, como em qualquer editor).
    /// Imagem/texto: escala uniforme — projeta o cursor na diagonal original, então nunca
    /// deforma. Forma (ou <paramref name="free"/>): largura e altura seguem o cursor.
    /// </summary>
    public void ResizeFromCorner(LayerCorner grabbed, SKRect origin, SKPoint cursor, bool free = false)
    {
        bool anchorIsLeft = grabbed is LayerCorner.TopRight or LayerCorner.BottomRight;
        bool anchorIsTop = grabbed is LayerCorner.BottomLeft or LayerCorner.BottomRight;
        float ax = anchorIsLeft ? origin.Left : origin.Right;
        float ay = anchorIsTop ? origin.Top : origin.Bottom;

        float nw, nh;
        if (free || !KeepsAspect)
        {
            nw = Math.Max(anchorIsLeft ? cursor.X - ax : ax - cursor.X, MinSize);
            nh = Math.Max(anchorIsTop ? cursor.Y - ay : ay - cursor.Y, MinSize);
        }
        else
        {
            float dx = anchorIsLeft ? origin.Width : -origin.Width;
            float dy = anchorIsTop ? origin.Height : -origin.Height;
            float denom = dx * dx + dy * dy;
            if (denom <= float.Epsilon) return;
            float t = ((cursor.X - ax) * dx + (cursor.Y - ay) * dy) / denom;
            float minT = MinSize / Math.Max(Math.Min(origin.Width, origin.Height), 1);
            t = Math.Clamp(t, minT, MaxScale);
            nw = origin.Width * t;
            nh = origin.Height * t;
        }

        Width = nw;
        Height = nh;
        X = anchorIsLeft ? ax : ax - nw;
        Y = anchorIsTop ? ay : ay - nh;
    }

    public const float MinSize = 4f;
    public const float MaxScale = 20f;

    public EditorLayer Clone() => new(Kind, Bitmap?.Copy(), Name)
    {
        X = X, Y = Y, Width = Width, Height = Height,
        Opacity = Opacity, Visible = Visible,
        Text = Text, FontSize = FontSize, Color = Color,
        Shape = Shape, Filled = Filled, StrokeWidth = StrokeWidth, LineRising = LineRising,
        Rotation = Rotation, FlipX = FlipX, FlipY = FlipY,
    };

    public void Dispose() => Bitmap?.Dispose();
}

/// <summary>Canto da camada agarrado pra redimensionar (ver <see cref="EditorLayer.ResizeFromCorner"/>).</summary>
public enum LayerCorner { TopLeft, TopRight, BottomLeft, BottomRight }
