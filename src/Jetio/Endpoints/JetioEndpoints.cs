using System.Globalization;
using System.Text.RegularExpressions;
using Jetio.Configuration;
using Jetio.Library;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

namespace Jetio.Endpoints;

public static partial class JetioEndpoints
{
    private static readonly string[] ReadMethods = { "GET", "HEAD" };

    public static void MapJetioEndpoints(this WebApplication app)
    {
        // These two are what every .strm file points at.
        app.MapMethods("/resolve/movie/{imdbId}", ReadMethods, ResolveMovieAsync);
        app.MapMethods("/resolve/series/{imdbId}/{season:int}/{episode:int}", ReadMethods, ResolveEpisodeAsync);

        // Consumed by the Jellyfin plugin to populate its version picker.
        app.MapGet("/candidates/movie/{imdbId}", ListMovieCandidatesAsync);
        app.MapGet("/candidates/series/{imdbId}/{season:int}/{episode:int}", ListEpisodeCandidatesAsync);

        app.MapPost("/sync", TriggerSync);
        app.MapGet("/status", GetStatus);
        app.MapGet("/healthz", GetHealthAsync);
    }

    private static async Task<IResult> ResolveMovieAsync(
        string imdbId,
        HttpRequest request,
        StreamResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!ImdbIdRegex().IsMatch(imdbId))
        {
            return Results.BadRequest(new { error = "Not an IMDb title id" });
        }

        return await RespondAsync(resolver, "movie", imdbId, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ResolveEpisodeAsync(
        string imdbId,
        int season,
        int episode,
        HttpRequest request,
        StreamResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!ImdbIdRegex().IsMatch(imdbId))
        {
            return Results.BadRequest(new { error = "Not an IMDb title id" });
        }

        if (season < 0 || episode <= 0)
        {
            return Results.BadRequest(new { error = "Season must be >= 0 and episode >= 1" });
        }

        var stremioId = string.Create(CultureInfo.InvariantCulture, $"{imdbId}:{season}:{episode}");
        return await RespondAsync(resolver, "series", stremioId, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> RespondAsync(
        StreamResolver resolver,
        string type,
        string stremioId,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        // ?debug=1 shows every candidate and why it was or wasn't picked.
        if (request.Query.ContainsKey("debug"))
        {
            var ranked = await resolver.DescribeAsync(type, stremioId, cancellationToken).ConfigureAwait(false);

            return Results.Json(new
            {
                type,
                id = stremioId,
                candidates = ranked.Select(c => new
                {
                    release = c.ReleaseName,
                    resolution = c.Resolution,
                    seeders = c.Seeders,
                    sizeGb = Math.Round(c.SizeGb, 2),
                    infoHash = c.Stream.InfoHash,
                    fileIdx = c.Stream.FileIdx,
                    isPack = c.IsPack,
                    eligible = c.IsEligible,
                    rejectedBecause = c.RejectionReason,
                }),
            });
        }

        var refresh = request.Query.ContainsKey("refresh");
        var resolved = await resolver.ResolveAsync(type, stremioId, refresh, cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            return Results.NotFound(new { error = "No playable stream found", type, id = stremioId });
        }

        // 302 rather than a proxy: the player talks to the streaming server directly,
        // so range requests and seeking are not funnelled through jetio.
        return Results.Redirect(resolved.Url, permanent: false);
    }

    private static async Task<IResult> ListMovieCandidatesAsync(
        string imdbId,
        StreamResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!ImdbIdRegex().IsMatch(imdbId))
        {
            return Results.BadRequest(new { error = "Not an IMDb title id" });
        }

        return await ListAsync(resolver, "movie", imdbId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ListEpisodeCandidatesAsync(
        string imdbId,
        int season,
        int episode,
        StreamResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!ImdbIdRegex().IsMatch(imdbId))
        {
            return Results.BadRequest(new { error = "Not an IMDb title id" });
        }

        var stremioId = string.Create(CultureInfo.InvariantCulture, $"{imdbId}:{season}:{episode}");
        return await ListAsync(resolver, "series", stremioId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ListAsync(
        StreamResolver resolver,
        string type,
        string stremioId,
        CancellationToken cancellationToken)
    {
        var options = await resolver.ListOptionsAsync(type, stremioId, cancellationToken).ConfigureAwait(false);

        return Results.Json(options.Select(o => new
        {
            o.Id,
            o.Name,
            o.Url,
            o.Container,
            release = o.Candidate.ReleaseName,
            resolution = o.Candidate.Resolution,
            seeders = o.Candidate.Seeders,
            sizeGb = Math.Round(o.Candidate.SizeGb, 2),
        }));
    }

    private static IResult TriggerSync(
        IServiceScopeFactory scopeFactory,
        SyncState state,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        if (state.IsRunning)
        {
            return Results.Conflict(new { error = "A sync is already running" });
        }

        var logger = loggerFactory.CreateLogger("Jetio.Sync");

        // A full sync outlives the request, so it gets its own scope and the app's lifetime token.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var synchronizer = scope.ServiceProvider.GetRequiredService<LibrarySynchronizer>();
                await synchronizer.SyncAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Manually triggered sync failed");
            }
        });

        return Results.Accepted("/status");
    }

    private static IResult GetStatus(SyncState state, IOptions<JetioOptions> options)
    {
        var jetio = options.Value;

        return Results.Json(new
        {
            running = state.IsRunning,
            libraryRoot = jetio.LibraryRoot,
            publicBaseUrl = jetio.PublicBaseUrl,
            stremioServer = jetio.StremioServer.BaseUrl,
            torrentio = jetio.Torrentio.BaseUrl,
            // Echoed so a misconfigured library list is visible without reading the logs.
            jellyfinLibraries = jetio.Jellyfin.LibraryNames,
            lastSync = state.LastReport is null
                ? null
                : new
                {
                    startedAt = state.LastReport.StartedAt,
                    durationSeconds = Math.Round(state.LastReport.Duration.TotalSeconds, 1),
                    state.LastReport.Movies,
                    state.LastReport.Episodes,
                    state.LastReport.Added,
                    state.LastReport.Pruned,
                    state.LastReport.Failed,
                    bySource = state.LastReport.EntriesBySource,
                },
        });
    }

    private static async Task<IResult> GetHealthAsync(
        StremioServerClient stremioServer,
        IOptions<JetioOptions> options,
        CancellationToken cancellationToken)
    {
        var streamingServerUp = await stremioServer.IsReachableAsync(cancellationToken).ConfigureAwait(false);
        var libraryRoot = options.Value.LibraryRoot;
        var libraryWritable = Directory.Exists(libraryRoot);

        var healthy = streamingServerUp && libraryWritable;

        var payload = new
        {
            status = healthy ? "healthy" : "degraded",
            streamingServer = streamingServerUp ? "up" : "unreachable",
            libraryRoot = libraryWritable ? "ok" : $"missing: {libraryRoot}",
        };

        return healthy ? Results.Ok(payload) : Results.Json(payload, statusCode: 503);
    }

    [GeneratedRegex(@"^tt\d{7,}$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbIdRegex();
}
