namespace VideoWallRW.Agenda;

public sealed record AgendaEvent(int Id, string Slug, string Url, string Title,
    string Category, string City, string Venue, TimeOnly? Time, DateOnly Date,
    string? ChannelLogoUrl);

public enum DownloadKind { Catalog, Regulation, Other }

public sealed record EventDownload(string Url, string Label, DownloadKind Kind);

public sealed record EventDetails(string Title, string? BannerUrl,
    string? BannerPreviewUrl, IReadOnlyList<EventDownload> Downloads);

/// <summary>Layout do site mudou de um jeito que o parser não reconhece — página
/// sem <c>.leiloes-area</c>/evento sem <c>&lt;h1&gt;</c>. O HTML bruto é salvo pelo
/// chamador para diagnóstico (ver <see cref="AgendaClient"/>).</summary>
public sealed class SiteLayoutChangedException(string message) : Exception(message);
