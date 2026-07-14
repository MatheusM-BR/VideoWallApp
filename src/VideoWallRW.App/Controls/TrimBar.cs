using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoWallRW.App.Controls;

/// <summary>
/// Barra de trim estilo Resolume (Fase Resolume-UX): mostra a duração inteira do
/// vídeo e duas alças arrastáveis marcando In/Out. O arraste é visual em tempo real,
/// mas o valor só é COMMITADO no soltar do mouse — evita spam de UpdateClip pro
/// engine a cada pixel.
/// </summary>
public sealed class TrimBar : FrameworkElement
{
    private const double HandleWidth = 9;

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InPointProperty = DependencyProperty.Register(
        nameof(InPoint), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutPointProperty = DependencyProperty.Register(
        nameof(OutPoint), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Instante (s) sob a alça enquanto o operador arrasta; -1 quando parado.
    /// A UI usa pra mostrar o frame exato que está sendo definido.</summary>
    public static readonly DependencyProperty ScrubSecondsProperty = DependencyProperty.Register(
        nameof(ScrubSeconds), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double InPoint { get => (double)GetValue(InPointProperty); set => SetValue(InPointProperty, value); }
    public double OutPoint { get => (double)GetValue(OutPointProperty); set => SetValue(OutPointProperty, value); }
    public double ScrubSeconds { get => (double)GetValue(ScrubSecondsProperty); set => SetValue(ScrubSecondsProperty, value); }

    private enum DragTarget { None, In, Out }
    private DragTarget _dragging = DragTarget.None;
    private double _dragValue; // valor provisório durante o arraste (segundos)

    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1A)));
    private static readonly Brush RangeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x5E, 0x28)));
    private static readonly Brush HandleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xCD, 0x32)));
    private static readonly Pen BorderPen = FrozenPen(Color.FromRgb(0x3C, 0x3C, 0x44));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xDF, 0xDF, 0xDF)));

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Color c) { var p = new Pen(new SolidColorBrush(c), 1); p.Freeze(); return p; }

    public TrimBar()
    {
        MinHeight = 26;
        Cursor = Cursors.Hand;
    }

    private double EffectiveIn => _dragging == DragTarget.In ? _dragValue : InPoint;
    private double EffectiveOut => _dragging == DragTarget.Out ? _dragValue : OutPoint;

    private double ToX(double seconds) =>
        Duration <= 0 ? 0 : System.Math.Clamp(seconds / Duration, 0, 1) * ActualWidth;

    private double ToSeconds(double x) =>
        Duration <= 0 ? 0 : System.Math.Clamp(x / System.Math.Max(ActualWidth, 1), 0, 1) * Duration;

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(TrackBrush, BorderPen, full);
        if (Duration <= 0)
        {
            DrawLabel(dc, "sem duração", ActualWidth / 2 - 30, muted: true);
            return;
        }

        double inX = ToX(EffectiveIn);
        double outX = ToX(EffectiveOut <= 0 ? Duration : EffectiveOut);
        if (outX < inX) (inX, outX) = (outX, inX);

        dc.DrawRectangle(RangeBrush, null, new Rect(inX, 1, System.Math.Max(outX - inX, 0), ActualHeight - 2));
        dc.DrawRectangle(HandleBrush, null, new Rect(inX - HandleWidth / 2, 0, HandleWidth, ActualHeight));
        dc.DrawRectangle(HandleBrush, null, new Rect(outX - HandleWidth / 2, 0, HandleWidth, ActualHeight));

        DrawLabel(dc, Format(EffectiveIn), System.Math.Min(inX + 8, ActualWidth - 80), muted: false);
        DrawLabel(dc, Format(EffectiveOut <= 0 ? Duration : EffectiveOut), System.Math.Max(outX - 46, 4), muted: false);
    }

    private void DrawLabel(DrawingContext dc, string text, double x, bool muted)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, muted ? Brushes.Gray : TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(x, (ActualHeight - ft.Height) / 2));
    }

    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(System.Math.Max(seconds, 0)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        double x = e.GetPosition(this).X;
        double inX = ToX(InPoint);
        double outX = ToX(OutPoint <= 0 ? Duration : OutPoint);

        // Pega a alça mais próxima do clique (dentro de qualquer distância — clicar no
        // meio move a alça mais próxima, igual ao trim do Resolume).
        _dragging = System.Math.Abs(x - inX) <= System.Math.Abs(x - outX) ? DragTarget.In : DragTarget.Out;
        _dragValue = ToSeconds(x);
        ScrubSeconds = _dragValue;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging == DragTarget.None) return;
        _dragValue = ToSeconds(e.GetPosition(this).X);

        // Não deixa as alças se cruzarem (margem mínima de 0.05s).
        double outEff = OutPoint <= 0 ? Duration : OutPoint;
        if (_dragging == DragTarget.In) _dragValue = System.Math.Min(_dragValue, outEff - 0.05);
        else _dragValue = System.Math.Max(_dragValue, InPoint + 0.05);

        ScrubSeconds = _dragValue;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging == DragTarget.None) return;
        var target = _dragging;
        _dragging = DragTarget.None;
        ReleaseMouseCapture();

        // Commit só aqui (uma escrita no binding, um UpdateClip no engine).
        if (target == DragTarget.In) InPoint = System.Math.Max(_dragValue, 0);
        else OutPoint = System.Math.Min(_dragValue, Duration);
        ScrubSeconds = -1; // fim do arraste — o preview volta pro thumbnail normal
        InvalidateVisual();
        e.Handled = true;
    }
}
