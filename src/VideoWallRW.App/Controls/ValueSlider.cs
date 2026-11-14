using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace VideoWallRW.App.Controls;

/// <summary>
/// Barra de valor no estilo do Resolume Arena — o controle padrão de parâmetro dele.
/// Diferente de um Slider comum (que tem alça e PULA pro ponto clicado), aqui:
///  · arrastar em QUALQUER ponto muda o valor de forma RELATIVA (nada salta ao clicar);
///  · o valor fica escrito dentro da própria barra, sempre visível;
///  · duplo-clique volta ao padrão (0 em posição, 1 em escala);
///  · segurar SHIFT deixa o arraste ~8x mais fino, pra acertar valor ao vivo;
///  · botão direito abre a caixa pra digitar o número exato.
/// Parâmetros bipolares (mínimo negativo, ex.: posição -1..1) preenchem a partir do
/// CENTRO, igual ao Resolume — dá pra ver de relance pra que lado está deslocado.
/// </summary>
public sealed class ValueSlider : FrameworkElement
{
    private const double FineFactor = 0.125;   // com Shift
    private const double TextPad = 6;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ValueSlider),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            OnValueChanged));

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var slider = (ValueSlider)sender;
        if (UIElementAutomationPeer.FromElement(slider) is { } peer)
            peer.RaisePropertyChangedEvent(System.Windows.Automation.RangeValuePatternIdentifiers.ValueProperty,
                (double)e.OldValue * slider.DisplayMultiplier, (double)e.NewValue * slider.DisplayMultiplier);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ValueSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ValueSlider),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Valor do duplo-clique (padrão de fábrica do parâmetro).</summary>
    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(ValueSlider), new PropertyMetadata(0.0));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ValueSlider),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Formato do número (ex.: "F2"). Ignorado se DisplayText estiver definido.</summary>
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(ValueSlider),
        new FrameworkPropertyMetadata("F2", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Texto alternativo pra mostrar no lugar do número — usado quando o que o
    /// operador entende não é o valor cru (ex.: arrasta ESCALA, mas lê "1920px").</summary>
    public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.Register(
        nameof(DisplayText), typeof(string), typeof(ValueSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Converte a unidade interna para a unidade exibida e digitada.</summary>
    public static readonly DependencyProperty DisplayMultiplierProperty = DependencyProperty.Register(
        nameof(DisplayMultiplier), typeof(double), typeof(ValueSlider),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double factor && double.IsFinite(factor) && factor > 0);

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(ValueSlider),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string InputTitle { get; set; } = "Valor";
    public double DisplayMultiplier { get => (double)GetValue(DisplayMultiplierProperty); set => SetValue(DisplayMultiplierProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string? DisplayText { get => (string?)GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }

    private bool _dragging;
    private Point _dragStart;
    private double _dragStartValue;

    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x17, 0x17, 0x1A));
    private static readonly Brush FillBrush = Frozen(Color.FromRgb(0x25, 0x43, 0x65));
    private static readonly Brush FillHotBrush = Frozen(Color.FromRgb(0x36, 0x60, 0x89));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA0));
    private static readonly Pen BorderPen = FrozenPen(Color.FromRgb(0x3C, 0x3C, 0x44), 1);
    private static readonly Pen ZeroPen = FrozenPen(Color.FromRgb(0x5A, 0x5A, 0x64), 1);

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double t) { var p = new Pen(Frozen(c), t); p.Freeze(); return p; }

    public ValueSlider()
    {
        MinHeight = 20;
        ClipToBounds = true;
        Cursor = Cursors.SizeWE;
        Focusable = true;
        IsEnabledChanged += (_, _) => InvalidateVisual();
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Bipolar = tem lado negativo (posição). Preenche do centro, não da esquerda.</summary>
    private bool IsBipolar => Minimum < 0 && Maximum > 0;

    private double Norm(double v) => Maximum - Minimum <= 0 ? 0 : (v - Minimum) / (Maximum - Minimum);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var rect = new Rect(0, 0, w, h);
        dc.PushOpacity(IsEnabled ? 1 : .45);
        dc.DrawRoundedRectangle(TrackBrush, BorderPen, rect, 2, 2);

        double vN = Math.Clamp(Norm(Value), 0, 1);
        var fill = _dragging ? FillHotBrush : FillBrush;
        if (IsBipolar)
        {
            double zeroN = Norm(0);
            double x0 = zeroN * w, x1 = vN * w;
            var bar = new Rect(Math.Min(x0, x1), 1, Math.Abs(x1 - x0), h - 2);
            if (bar.Width > 0.5) dc.DrawRectangle(fill, null, bar);
            dc.DrawLine(ZeroPen, new Point(x0, 1), new Point(x0, h - 1));   // marca do zero
        }
        else if (vN > 0)
        {
            dc.DrawRectangle(fill, null, new Rect(1, 1, Math.Max(0, vN * w - 2), h - 2));
        }

        double fontSize = Math.Max(9, Math.Min(11, h - 8));
        if (!string.IsNullOrEmpty(Label))
            dc.DrawText(Text(Label, fontSize, LabelBrush),
                new Point(TextPad, (h - fontSize * 1.35) / 2));

        var valueText = Text(DisplayText ?? (Value * DisplayMultiplier).ToString(Format, CultureInfo.CurrentCulture) + Unit, fontSize, TextBrush);
        dc.DrawText(valueText, new Point(w - valueText.Width - TextPad, (h - fontSize * 1.35) / 2));
        if (IsKeyboardFocused) dc.DrawRoundedRectangle(null, FrozenPen(Color.FromRgb(0x82, 0xBA, 0xF6), 2), new Rect(1, 1, w - 2, h - 2), 2, 2);
        dc.Pop();
    }

    private FormattedText Text(string s, double size, Brush brush) => new(
        s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Duplo-clique = volta ao padrão (Resolume). Não inicia arraste.
            SetCurrentValue(ValueProperty, Math.Clamp(DefaultValue, Minimum, Maximum));
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartValue = Value;
        CaptureMouse();
        Focus();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || ActualWidth <= 0) return;
        double dx = e.GetPosition(this).X - _dragStart.X;
        // RELATIVO ao ponto onde pegou (nunca salta) e proporcional à largura do controle.
        double factor = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? FineFactor : 1.0;
        double delta = dx / ActualWidth * (Maximum - Minimum) * factor;
        SetCurrentValue(ValueProperty, Math.Clamp(_dragStartValue + delta, Minimum, Maximum));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Botão direito: digitar o valor exato (o "Set Value" do Resolume).</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        EditValue();
        e.Handled = true;
    }

    private void EditValue()
    {
        string current = (Value * DisplayMultiplier).ToString(Format, CultureInfo.CurrentCulture);
        string? typed = TextPromptWindow.Ask(string.IsNullOrEmpty(Label) ? InputTitle : $"Valor — {Label}", current,
            Window.GetWindow(this), ValidateDisplayInput);
        ApplyDisplayInput(typed);
    }

    private string? ValidateDisplayInput(string typed)
    {
        if (!double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            return "Informe um número válido. Use vírgula ou ponto para decimais.";
        return value < Minimum * DisplayMultiplier || value > Maximum * DisplayMultiplier
            ? $"Informe um valor entre {Minimum * DisplayMultiplier:0.##} e {Maximum * DisplayMultiplier:0.##}{Unit}." : null;
    }

    private double KeyboardStep => Unit.Length > 0 ? 1 / DisplayMultiplier : (Maximum - Minimum) / 200;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = KeyboardStep * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .1 : 1);
        switch (e.Key)
        {
            case Key.Left: case Key.Down: SetCurrentValue(ValueProperty, Math.Clamp(Value - step, Minimum, Maximum)); break;
            case Key.Right: case Key.Up: SetCurrentValue(ValueProperty, Math.Clamp(Value + step, Minimum, Maximum)); break;
            case Key.Home: SetCurrentValue(ValueProperty, Math.Clamp(DefaultValue, Minimum, Maximum)); break;
            case Key.Enter: case Key.F2: EditValue(); break;
            case Key.Escape when _dragging: SetCurrentValue(ValueProperty, _dragStartValue); ReleaseMouseCapture(); _dragging = false; break;
            default: base.OnKeyDown(e); return;
        }
        InvalidateVisual(); e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = false; InvalidateVisual(); base.OnLostMouseCapture(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ValueSliderPeer(this);

    private sealed class ValueSliderPeer(ValueSlider owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(ValueSlider);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetNameCore() => string.IsNullOrEmpty(base.GetNameCore()) ? owner.InputTitle : base.GetNameCore();
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => SmallChange * 10;
        public double SmallChange => owner.KeyboardStep * owner.DisplayMultiplier;
        public double Maximum => owner.Maximum * owner.DisplayMultiplier;
        public double Minimum => owner.Minimum * owner.DisplayMultiplier;
        public double Value => owner.Value * owner.DisplayMultiplier;
        public void SetValue(double value)
        {
            if (!owner.IsEnabled) throw new System.Windows.Automation.ElementNotEnabledException();
            if (!double.IsFinite(value) || value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            owner.SetCurrentValue(ValueProperty, value / owner.DisplayMultiplier);
        }
    }

    private void ApplyDisplayInput(string? typed)
    {
        if (!string.IsNullOrWhiteSpace(typed) &&
            double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
            double.IsFinite(v))
            SetCurrentValue(ValueProperty, Math.Clamp(v / DisplayMultiplier, Minimum, Maximum));
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, MinHeight);
}
