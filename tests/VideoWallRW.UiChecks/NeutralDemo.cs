/// <summary>
/// Os prints do manual saem destas verificações, e o manual pode ser mostrado fora da
/// empresa: tudo o que aparece neles é neutro — sem marca, sem leilão, sem nomes reais.
/// Os textos de demonstração usam "Evento 1", "Cenário", "Estúdio". O nome do produto
/// (RWVideoWall) aparece normalmente; nomes antigos que sobrarem viram neutros.
/// </summary>
internal static class NeutralDemo
{
    private static readonly (string From, string To)[] Replacements =
    [
        ("RW VideoWall", "RWVideoWall"),
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
}
