using System.Windows.Media;

/// <summary>
/// Os prints do manual saem destas verificações, e o manual pode ser mostrado fora da
/// empresa: tudo o que aparece neles é neutro — sem marca, sem leilão, sem nomes reais.
/// Os textos de demonstração usam "Evento 1", "Cenário", "Estúdio"; o cabeçalho mostra
/// só "VideoWall" e um ícone genérico de telão no lugar do logo.
/// </summary>
internal static class NeutralDemo
{
    private static readonly (string From, string To)[] Replacements =
    [
        ("RW VideoWall", "VideoWall"),
        ("REMATE WEB", "ESTÚDIO 1"),
        ("Remate Web", "Estúdio"),
        ("LEILÕES", "EVENTOS"),
        ("Leilões", "Eventos"),
        ("leilões", "eventos"),
        ("Leilão", "Evento"),
        ("leilão", "evento"),
    ];

    /// <summary>Aplica as trocas de texto num XAML antes de renderizar.</summary>
    public static string Xaml(string xaml)
    {
        foreach (var (from, to) in Replacements) xaml = xaml.Replace(from, to);
        return xaml;
    }

    /// <summary>Ícone genérico (telão 2×2) no lugar do logo nos prints.</summary>
    public static DrawingImage BrandMark()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)), null, Geometry.Parse("M0,0 H64 V64 H0 Z")));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xDF, 0xE5, 0xED)), null, Geometry.Parse("M6,10 H58 V48 H6 Z M28,48 H36 V54 H44 V58 H20 V54 H28 Z")));
        var tile = new SolidColorBrush(Color.FromRgb(0x5A, 0x8F, 0xD0));
        group.Children.Add(new GeometryDrawing(tile, null, Geometry.Parse("M10,14 H30.5 V27.5 H10 Z M33.5,14 H54 V27.5 H33.5 Z M10,30.5 H30.5 V44 H10 Z M33.5,30.5 H54 V44 H33.5 Z")));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
