using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoWallRW.App.Manual;
using MPicture = VideoWallRW.App.Manual.Picture;
using MParagraph = VideoWallRW.App.Manual.Paragraph;

namespace VideoWallRW.App;

/// <summary>
/// Manual do programa (botão "📖 Manual" no topo e Ferramentas → Manual). O texto vem de
/// <see cref="ManualContent"/>; os prints são arquivos em Manual\*.png ao lado do exe
/// (copiados de Assets/Manual no build). Print que faltar vira um aviso no lugar — o
/// manual nunca deixa de abrir por causa de imagem.
/// </summary>
public partial class ManualWindow : Window
{
    private readonly List<FrameworkElement> _sectionHeaders = [];
    private bool _scrollingFromIndex;

    public ManualWindow()
    {
        InitializeComponent();
        Build();
        Index.ItemsSource = ManualContent.Sections;
        Scroller.ScrollChanged += OnScrollChanged;
    }

    public static string PicturePath(string file) => Path.Combine(AppContext.BaseDirectory, "Manual", file);

    private void Build()
    {
        int number = 1;
        foreach (var section in ManualContent.Sections)
        {
            var header = new TextBlock
            {
                Text = $"{number++}. {section.Title}",
                FontSize = 22, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, _sectionHeaders.Count == 0 ? 0 : 28, 0, 2),
            };
            _sectionHeaders.Add(header);
            Body.Children.Add(header);
            Body.Children.Add(new TextBlock
            {
                Text = section.Summary, Foreground = Dim, Margin = new Thickness(0, 0, 0, 12),
            });
            foreach (var block in section.Blocks)
                Body.Children.Add(Render(block));
        }
    }

    private Brush Dim => (Brush)FindResource("FgDim");

    private UIElement Render(ManualBlock block) => block switch
    {
        MParagraph p => Text(p.Text, new Thickness(0, 0, 0, 10)),
        Subtitle s => new TextBlock { Text = s.Text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) },
        Bullets b => List(b.Items, i => "•"),
        Steps st => List(st.Items, i => $"{i + 1}."),
        Tip t => Callout("💡", t.Text, (Brush)FindResource("SelectionBg"), (Brush)FindResource("Selection")),
        Warning w => Callout("⚠", w.Text, new SolidColorBrush(Color.FromRgb(0x4A, 0x36, 0x1A)), new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x40))),
        KeyTable k => Keys(k.Rows),
        MPicture pic => PictureBlock(pic),
        _ => new TextBlock(),
    };

    private TextBlock Text(string text, Thickness margin)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = margin };
        foreach (var inline in Inlines(text)) tb.Inlines.Add(inline);
        return tb;
    }

    /// <summary>Marcação mínima: **negrito** e `tecla/caminho`.</summary>
    private IEnumerable<Inline> Inlines(string text)
    {
        foreach (string part in Regex.Split(text, @"(\*\*.+?\*\*|`.+?`)"))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                yield return new Bold(new Run(part[2..^2]));
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                yield return new Run(part[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = (Brush)FindResource("InputBg"),
                };
            else
                yield return new Run(part);
        }
    }

    private UIElement List(string[] items, Func<int, string> marker)
    {
        var grid = new Grid { Margin = new Thickness(4, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var m = new TextBlock { Text = marker(i), Foreground = Dim, Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetRow(m, i);
            var t = Text(items[i], new Thickness(0, 0, 0, 6));
            Grid.SetRow(t, i);
            Grid.SetColumn(t, 1);
            grid.Children.Add(m);
            grid.Children.Add(t);
        }
        return grid;
    }

    private UIElement Callout(string icon, string text, Brush background, Brush border) => new Border
    {
        Background = background, BorderBrush = border, BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 2, 0, 12),
        Child = new DockPanel
        {
            Children =
            {
                new TextBlock { Text = icon, Margin = new Thickness(0, 0, 10, 0), FontSize = 15 },
                Text(text, new Thickness(0)),
            },
        },
    };

    private UIElement Keys((string Keys, string Action)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new Border
            {
                Background = (Brush)FindResource("InputBg"), BorderBrush = (Brush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1), Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 10, 5), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = rows[i].Keys, FontFamily = new FontFamily("Consolas") },
            };
            var action = new TextBlock { Text = rows[i].Action, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 5) };
            Grid.SetRow(key, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(key);
            grid.Children.Add(action);
        }
        return grid;
    }

    private UIElement PictureBlock(MPicture picture)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 14) };
        string path = PicturePath(picture.File);
        BitmapImage? bitmap = TryLoad(path);
        if (bitmap is not null)
        {
            var image = new Image
            {
                Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 560,
                HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
                ToolTip = "Clique para ampliar",
            };
            image.MouseLeftButtonUp += (_, _) => ShowLarge(bitmap, picture.Caption);
            panel.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("BorderDim"), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, Child = image,
            });
        }
        else
        {
            panel.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("BorderDim"), BorderThickness = new Thickness(1),
                Background = (Brush)FindResource("InputBg"), Padding = new Thickness(16, 24, 16, 24),
                Child = new TextBlock
                {
                    Text = $"Imagem ainda não gerada ({picture.File}).",
                    Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center,
                },
            });
        }
        panel.Children.Add(new TextBlock { Text = picture.Caption, Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }

    private static BitmapImage? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // não segura o arquivo aberto
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ShowLarge(BitmapSource bitmap, string caption)
    {
        var viewer = new Window
        {
            Title = caption, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Min(bitmap.PixelWidth + 40, SystemParameters.WorkArea.Width * 0.95),
            Height = Math.Min(bitmap.PixelHeight + 60, SystemParameters.WorkArea.Height * 0.95),
            Background = (Brush)FindResource("Bg"),
            Content = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(10) },
        };
        viewer.KeyDown += (_, e) => { if (e.Key == Key.Escape) viewer.Close(); };
        viewer.Show();
    }

    private void OnIndexSelected(object sender, SelectionChangedEventArgs e)
    {
        int i = Index.SelectedIndex;
        if (i < 0 || i >= _sectionHeaders.Count || !IsLoaded) return;
        _scrollingFromIndex = true;
        var offset = _sectionHeaders[i].TransformToAncestor(Body).Transform(new Point(0, 0)).Y;
        Scroller.ScrollToVerticalOffset(offset);
        _scrollingFromIndex = false;
    }

    /// <summary>Rolando o texto, o índice acompanha a seção visível.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scrollingFromIndex || _sectionHeaders.Count == 0) return;
        int current = 0;
        for (int i = 0; i < _sectionHeaders.Count; i++)
        {
            double y = _sectionHeaders[i].TransformToAncestor(Body).Transform(new Point(0, 0)).Y;
            if (y <= Scroller.VerticalOffset + 40) current = i;
        }
        if (Index.SelectedIndex != current)
        {
            Index.SelectionChanged -= OnIndexSelected;
            Index.SelectedIndex = current;
            Index.ScrollIntoView(Index.SelectedItem);
            Index.SelectionChanged += OnIndexSelected;
        }
    }
}
