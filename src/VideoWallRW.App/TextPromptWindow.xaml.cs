using System.Windows;

namespace VideoWallRW.App;

/// <summary>Prompt simples de texto (renomear cena/layer etc.).</summary>
public partial class TextPromptWindow : Window
{
    public string Value => Input.Text;
    public Func<string, string?>? Validator { get; set; }

    public TextPromptWindow(string title, string initialValue)
    {
        InitializeComponent();
        Title = title;
        Input.Text = initialValue;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string? error = Validator?.Invoke(Value);
        if (error is not null)
        {
            ErrorMessage.Text = error;
            ErrorMessage.Visibility = Visibility.Visible;
            Input.Focus();
            return;
        }
        DialogResult = true;
    }

    /// <summary>Abre o prompt e retorna o texto novo, ou null se cancelado/vazio.</summary>
    public static string? Ask(string title, string initialValue, Window? owner = null, Func<string, string?>? validator = null)
    {
        var dlg = new TextPromptWindow(title, initialValue) { Owner = owner ?? Application.Current.MainWindow, Validator = validator };
        return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value) ? dlg.Value.Trim() : null;
    }
}
