using Jetio.Configuration;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

namespace Jetio.Catalog;

/// <summary>
/// A hand-maintained text file. Lines are either "tt0133093" or "series:tt0903747";
/// without a prefix the type is probed against Cinemeta.
/// </summary>
public sealed class WatchlistFileCatalogSource : ICatalogSource
{
    private readonly CinemetaClient _cinemeta;
    private readonly WatchlistCatalogOptions _options;
    private readonly ILogger<WatchlistFileCatalogSource> _logger;

    public WatchlistFileCatalogSource(
        CinemetaClient cinemeta,
        IOptions<JetioOptions> options,
        ILogger<WatchlistFileCatalogSource> logger)
    {
        _cinemeta = cinemeta;
        _options = options.Value.Catalogs.Watchlist;
        _logger = logger;
    }

    public string Name => "watchlist";

    public bool Enabled => _options.Enabled;

    public async Task<IReadOnlyList<CatalogEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.Path))
        {
            _logger.LogInformation("Watchlist file {Path} not found; skipping", _options.Path);
            return Array.Empty<CatalogEntry>();
        }

        var lines = await File.ReadAllLinesAsync(_options.Path, cancellationToken).ConfigureAwait(false);
        var entries = new List<CatalogEntry>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var entry = await ParseLineAsync(line, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                _logger.LogWarning("Watchlist: could not interpret line {Line}", line);
                continue;
            }

            entries.Add(entry);
        }

        _logger.LogInformation("Watchlist: {Count} entries from {Path}", entries.Count, _options.Path);
        return entries;
    }

    private async Task<CatalogEntry?> ParseLineAsync(string line, CancellationToken cancellationToken)
    {
        var separator = line.IndexOf(':');
        if (separator > 0)
        {
            var prefix = line[..separator].Trim().ToLowerInvariant();
            var id = line[(separator + 1)..].Trim();

            if (id.LooksLikeImdbId())
            {
                return prefix switch
                {
                    "movie" or "film" => new CatalogEntry(id, MediaKind.Movie),
                    "series" or "show" or "tv" => new CatalogEntry(id, MediaKind.Series),
                    _ => null,
                };
            }
        }

        var bare = line.Split(new[] { ' ', '\t', '#' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!bare.LooksLikeImdbId())
        {
            return null;
        }

        return await ProbeKindAsync(bare!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cinemeta answers on exactly one of the two type endpoints, so ask both.</summary>
    private async Task<CatalogEntry?> ProbeKindAsync(string imdbId, CancellationToken cancellationToken)
    {
        var asMovie = await _cinemeta.GetMetaAsync("movie", imdbId, cancellationToken).ConfigureAwait(false);
        if (asMovie is not null)
        {
            return new CatalogEntry(imdbId, MediaKind.Movie, asMovie.Name);
        }

        var asSeries = await _cinemeta.GetMetaAsync("series", imdbId, cancellationToken).ConfigureAwait(false);
        return asSeries is not null
            ? new CatalogEntry(imdbId, MediaKind.Series, asSeries.Name)
            : null;
    }
}
