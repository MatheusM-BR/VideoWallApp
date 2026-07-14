using VideoWallRW.Agenda;
using Xunit;

namespace VideoWallRW.Agenda.Tests;

public class AgendaClientTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void ParseAgenda_HtmlReal_ExtraiEventos()
    {
        string html = ReadFixture("agenda-13-07-2026.html");
        var date = new DateOnly(2026, 7, 13);

        var events = AgendaClient.ParseAgenda(html, date);

        Assert.NotEmpty(events);
        var first = events[0];
        Assert.True(first.Id > 0);
        Assert.False(string.IsNullOrWhiteSpace(first.Slug));
        Assert.False(string.IsNullOrWhiteSpace(first.Title));
        Assert.Equal(date, first.Date);
    }

    [Fact]
    public void ParseAgenda_EventoConhecido_CamposCorretos()
    {
        string html = ReadFixture("agenda-13-07-2026.html");
        var events = AgendaClient.ParseAgenda(html, new DateOnly(2026, 7, 13));

        var evt = Assert.Single(events, e => e.Id == 90516);
        Assert.Equal("leilao-virtual-nelore-eliza-empreendimentos", evt.Slug);
        Assert.Equal("Leilão Virtual Nelore Eliza Empreendimentos", evt.Title);
        Assert.Equal("Nelore PO", evt.Category);
        Assert.Contains("Londrina", evt.City);
        Assert.Equal(new TimeOnly(19, 0), evt.Time);
        Assert.NotNull(evt.ChannelLogoUrl);
    }

    [Fact]
    public void ParseAgenda_SemLeiloesArea_LancaSiteLayoutChanged()
    {
        Assert.Throws<SiteLayoutChangedException>(() =>
            AgendaClient.ParseAgenda("<html><body>nada aqui</body></html>", new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void ParseAgenda_AreaExisteSemEventos_RetornaListaVazia()
    {
        var events = AgendaClient.ParseAgenda(
            "<html><body><div class='leiloes-area'><ul></ul></div></body></html>", new DateOnly(2026, 1, 1));
        Assert.Empty(events);
    }

    [Fact]
    public void ParseEventDetails_HtmlReal_ExtraiBannerEDownloads()
    {
        string html = ReadFixture("evento-90516.html");

        var details = AgendaClient.ParseEventDetails(html);

        Assert.Equal("Leilão Virtual Nelore Eliza Empreendimentos", details.Title);
        Assert.NotNull(details.BannerUrl);
        Assert.Contains("/files/leiloes/", details.BannerUrl);
        Assert.Equal(2, details.Downloads.Count);
        Assert.Contains(details.Downloads, d => d.Kind == DownloadKind.Catalog);
        Assert.Contains(details.Downloads, d => d.Kind == DownloadKind.Regulation);
    }

    [Fact]
    public void ParseEventDetails_SemH1_LancaSiteLayoutChanged()
    {
        Assert.Throws<SiteLayoutChangedException>(() =>
            AgendaClient.ParseEventDetails("<html><body>sem titulo</body></html>"));
    }
}
