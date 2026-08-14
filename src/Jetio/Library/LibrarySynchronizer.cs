using System.Collections.Concurrent;
using Jetio.Catalog;
using Jetio.Configuration;
using Jetio.Jellyfin;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

namespace Jetio.Library;

public sealed record SyncReport
{
    public required DateTimeOffset StartedAt { get; init; }

    public required TimeSpan Duration { get; init; }

    public required int Movies { get; init; }

    public required int Episodes { get; init; }

    /// <summary>Files actually written this pass — new episodes, new titles, changed URLs.</summary>
    public required int Added { get; init; }

    public required int Pruned { get; init; }

    public required int Failed { get; init; }

    public required IReadOnlyDictionary<string, int> EntriesBySource { get; init; }
}

/// <summary>Runs one full catalog -> .strm pass. Serialised; concurrent callers wait.</summary>
public sealed class LibrarySynchronizer
{
    private readonly IEnumerable<ICatalogSource> _sources;
    private readonly CinemetaClient _cinemeta;
    private readonly StrmLibraryWriter _writer;
    private readonly JellyfinClient _jellyfin;
    private readonly SyncState _state;
    private readonly SyncOptions _options;
    private readonly ILogger<LibrarySynchronizer> _logger;

    public LibrarySynchronizer(
        IEnumerable<ICatalogSource> sources,
        CinemetaClient cinemeta,
        StrmLibraryWriter writer,
        JellyfinClient jellyfin,
        SyncState state,
        IOptions<JetioOptions> options,
        ILogger<LibrarySynchronizer> logger)
    {
        _sources = sources;
        _cinemeta = cinemeta;
        _writer = writer;
        _jellyfin = jellyfin;
        _state = state;
        _options = options.Value.Sync;
        _logger = logger;
    }

    public async Task<SyncReport> SyncAsync(CancellationToken cancellationToken)
    {
        using var handle = await _state.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var startedAt = DateTimeOffset.UtcNow;

        {
            var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var entries = new Dictionary<(string ImdbId, MediaKind Kind), CatalogEntry>();

            foreach (var source in _sources.Where(s => s.Enabled))
            {
                try
                {
                    var sourceEntries = await source.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
                    bySource[source.Name] = sourceEntries.Count;

                    foreach (var entry in sourceEntries)
                    {
                        entries.TryAdd((entry.ImdbId, entry.Kind), entry);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Catalog source {Source} failed", source.Name);
                    bySource[source.Name] = 0;
                }
            }

            _logger.LogInformation("Syncing {Count} unique titles", entries.Count);

            var written = new ConcurrentBag<string>();
            var movies = 0;
            var episodes = 0;
            var added = 0;
            var failed = 0;

            await Parallel.ForEachAsync(
                entries.Values,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrency),
                    CancellationToken = cancellationToken,
                },
                async (entry, ct) =>
                {
                    try
                    {
                        var meta = await _cinemeta.GetMetaAsync(entry.StremioType, entry.ImdbId, ct)
                            .ConfigureAwait(false);

                        if (meta is null || string.IsNullOrWhiteSpace(meta.Name))
                        {
                            _logger.LogWarning("No Cinemeta metadata for {Id}; skipping", entry.ImdbId);
                            Interlocked.Increment(ref failed);
                            return;
                        }

                        var result = entry.Kind == MediaKind.Movie
                            ? await _writer.WriteMovieAsync(meta, ct).ConfigureAwait(false)
                            : await _writer.WriteSeriesAsync(meta, ct).ConfigureAwait(false);

                        foreach (var path in result.Paths)
                        {
                            written.Add(path);
                        }

                        if (entry.Kind == MediaKind.Movie)
                        {
                            Interlocked.Increment(ref movies);
                        }
                        else
                        {
                            Interlocked.Add(ref episodes, result.Paths.Count);
                        }

                        Interlocked.Add(ref added, result.Changed);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Failed to write {Id}", entry.ImdbId);
                        Interlocked.Increment(ref failed);
                    }
                }).ConfigureAwait(false);

            var pruned = _writer.Prune(written.ToHashSet(StringComparer.OrdinalIgnoreCase));

            var report = new SyncReport
            {
                StartedAt = startedAt,
                Duration = DateTimeOffset.UtcNow - startedAt,
                Movies = movies,
                Episodes = episodes,
                Added = added,
                Pruned = pruned,
                Failed = failed,
                EntriesBySource = bySource,
            };

            _logger.LogInformation(
                "Sync finished in {Duration}: {Movies} movies, {Episodes} episodes, {Added} new, {Pruned} pruned, {Failed} failed",
                report.Duration,
                movies,
                episodes,
                added,
                pruned,
                failed);

            _state.Publish(report);

            // Only disturb Jellyfin when something actually changed. Counting every file
            // written would trigger a full rescan on every sync forever.
            if (added + pruned > 0)
            {
                await _jellyfin.TriggerLibraryRefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            return report;
        }
    }
}
