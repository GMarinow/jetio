using System.Globalization;

namespace Jetio.Streaming;

/// <summary>
/// Identifies a title in a single URL segment, so the HLS routes do not need a separate shape for
/// films and episodes. "movie-tt0133093" and "series-tt0903747-2-5".
/// </summary>
public sealed record TitleRef(string Type, string StremioId, string ImdbId, int? Season, int? Episode)
{
    public string Slug => Season is null
        ? $"movie-{ImdbId}"
        : string.Create(CultureInfo.InvariantCulture, $"series-{ImdbId}-{Season}-{Episode}");

    public static TitleRef Movie(string imdbId) => new("movie", imdbId, imdbId, null, null);

    public static TitleRef Series(string imdbId, int season, int episode) =>
        new(
            "series",
            string.Create(CultureInfo.InvariantCulture, $"{imdbId}:{season}:{episode}"),
            imdbId,
            season,
            episode);

    public static TitleRef? Parse(string slug)
    {
        var parts = slug.Split('-');

        return parts switch
        {
            ["movie", var id] => Movie(id),
            ["series", var id, var s, var e]
                when int.TryParse(s, CultureInfo.InvariantCulture, out var season)
                     && int.TryParse(e, CultureInfo.InvariantCulture, out var episode) =>
                Series(id, season, episode),
            _ => null,
        };
    }
}
