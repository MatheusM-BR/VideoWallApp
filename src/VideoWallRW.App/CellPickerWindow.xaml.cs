using System.Windows;

namespace VideoWallRW.App;

/// <summary>
/// Escolha da célula de destino (layer × coluna) ao enviar mídia pra grade — usado
/// pelo editor de imagem, Agenda, PDF e "Carregar mídia".
/// </summary>
public partial class CellPickerWindow : Window
{
    public int SelectedLayer => LayerPicker.SelectedIndex;
    public int SelectedColumn => ColumnPicker.SelectedIndex;

    public CellPickerWindow(IReadOnlyList<string> layerNames, int columnCount,
        int initialLayer, int initialColumn)
    {
        InitializeComponent();
        foreach (var name in layerNames)
            LayerPicker.Items.Add(name);
        for (int i = 1; i <= columnCount; i++)
            ColumnPicker.Items.Add($"Coluna {i}");

        LayerPicker.SelectedIndex = Math.Clamp(initialLayer, 0, Math.Max(layerNames.Count - 1, 0));
        ColumnPicker.SelectedIndex = Math.Clamp(initialColumn, 0, Math.Max(columnCount - 1, 0));
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
