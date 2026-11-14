using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VideoWallRW.App;

public sealed class IdentifyLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ? "Identificação ligada" : "Identificar telas";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class OperationLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string action && action.StartsWith("TriggerColumn") && int.TryParse(action[13..], out int column))
            return $"Disparar coluna {column}";
        return value?.ToString() switch
        {
            "None" => "Manter clipe", "PlayNextClip" => "Próximo clipe", "PlayPreviousClip" => "Clipe anterior",
            "PlayRandomClip" => "Clipe aleatório", "PlayFirstClip" => "Primeiro clipe",
            "Loop" => "Repetir", "PingPong" => "Ida e volta", "OnceHold" => "Tocar e manter", "OnceClear" => "Tocar e retirar",
            "Add" => "Somar", "Multiply" => "Multiplicar", "Screen" => "Clarear",
            "ToggleMasterPause" => "Troca automática", "ClearLayer" => "Retirar camada", "ClearAll" => "Retirar todas",
            "Blackout" => "Blackout", "ToggleOutputs" => "Ativar/desativar saídas", "SaveProject" => "Salvar projeto",
            "OpenProject" => "Abrir projeto", "NewProject" => "Novo projeto", "NextScene" => "Próxima cena", "PreviousScene" => "Cena anterior",
            "TriggerColumn" => "Disparar coluna", "TriggerScene" => "Abrir cena", "StartSequencer" => "Iniciar sequência",
            "StopSequencer" => "Parar sequência", "BlackoutOn" => "Ligar blackout", "BlackoutOff" => "Desligar blackout",
            "R0" => "0°", "R90" => "90°", "R180" => "180°", "R270" => "270°",
            _ => value?.ToString() ?? "",
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
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

/// <summary>Contagem (ex.: ObservableCollection.Count) == 0 → Visible, senão Collapsed
/// — usado pra mensagem de "lista vazia" (fontes NDI).</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => (value is int i && i == 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Rótulo em português pro combo de Resize (Fit/Fill/Stretch/Original) — só
/// exibição, o enum em si (nome usado no arquivo salvo) continua em inglês.</summary>
public sealed class ResizeModeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            Core.Model.ResizeMode.Fit => "Ajustar (Fit)",
            Core.Model.ResizeMode.Fill => "Preencher (Fill)",
            Core.Model.ResizeMode.Stretch => "Esticar (Stretch)",
            Core.Model.ResizeMode.Original => "Original",
            _ => value?.ToString() ?? "",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Dimensão em pixels antes da escala manual, igual à base do gizmo e do motor.</summary>
public sealed class ClipBaseDimensionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        static float Dimension(object value) => value switch
        {
            int n when n > 0 => n,
            double n when double.IsFinite(n) && n > 0 && n <= float.MaxValue => (float)n,
            _ => 0,
        };

        if (values.Length != 5) return 1.0;
        float sourceWidth = Dimension(values[0]), sourceHeight = Dimension(values[1]);
        float stageWidth = Dimension(values[2]), stageHeight = Dimension(values[3]);
        if (stageWidth <= 0 || stageHeight <= 0) return 1.0;
        bool height = parameter is string axis && axis == "Height";
        // Sem metadados, o gizmo usa a composição inteira como retângulo-base.
        if (sourceWidth <= 0 || sourceHeight <= 0) return (double)(height ? stageHeight : stageWidth);
        var mode = values[4] is Core.Model.ResizeMode resize ? resize : Core.Model.ResizeMode.Fit;
        var rect = Core.Math.ResizeMath.Compute(mode, sourceWidth, sourceHeight, stageWidth, stageHeight);
        return (double)(height ? rect.Height : rect.Width);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
