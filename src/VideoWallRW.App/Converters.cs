using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VideoWallRW.App;

public sealed class IdentifyLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ? "Identify: ON" : "Identify: OFF";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Índice 0-based → rótulo 1-based (cabeçalho de colunas — o operador conta
/// a partir de 1, igual aos atalhos de teclado).</summary>
public sealed class IncrementConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is int i ? (i + 1).ToString(culture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Liga um ToggleButton.IsChecked a um valor específico de enum (ex.: ferramenta
/// atual do editor de imagem) — ConverterParameter é o valor do enum representado por
/// esse botão.</summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value?.Equals(parameter) == true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ? parameter : Binding.DoNothing;
}

/// <summary>Inverte um bool (usado nos dois ToggleButtons da aba Files/Sources: um é o
/// espelho do outro).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => !(value is true);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is true);
}

/// <summary>bool=true → Collapsed, false → Visible (oposto do BooleanToVisibilityConverter padrão).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Caminho completo → só o nome do arquivo (aba Sources: lista de recentes guarda paths).</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is string path ? System.IO.Path.GetFileName(path) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
