using Jetio.Catalog;
using Jetio.Configuration;
using Jetio.Jellyfin;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

namespace Jetio.Library;

public sealed record AddResult(bool Added, ManagedItem? Item, string? Error);

/// <summary>
/// Add and remove single titles on demand. Deliberately does the .strm write inline rather
/// than waiting for the next scheduled sync — the point of the UI is that a title shows up
/// in Jellyfin seconds after you click Add.
/// </summary>
public sealed class ManagedLibraryService
{
    private readonly ManagedLibraryStore _store;
    private readonly CinemetaClient _cinemeta;
    private readonly StrmLibraryWriter _writer;
    private readonly JellyfinClient _jellyfin;
    private readonly MediaAnalysisQueue _analysis;
    private readonly JellyfinOptions _options;
    private readonly ILogger<ManagedLibraryService> _logger;

    public ManagedLibraryService(
        ManagedLibraryStore store,
        CinemetaClient cinemeta,
        StrmLibraryWriter writer,
        JellyfinClient jellyfin,
        MediaAnalysisQueue analysis,
        IOptions<JetioOptions> options,
        ILogger<ManagedLibraryService> logger)
    {
        _store = store;
        _cinemeta = cinemeta;
        _writer = writer;
        _jellyfin = jellyfin;
        _analysis = analysis;
        _options = options.Value.Jellyfin;
        _logger = logger;
    }

    public Task<IReadOnlyList<ManagedItem>> GetAllAsync(CancellationToken cancellationToken) =>
        _store.GetAllAsync(cancellationToken);

    public async Task<AddResult> AddAsync(string imdbId, MediaKind kind, CancellationToken cancellationToken)
    {
        var type = kind == MediaKind.Movie ? "movie" : "series";
        var meta = await _cinemeta.GetMetaAsync(type, imdbId, cancellationToken).ConfigureAwait(false);

        if (meta is null || string.IsNullOrWhiteSpace(meta.Name))
        {
            return new AddResult(false, null, $"Cinemeta has no {type} metadata for {imdbId}");
        }

        var item = new ManagedItem
        {
            ImdbId = imdbId,
            Kind = kind,
            Name = meta.Name,
            Year = StrmLibraryWriter.ExtractYear(meta),
            Poster = meta.Poster,
        };

        var added = await _store.AddAsync(item, cancellationToken).ConfigureAwait(false);
        if (!added)
        {
            return new AddResult(false, item, "Already in your library");
        }

        var result = kind == MediaKind.Movie
            ? await _writer.WriteMovieAsync(meta, cancellationToken).ConfigureAwait(false)
            : await _writer.WriteSeriesAsync(meta, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Wrote {Count} file(s) for {Name}", result.Paths.Count, meta.Name);

        await _jellyfin.TriggerLibraryRefreshAsync(cancellationToken).ConfigureAwait(false);

        // Queued rather than awaited: analysis has to wait for Jellyfin's scan to reach the new
        // item, which is far longer than this request should take. Only on explicit adds —
        // doing it during a bulk sync would start every title downloading at once.
        if (_options.AnalyzeAddedItems)
        {
            _analysis.Enqueue(new AnalysisRequest(imdbId, meta.Name));
        }

        _logger.LogInformation("Added {Name} ({ImdbId}) to the managed library", meta.Name, imdbId);
        return new AddResult(true, item, null);
    }

    public async Task<bool> RemoveAsync(string imdbId, MediaKind kind, CancellationToken cancellationToken)
    {
        var removed = await _store.RemoveAsync(imdbId, kind, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return false;
        }

        // Delete the files now rather than leaving them until the next prune, so the item
        // disappears from Jellyfin at the same time it disappears from the UI.
        var deleted = _writer.RemoveTitle(kind, imdbId);
        _logger.LogInformation("Removed {ImdbId}, deleted {Count} folder(s)", imdbId, deleted);

        await _jellyfin.TriggerLibraryRefreshAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
