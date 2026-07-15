using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VideoWallRW.App.Controls;

/// <summary>
/// Slide "estilo volante": o cabo sempre começa centralizado. Arrastar pra direita/
/// esquerda aplica uma taxa contínua (Command executado a cada tick de um timer interno,
/// com o deslocamento -1..1 como parâmetro) enquanto o botão do mouse estiver pressionado
/// — quanto mais longe do centro, mais rápido o efeito. Ao soltar, o cabo anima de volta
/// pro centro sozinho (não fica "travado" numa posição, diferente de um Slider normal).
/// </summary>
public sealed class CenterSnapSlider : FrameworkElement
{
    private const double HandleWidth = 12;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(40);

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CenterSnapSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Executado a cada tick (~25/s) enquanto arrastado, com o deslocamento
    /// atual (-1..1) como parâmetro — 0 nunca é enviado (perto do centro não faz nada).</summary>
    public static readonly DependencyProperty JogCommandProperty = DependencyProperty.Register(
        nameof(JogCommand), typeof(System.Windows.Input.ICommand), typeof(CenterSnapSlider));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public System.Windows.Input.ICommand? JogCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(JogCommandProperty);
        set => SetValue(JogCommandProperty, value);
    }

    private bool _dragging;
    private readonly DispatcherTimer _timer;

    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x17, 0x17, 0x1A));
    private static readonly Brush CenterTickBrush = Frozen(Color.FromRgb(0x3C, 0x3C, 0x44));
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0x9A, 0xCD, 0x32));
    private static readonly Pen BorderPen = FrozenPen(Color.FromRgb(0x3C, 0x3C, 0x44));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c) { var p = new Pen(Frozen(c), 1); p.Freeze(); return p; }

    public CenterSnapSlider()
    {
        Height = 18;
        Cursor = Cursors.SizeWE;
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) =>
        {
            if (Math.Abs(Value) > 0.03)
                JogCommand?.Execute(Value);
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(TrackBrush, BorderPen, full);

        double centerX = ActualWidth / 2;
        dc.DrawLine(new Pen(CenterTickBrush, 2), new Point(centerX, 2), new Point(centerX, ActualHeight - 2));

        double halfRange = Math.Max(ActualWidth / 2 - HandleWidth / 2, 1);
        double handleX = centerX + Value * halfRange;
        dc.DrawRoundedRectangle(HandleBrush, null,
            new Rect(handleX - HandleWidth / 2, 1, HandleWidth, ActualHeight - 2), 2, 2);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // A animação de "voltar pro centro" do arraste anterior deixa a propriedade
        // Value presa (WPF: FillBehavior.HoldEnd continua "dona" da propriedade depois
        // que a animação termina, e um SetValue direto é ignorado silenciosamente até
        // limpar a animação) — sem isso, só o primeiro arraste funcionava.
        BeginAnimation(ValueProperty, null);

        _dragging = true;
        CaptureMouse();
        _timer.Start();
        UpdateValueFromMouse(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        UpdateValueFromMouse(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _timer.Stop();
        ReleaseMouseCapture();
        SnapBackToCenter();
        e.Handled = true;
    }

    private void UpdateValueFromMouse(double x)
    {
        double centerX = ActualWidth / 2;
        double halfRange = Math.Max(ActualWidth / 2 - HandleWidth / 2, 1);
        Value = Math.Clamp((x - centerX) / halfRange, -1, 1);
    }

    private void SnapBackToCenter()
    {
        var anim = new DoubleAnimation(Value, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut },
        };
        // Solta a propriedade assim que a animação termina em vez de deixar o clock
        // "dono" dela pra sempre — sem isso, arrastar de novo simplesmente não move
        // mais o cabo (o WPF ignora SetValue enquanto uma animação ainda segura o valor).
        anim.Completed += (_, _) =>
        {
            BeginAnimation(ValueProperty, null);
            Value = 0;
        };
        BeginAnimation(ValueProperty, anim);
    }
}
