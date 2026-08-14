using System.Globalization;
using Jetio.Catalog;
using Jetio.Library;
using Jetio.Stremio;

namespace Jetio.Endpoints;

/// <summary>Browse-and-discover API behind the jetio web UI.</summary>
public static class BrowseEndpoints
{
    /// <summary>Cinemeta's genre list, taken from its manifest. Stable enough to pin.</summary>
    private static readonly string[] Genres =
    [
        "Action", "Adventure", "Animation", "Biography", "Comedy", "Crime", "Documentary",
        "Drama", "Family", "Fantasy", "History", "Horror", "Mystery", "Romance", "Sci-Fi",
        "Sport", "Thriller", "War", "Western",
    ];

    /// <summary>
    /// Cinemeta advertises a "year" catalog but it returns nothing, so sorting is limited to
    /// these two. Year is handled by filtering instead — see <see cref="MatchesDecade"/>.
    /// </summary>
    private static readonly string[] Sorts = ["top", "imdbRating"];

    private const int PageSize = 50;

    /// <summary>
    /// Cap on Cinemeta pages fetched to satisfy a year filter. A single year is a narrow slice
    /// of a popularity-ordered catalog, so this has to reach further than a genre filter would.
    /// </summary>
    private const int MaxFilterPages = 10;

    public static void MapBrowseApi(this WebApplication app)
    {
        app.MapGet("/api/genres", () => Results.Json(new
        {
            genres = Genres,
            sorts = Sorts,
            // The rating catalog only carries recent releases; the UI uses this to explain
            // why an older year comes back empty rather than showing a blank grid.
            ratingCatalogFromYear = 2020,
        }));
        app.MapGet("/api/browse", BrowseAsync);
    }

    private static async Task<IResult> BrowseAsync(
        string? type,
        string? sort,
        string? genre,
        string? year,
        int? skip,
        CinemetaClient cinemeta,
        ManagedLibraryService library,
        CancellationToken cancellationToken)
    {
        var mediaType = type?.ToLowerInvariant() == "series" ? "series" : "movie";
        var kind = mediaType == "series" ? MediaKind.Series : MediaKind.Movie;

        var catalog = Sorts.Contains(sort, StringComparer.OrdinalIgnoreCase) ? sort! : "top";

        var selectedGenre = !string.IsNullOrWhiteSpace(genre) && Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)
            ? genre
            : null;

        var offset = Math.Max(0, skip ?? 0);

        var existing = (await library.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Select(i => $"{i.Kind}:{i.ImdbId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var collected = new List<CinemetaMeta>();
        var page = 0;
        var exhausted = false;

        // Without a year filter this loop runs exactly once. With one it keeps pulling pages
        // until it has a screenful, because Cinemeta cannot filter by year itself.
        while (page < (string.IsNullOrWhiteSpace(year) ? 1 : MaxFilterPages))
        {
            var pageSkip = offset + (page * PageSize);
            var path = BuildCatalogPath(mediaType, catalog, selectedGenre, pageSkip);

            var metas = await cinemeta.GetCatalogAsync(path, cancellationToken).ConfigureAwait(false);

            if (metas.Count == 0)
            {
                exhausted = true;
                break;
            }

            collected.AddRange(metas.Where(m => MatchesYear(m, year)));
            page++;

            if (collected.Count >= PageSize)
            {
                break;
            }
        }

        var results = collected
            .Where(m => (m.ImdbId ?? m.Id).LooksLikeImdbId())
            .DistinctBy(m => m.ImdbId ?? m.Id)
            .Select(m => new
            {
                imdbId = m.ImdbId ?? m.Id,
                name = m.Name,
                year = StrmLibraryWriter.ExtractYear(m),
                poster = m.Poster,
                kind = kind.ToString(),
                inLibrary = existing.Contains($"{kind}:{m.ImdbId ?? m.Id}"),
            })
            .ToList();

        return Results.Json(new
        {
            items = results,
            // How far the client should skip next. Pages consumed matters when filtering.
            nextSkip = offset + (Math.Max(1, page) * PageSize),
            hasMore = !exhausted,
        });
    }

    private static string BuildCatalogPath(string type, string catalog, string? genre, int skip)
    {
        var extras = new List<string>();

        if (!string.IsNullOrWhiteSpace(genre))
        {
            extras.Add($"genre={Uri.EscapeDataString(genre)}");
        }

        if (skip > 0)
        {
            extras.Add(string.Create(CultureInfo.InvariantCulture, $"skip={skip}"));
        }

        return extras.Count == 0
            ? $"{type}/{catalog}"
            : $"{type}/{catalog}/{string.Join("&", extras)}";
    }

    /// <param name="year">A single year such as "2024", or "older" for anything before 1980.</param>
    private static bool MatchesYear(CinemetaMeta meta, string? year)
    {
        if (string.IsNullOrWhiteSpace(year))
        {
            return true;
        }

        var titleYearText = StrmLibraryWriter.ExtractYear(meta);
        if (!int.TryParse(titleYearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var titleYear))
        {
            return false;
        }

        if (year.Equals("older", StringComparison.OrdinalIgnoreCase))
        {
            return titleYear < 1980;
        }

        return int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wanted)
            && titleYear == wanted;
    }
}
