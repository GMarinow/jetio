using Jetio.Catalog;
using Jetio.Library;
using Jetio.Stremio;

namespace Jetio.Endpoints;

/// <summary>Backs the search-and-add web UI served from wwwroot.</summary>
public static class LibraryApiEndpoints
{
    public sealed record AddRequest(string ImdbId, string Kind);

    public static void MapLibraryApi(this WebApplication app)
    {
        app.MapGet("/api/search", SearchAsync);
        app.MapGet("/api/library", GetLibraryAsync);
        app.MapPost("/api/library", AddAsync);
        app.MapDelete("/api/library/{kind}/{imdbId}", RemoveAsync);

        // Retrofits titles added before analysis existed, one at a time.
        app.MapPost("/api/library/{imdbId}/analyze", Analyze);
    }

    private static IResult Analyze(string imdbId, MediaAnalysisQueue queue)
    {
        if (!imdbId.LooksLikeImdbId())
        {
            return Results.BadRequest(new { error = "Not an IMDb title id" });
        }

        queue.Enqueue(new AnalysisRequest(imdbId, null));
        return Results.Accepted("/status", new { queued = imdbId });
    }

    private static async Task<IResult> SearchAsync(
        string? q,
        string? type,
        CinemetaClient cinemeta,
        ManagedLibraryService library,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return Results.BadRequest(new { error = "Enter at least two characters" });
        }

        var query = q.Trim();

        // Default to searching both, since people rarely think in Stremio's type split.
        var types = type?.ToLowerInvariant() switch
        {
            "movie" => new[] { "movie" },
            "series" => new[] { "series" },
            _ => new[] { "movie", "series" },
        };

        var existing = (await library.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Select(i => $"{i.Kind}:{i.ImdbId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<object>();

        foreach (var searchType in types)
        {
            var metas = await cinemeta.SearchAsync(searchType, query, cancellationToken).ConfigureAwait(false);
            var kind = searchType == "movie" ? MediaKind.Movie : MediaKind.Series;

            results.AddRange(metas
                .Where(m => (m.ImdbId ?? m.Id).LooksLikeImdbId())
                .Select(m => new
                {
                    imdbId = m.ImdbId ?? m.Id,
                    name = m.Name,
                    year = StrmLibraryWriter.ExtractYear(m),
                    poster = m.Poster,
                    kind = kind.ToString(),
                    inLibrary = existing.Contains($"{kind}:{m.ImdbId ?? m.Id}"),
                }));
        }

        return Results.Json(results);
    }

    private static async Task<IResult> GetLibraryAsync(
        ManagedLibraryService library,
        CancellationToken cancellationToken)
    {
        var items = await library.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Results.Json(items.OrderByDescending(i => i.AddedAt).Select(Shape));
    }

    /// <summary>
    /// Every endpoint returns items in this shape. Notably Kind goes out as a string —
    /// the default serializer would emit the enum's integer value, which the UI posts back.
    /// </summary>
    private static object Shape(ManagedItem item) => new
    {
        imdbId = item.ImdbId,
        name = item.Name,
        year = item.Year,
        poster = item.Poster,
        kind = item.Kind.ToString(),
        addedAt = item.AddedAt,
    };

    private static async Task<IResult> AddAsync(
        AddRequest request,
        ManagedLibraryService library,
        CancellationToken cancellationToken)
    {
        if (!request.ImdbId.LooksLikeImdbId())
        {
            return Results.BadRequest(new { error = "Not an IMDb title id" });
        }

        if (!TryParseKind(request.Kind, out var kind))
        {
            return Results.BadRequest(new { error = "Kind must be Movie or Series" });
        }

        var result = await library.AddAsync(request.ImdbId, kind, cancellationToken).ConfigureAwait(false);

        return result.Added && result.Item is not null
            ? Results.Json(new { added = true, item = Shape(result.Item) })
            : Results.Json(new { added = false, error = result.Error }, statusCode: 409);
    }

    private static async Task<IResult> RemoveAsync(
        string kind,
        string imdbId,
        ManagedLibraryService library,
        CancellationToken cancellationToken)
    {
        // Validated because this id reaches the filesystem: RemoveTitle matches it against
        // directory names to decide what to delete.
        if (!imdbId.LooksLikeImdbId())
        {
            return Results.BadRequest(new { error = "Not an IMDb title id" });
        }

        if (!TryParseKind(kind, out var mediaKind))
        {
            return Results.BadRequest(new { error = "Kind must be Movie or Series" });
        }

        var removed = await library.RemoveAsync(imdbId, mediaKind, cancellationToken).ConfigureAwait(false);

        return removed
            ? Results.Json(new { removed = true })
            : Results.NotFound(new { error = "Not in your library" });
    }

    private static bool TryParseKind(string value, out MediaKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind);
}
