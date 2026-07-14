using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace VideoWallRW.App;

/// <summary>
/// Diálogo de tamanho da tela do editor de imagem (estilo Paint): largura/altura,
/// fixar proporção (um campo recalcula o outro), e o modo — esticar a imagem junto
/// ou redimensionar só a tela.
/// </summary>
public partial class CanvasSizeWindow : Window
{
    private readonly double _aspect;
    private bool _updating;

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }
    public bool StretchImage => ModeStretch.IsChecked == true;

    public CanvasSizeWindow(int currentWidth, int currentHeight)
    {
        InitializeComponent();
        _aspect = currentHeight > 0 ? (double)currentWidth / currentHeight : 1;
        _updating = true;
        WidthBox.Text = currentWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = currentHeight.ToString(CultureInfo.InvariantCulture);
        _updating = false;
    }

    private void OnWidthChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || KeepAspect.IsChecked != true) return;
        if (int.TryParse(WidthBox.Text, out int w) && w > 0)
        {
            _updating = true;
            HeightBox.Text = Math.Max((int)Math.Round(w / _aspect), 1).ToString(CultureInfo.InvariantCulture);
            _updating = false;
        }
    }

    private void OnHeightChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || KeepAspect.IsChecked != true) return;
        if (int.TryParse(HeightBox.Text, out int h) && h > 0)
        {
            _updating = true;
            WidthBox.Text = Math.Max((int)Math.Round(h * _aspect), 1).ToString(CultureInfo.InvariantCulture);
            _updating = false;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out int w) || w <= 0 ||
            !int.TryParse(HeightBox.Text, out int h) || h <= 0)
        {
            MessageBox.Show("Largura e altura precisam ser números positivos.", "Tamanho da tela",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        NewWidth = w;
        NewHeight = h;
        DialogResult = true;
    }
}
