using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace VideoWallRW.Agenda;

/// <summary>
/// Cliente HTTP + parser do programaleiloes.com (Fase 10 T10.1). Server-rendered —
/// <see cref="HttpClient"/> puro é suficiente, sem WebView. Parse tolerante: campo
/// ausente vira string vazia/null, nunca exceção; layout irreconhecível vira
/// <see cref="SiteLayoutChangedException"/>.
/// </summary>
public sealed partial class AgendaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<DateOnly, (DateTime FetchedAt, IReadOnlyList<AgendaEvent> Events)> _cache = [];

    [GeneratedRegex(@"/agenda/leilao/(\d+)-([a-z0-9-]+)$")] private static partial Regex EventIdSlugRe();

    public AgendaClient(string baseUrl = "https://programaleiloes.com")
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RWVideoWall/1.1");
    }

    public async Task<IReadOnlyList<AgendaEvent>> GetEventsAsync(DateOnly date, CancellationToken ct)
    {
        if (_cache.TryGetValue(date, out var cached) && DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(5))
            return cached.Events;

        string html = await GetStringWithRetryAsync($"/agenda/{date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}", ct);
        var events = ParseAgenda(html, date);
        _cache[date] = (DateTime.UtcNow, events);
        return events;
    }

    public async Task<EventDetails> GetEventAsync(string eventUrl, CancellationToken ct)
    {
        string html = await GetStringWithRetryAsync(eventUrl, ct);
        return ParseEventDetails(html);
    }

    public async Task DownloadFileAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(ToAbsolute(url), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;

        string tmp = destPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(tmp))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0) progress?.Report((double)read / total.Value);
            }
        }
        File.Move(tmp, destPath, overwrite: true);
    }

    private async Task<string> GetStringWithRetryAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            return await _http.GetStringAsync(ToAbsolute(relativeUrl), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);
            return await _http.GetStringAsync(ToAbsolute(relativeUrl), ct);
        }
    }

    private Uri ToAbsolute(string url) => Uri.IsWellFormedUriString(url, UriKind.Absolute)
        ? new Uri(url)
        : new Uri(_http.BaseAddress!, url);

    public static IReadOnlyList<AgendaEvent> ParseAgenda(string html, DateOnly date)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var links = doc.DocumentNode.SelectNodes(AgendaSelectors.EventLinkXPath);
        if (links is null || links.Count == 0)
        {
            if (doc.DocumentNode.SelectSingleNode("//div[contains(@class,'leiloes-area')]") is null)
                throw new SiteLayoutChangedException(
                    "Página de agenda sem '.leiloes-area' — layout do site pode ter mudado.");
            return []; // .leiloes-area existe mas sem eventos nesse dia — legítimo, não é erro.
        }

        var events = new List<AgendaEvent>();
        foreach (var a in links)
        {
            string href = a.GetAttributeValue("href", "");
            var m = EventIdSlugRe().Match(href);
            if (!m.Success) continue;

            events.Add(new AgendaEvent(
                Id: int.Parse(m.Groups[1].Value),
                Slug: m.Groups[2].Value,
                Url: href,
                Title: TextByExactClass(a, "titulo") ?? "",
                Category: TextByExactClass(a, "categoria") ?? "",
                City: TextByExactClass(a, "cidade") ?? "",
                Venue: TextByExactClass(a, "local") ?? "",
                Time: ParseTime(TextByExactClass(a, "hora-tempo")),
                Date: date,
                ChannelLogoUrl: a.SelectSingleNode(AgendaSelectors.TransmissaoImgXPath)?.GetAttributeValue("src", null)));
        }
        return events;
    }

    public static EventDetails ParseEventDetails(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var h1 = doc.DocumentNode.SelectSingleNode(AgendaSelectors.EventTitleXPath);
        if (h1 is null)
            throw new SiteLayoutChangedException("Página de evento sem <h1> — layout do site pode ter mudado.");

        string? banner = doc.DocumentNode.SelectSingleNode(AgendaSelectors.FancyboxXPath)?.GetAttributeValue("href", null);
        string? bannerPreview = null;
        if (banner is null)
            banner = doc.DocumentNode.SelectSingleNode(AgendaSelectors.OgImageXPath)?.GetAttributeValue("content", null);
        else
            bannerPreview = banner;

        var downloads = new List<EventDownload>();
        var downloadNodes = doc.DocumentNode.SelectNodes(AgendaSelectors.DownloadsXPath);
        if (downloadNodes is not null)
        {
            foreach (var a in downloadNodes)
            {
                string href = a.GetAttributeValue("href", "");
                string label = HtmlEntity.DeEntitize(a.InnerText).Trim();
                var kind = href.Contains("/leilao/download/", StringComparison.OrdinalIgnoreCase) ? DownloadKind.Catalog
                    : href.Contains("/leilao/regulamento/", StringComparison.OrdinalIgnoreCase) ? DownloadKind.Regulation
                    : DownloadKind.Other;
                downloads.Add(new EventDownload(href, label, kind));
            }
        }

        return new EventDetails(HtmlEntity.DeEntitize(h1.InnerText).Trim(), banner, bannerPreview, downloads);
    }

    private static string? TextByExactClass(HtmlNode root, string className)
    {
        var node = root.Descendants()
            .FirstOrDefault(n => (n.GetAttributeValue("class", "") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(className));
        return node is null ? null : HtmlEntity.DeEntitize(node.InnerText).Trim();
    }

    private static TimeOnly? ParseTime(string? text)
        => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public void Dispose() => _http.Dispose();
}
