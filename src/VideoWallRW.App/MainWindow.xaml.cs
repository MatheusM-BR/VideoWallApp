using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VideoWallRW.App.Input;

namespace VideoWallRW.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly KeymapService _keymap = new();

    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        _vm = vm;
        InitializeComponent();

        _vm.KeymapChanged += () => _keymap.Reload();
        Closing += OnClosing;
        // Seleção vinda do código (ex.: "salvou no editor → mostra na Biblioteca") não
        // rola a lista sozinha; sem isto o arquivo ficava selecionado fora da vista.
        FileList.SelectionChanged += (_, _) =>
        {
            if (FileList.SelectedItem is { } item) FileList.ScrollIntoView(item);
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.CanCloseWithoutPrompt())
        {
            e.Cancel = true;
            return;
        }
        _vm.OnClosing();
    }

    private const string NdiDragFormat = "VideoWallRW.NdiSourceName";

    private void OnClipDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ViewModels.ClipViewModel clipVm }) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                _vm.Grid.SetFiles(clipVm.LayerIndex, clipVm.ColumnIndex, files);
                e.Handled = true;
            }
        }
        else if (e.Data.GetDataPresent(NdiDragFormat))
        {
            if (e.Data.GetData(NdiDragFormat) is string sourceName)
            {
                _vm.SendNdiToCell(sourceName, clipVm.LayerIndex, clipVm.ColumnIndex);
                e.Handled = true;
            }
        }
    }

    // ── Painel FILES: duplo-clique envia, arrastar solta nas células (FileDrop) ──
    private Point _fileDragStart;

    private void OnFileListMouseDown(object sender, MouseButtonEventArgs e)
        => _fileDragStart = e.GetPosition(null);

    private void OnFileListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (FileList.SelectedItem is ViewModels.FileBrowserItem { IsFolder: false } item)
            DragDrop.DoDragDrop(FileList,
                new DataObject(DataFormats.FileDrop, new[] { item.FullPath }), DragDropEffects.Copy);
    }

    private void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is ViewModels.FileBrowserItem item)
            _vm.FileBrowser.Open(item);
    }

    private void OnRecentSourceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string path })
            _vm.SendRecentSource(path);
    }

    private void OnNdiSourceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string name })
            _vm.SendNdiToSelectedCell(name);
    }

    // ── Aba Sources > NDI: duplo-clique envia, arrastar solta nas células ──
    private Point _ndiDragStart;

    private void OnNdiListMouseDown(object sender, MouseButtonEventArgs e)
        => _ndiDragStart = e.GetPosition(null);

    private void OnNdiListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _ndiDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _ndiDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is ListBox { SelectedItem: string name })
            DragDrop.DoDragDrop((DependencyObject)sender,
                new DataObject(NdiDragFormat, name), DragDropEffects.Copy);
    }

    private void OnEditImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ViewModels.ClipViewModel clipVm }) return;
        if (clipVm.Model?.Source is not Core.Model.FileImageSource img) return;
        _vm.EditCellImage(clipVm.LayerIndex, clipVm.ColumnIndex, img.Path);
    }

    private void OnCellClear(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ViewModels.ClipViewModel clipVm })
            _vm.Grid.ClearCell(clipVm.LayerIndex, clipVm.ColumnIndex);
    }

    /// <summary>Ctrl+clique numa célula marca/desmarca ela pro grupo de troca (T20), sem
    /// disparar (TriggerCommand) nem selecionar pra edição (SelectCommand).</summary>
    private void OnCellPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (sender is not FrameworkElement { DataContext: ViewModels.ClipViewModel clipVm }) return;
        clipVm.IsMultiSelected = !clipVm.IsMultiSelected;
        e.Handled = true;
    }

    private void OnLinkCells(object sender, RoutedEventArgs e) => _vm.Grid.LinkMultiSelectedCells();

    private void OnUnlinkCell(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ViewModels.ClipViewModel clipVm })
            _vm.Grid.UnlinkCell(clipVm);
    }

    private void OnLayerDuplicate(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ViewModels.LayerViewModel layerVm })
            _vm.Grid.DuplicateLayer(layerVm.LayerIndex);
    }

    private void OnLayerRemove(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ViewModels.LayerViewModel layerVm })
            _vm.Grid.RemoveLayer(layerVm.LayerIndex);
    }

    // ── Menu de contexto do cabeçalho de coluna ──
    private static int? ColumnFromMenu(object sender)
        => sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: int col } } }
            ? col : null;

    private void OnColumnClear(object sender, RoutedEventArgs e)
    {
        if (ColumnFromMenu(sender) is { } col) _vm.Grid.ClearColumn(col);
    }

    private void OnColumnClone(object sender, RoutedEventArgs e)
    {
        if (ColumnFromMenu(sender) is { } col) _vm.Grid.CloneColumn(col);
    }

    private void OnColumnInsertBefore(object sender, RoutedEventArgs e)
    {
        if (ColumnFromMenu(sender) is { } col) _vm.Grid.InsertColumnBefore(col);
    }

    private void OnColumnInsertAfter(object sender, RoutedEventArgs e)
    {
        if (ColumnFromMenu(sender) is { } col) _vm.Grid.InsertColumnAfter(col);
    }

    private void OnColumnRemove(object sender, RoutedEventArgs e)
    {
        if (ColumnFromMenu(sender) is not { } col) return;
        var confirm = MessageBox.Show($"Remover a coluna {col + 1} inteira (todas as layers)?",
            "VideoWallRW", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
            _vm.Grid.RemoveColumn(col);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Espaço mantém a ação nativa quando o operador usa um controle pelo teclado.
        // Fora deles, alterna a troca automática global.
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None &&
            Keyboard.FocusedElement is not (TextBoxBase or ButtonBase or ComboBox or Slider or Controls.ValueSlider or Controls.TransformGizmo))
        {
            _vm.ToggleMasterPause();
            e.Handled = true;
            return;
        }

        // Ctrl+C/Ctrl+V fora de um TextBox: copiar/colar CLIP entre células.
        // Dentro de um TextBox o comportamento nativo de clipboard continua intacto.
        if (Keyboard.FocusedElement is not TextBoxBase && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.C && _vm.Grid.CopySelectedClip()) { e.Handled = true; return; }
            if (e.Key == Key.V && _vm.Grid.PasteClipboardClip()) { e.Handled = true; return; }
        }

        if (Keyboard.FocusedElement is TextBoxBase || !_keymap.TryGetAction(e, out var action, out var targetLayer))
            return;

        if (action.StartsWith("TriggerColumn", StringComparison.Ordinal) &&
            int.TryParse(action["TriggerColumn".Length..], out int column))
        {
            if (targetLayer is { } layer)
                _vm.Grid.TriggerClipFromShortcut(layer, column - 1);
            else
                _vm.Grid.TriggerColumnFromShortcut(column - 1);
        }
        else
        {
            switch (action)
            {
                case "ToggleMasterPause": _vm.ToggleMasterPause(); break;
                case "ClearLayer": _vm.Grid.ClearSelectedLayer(); break;
                case "ClearAll": _vm.Grid.ClearAll(); break;
                case "Blackout": _vm.ToggleBlackout(); break;
                case "ToggleOutputs": _vm.ToggleOutputs(); break;
                case "SaveProject": _vm.SaveProjectCommand.Execute(null); break;
                case "OpenProject": _vm.OpenProjectCommand.Execute(null); break;
                case "NewProject": _vm.NewProjectCommand.Execute(null); break;
                case "NextScene": _vm.SelectNextScene(); break;
                case "PreviousScene": _vm.SelectPreviousScene(); break;
                default: return;
            }
        }

        e.Handled = true;
    }
}
