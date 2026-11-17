using System.Windows;
using System.Windows.Threading;

/// <summary>
/// O Windows não deixa uma janela ficar maior que o monitor (numa sessão remota em
/// 1024×768, a janela de 1440×900 virava 1068×733 e as verificações falhavam por
/// causa do ambiente, não do app). Aqui o CONTEÚDO da janela é diagramado no tamanho
/// pedido mesmo quando o monitor é menor — o print é feito fora da tela, então sai
/// igual em qualquer máquina.
/// </summary>
static class WindowSizing
{
    /// <summary>Depois do Show(): mantém o tamanho do XAML da janela, mesmo num monitor menor.</summary>
    public static void KeepDesignSize(Window window)
    {
        if (!double.IsNaN(window.Width) && !double.IsNaN(window.Height))
            Resize(window, window.Width, window.Height);
    }

    public static void Resize(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.Content is not FrameworkElement root) return;
        root.Width = double.NaN;
        root.Height = double.NaN;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.ActualWidth >= width - 1 && window.ActualHeight >= height - 1) return;

        double chromeW = window.ActualWidth - root.ActualWidth, chromeH = window.ActualHeight - root.ActualHeight;
        root.Width = width - chromeW;
        root.Height = height - chromeH;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine($"  (monitor menor que {width}×{height}: conteúdo diagramado em {root.Width:F0}×{root.Height:F0} fora da tela)");
    }
}
