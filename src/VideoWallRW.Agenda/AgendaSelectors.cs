namespace VideoWallRW.Agenda;

/// <summary>
/// Seletores CSS/XPath do programaleiloes.com num lugar só — quando o site mudar de
/// layout, é aqui que se conserta (ver docs/fases/FASE-10.md, "Armadilhas conhecidas").
/// Verificado em 13/07/2026.
/// </summary>
internal static class AgendaSelectors
{
    public const string EventLinkXPath = "//div[contains(@class,'leiloes-area')]//ul/li/a[starts-with(@href,'/agenda/leilao/')]";
    public const string DiaSemanaClass = "dia-semana";
    public const string DiaClass = "dia";
    public const string MesClass = "mes";
    public const string HoraTempoClass = "hora-tempo";
    public const string TransmissaoImgXPath = ".//span[contains(@class,'transmissao')]//img";
    public const string TituloClass = "titulo";
    public const string CategoriaClass = "categoria";
    public const string LocalClass = "local";
    public const string CidadeClass = "cidade";

    public const string EventTitleXPath = "//h1";
    public const string FancyboxXPath = "//a[contains(@class,'fancybox')]";
    public const string OgImageXPath = "//meta[@property='og:image']";
    public const string DownloadsXPath = "//div[contains(@class,'left') and contains(@class,'downloads')]//a";
}
