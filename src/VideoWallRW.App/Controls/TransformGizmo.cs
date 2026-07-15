using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoWallRW.App.Controls;

/// <summary>
/// Painel CLIP interativo: representa a composição como um retângulo e o clip
/// selecionado como um retângulo móvel/redimensionável dentro dela — arrastar por
/// dentro move (PositionX/Y), arrastar a alça do canto (a "agulha") redimensiona nos
/// dois eixos juntos, e as alças de aresta redimensionam só horizontal ou só vertical.
/// Aproximação visual: assume que o retângulo "base" (escala 1, posição 0) preenche a
/// composição inteira — o encaixe real (Fit/Fill/letterbox) é calculado pelo engine;
/// aqui o operador só vê o deslocamento/escala que ele está aplicando por cima disso.
/// </summary>
public sealed class TransformGizmo : FrameworkElement
{
    private const double HandleSize = 10;

    public static readonly DependencyProperty PositionXProperty = Register(nameof(PositionX));
    public static readonly DependencyProperty PositionYProperty = Register(nameof(PositionY));
    public static readonly DependencyProperty ScaleXProperty = Register(nameof(ScaleX), 1.0);
    public static readonly DependencyProperty ScaleYProperty = Register(nameof(ScaleY), 1.0);

    private static DependencyProperty Register(string name, double defaultValue = 0.0) =>
        DependencyProperty.Register(name, typeof(double), typeof(TransformGizmo),
            new FrameworkPropertyMetadata(defaultValue,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public double PositionX { get => (double)GetValue(PositionXProperty); set => SetValue(PositionXProperty, value); }
    public double PositionY { get => (double)GetValue(PositionYProperty); set => SetValue(PositionYProperty, value); }
    public double ScaleX { get => (double)GetValue(ScaleXProperty); set => SetValue(ScaleXProperty, value); }
    public double ScaleY { get => (double)GetValue(ScaleYProperty); set => SetValue(ScaleYProperty, value); }

    private enum DragMode { None, Move, ScaleCorner, ScaleH, ScaleV }
    private DragMode _mode;
    private Point _dragStartMouse;
    private double _dragStartPosX, _dragStartPosY, _dragStartScaleX, _dragStartScaleY;

    private static readonly Brush OuterBrush = Frozen(Color.FromRgb(0x17, 0x17, 0x1A));
    private static readonly Brush InnerBrush = Frozen(Color.FromArgb(0x60, 0x9A, 0xCD, 0x32));
    private static readonly Pen OuterPen = FrozenPen(Color.FromRgb(0x3C, 0x3C, 0x44), 1);
    private static readonly Pen InnerPen = FrozenPen(Color.FromRgb(0x9A, 0xCD, 0x32), 2);
    private static readonly Pen CenterCrossPen = FrozenPen(Color.FromRgb(0x8C, 0x8C, 0x9A), 1);
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0x9A, 0xCD, 0x32));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double thickness) { var p = new Pen(Frozen(c), thickness); p.Freeze(); return p; }

    public TransformGizmo()
    {
        MinHeight = 140;
        ClipToBounds = true;
    }

    /// <summary>Área útil desenhável (deixa uma margem pra alça não vazar do controle).</summary>
    private Rect Stage => new(HandleSize, HandleSize,
        Math.Max(ActualWidth - HandleSize * 2, 1), Math.Max(ActualHeight - HandleSize * 2, 1));

    private Rect InnerRect(Rect stage)
    {
        double w = stage.Width * ScaleX;
        double h = stage.Height * ScaleY;
        double cx = stage.X + stage.Width / 2 + PositionX * stage.Width;
        double cy = stage.Y + stage.Height / 2 + PositionY * stage.Height;
        return new Rect(cx - w / 2, cy - h / 2, w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var stage = Stage;
        dc.DrawRectangle(OuterBrush, OuterPen, stage);

        var inner = InnerRect(stage);
        dc.DrawRectangle(InnerBrush, InnerPen, inner);

        // Cruz no centro do clip — referência visual de onde está o "meio" da mídia.
        var center = new Point(inner.X + inner.Width / 2, inner.Y + inner.Height / 2);
        dc.DrawLine(CenterCrossPen, new Point(center.X - 6, center.Y), new Point(center.X + 6, center.Y));
        dc.DrawLine(CenterCrossPen, new Point(center.X, center.Y - 6), new Point(center.X, center.Y + 6));

        // Alça de canto (agulha — escala nos dois eixos juntos).
        DrawHandle(dc, new Point(inner.Right, inner.Bottom));
        // Alças de aresta (só horizontal / só vertical).
        DrawHandle(dc, new Point(inner.Right, center.Y));
        DrawHandle(dc, new Point(center.X, inner.Bottom));
    }

    private static void DrawHandle(DrawingContext dc, Point p)
    {
        dc.DrawRectangle(HandleBrush, null, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var stage = Stage;
        var inner = InnerRect(stage);
        var center = new Point(inner.X + inner.Width / 2, inner.Y + inner.Height / 2);
        var p = e.GetPosition(this);

        double tol = HandleSize;
        if (Near(p, new Point(inner.Right, inner.Bottom), tol)) _mode = DragMode.ScaleCorner;
        else if (Near(p, new Point(inner.Right, center.Y), tol)) _mode = DragMode.ScaleH;
        else if (Near(p, new Point(center.X, inner.Bottom), tol)) _mode = DragMode.ScaleV;
        else if (inner.Contains(p)) _mode = DragMode.Move;
        else _mode = DragMode.None;

        if (_mode == DragMode.None) return;
        _dragStartMouse = p;
        _dragStartPosX = PositionX; _dragStartPosY = PositionY;
        _dragStartScaleX = ScaleX; _dragStartScaleY = ScaleY;
        CaptureMouse();
        e.Handled = true;
    }

    private static bool Near(Point a, Point b, double tol) => Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mode == DragMode.None) return;
        var stage = Stage;
        var p = e.GetPosition(this);
        double dx = (p.X - _dragStartMouse.X) / Math.Max(stage.Width, 1);
        double dy = (p.Y - _dragStartMouse.Y) / Math.Max(stage.Height, 1);

        switch (_mode)
        {
            case DragMode.Move:
                PositionX = _dragStartPosX + dx;
                PositionY = _dragStartPosY + dy;
                break;
            case DragMode.ScaleCorner:
                ScaleX = Math.Max(0.05, _dragStartScaleX + dx * 2);
                ScaleY = Math.Max(0.05, _dragStartScaleY + dy * 2);
                break;
            case DragMode.ScaleH:
                ScaleX = Math.Max(0.05, _dragStartScaleX + dx * 2);
                break;
            case DragMode.ScaleV:
                ScaleY = Math.Max(0.05, _dragStartScaleY + dy * 2);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_mode == DragMode.None) return;
        _mode = DragMode.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
