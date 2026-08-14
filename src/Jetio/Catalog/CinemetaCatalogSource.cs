using Jetio.Configuration;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

namespace Jetio.Catalog;

/// <summary>Pulls Stremio's own top/popular catalogs. No API key, already IMDb-keyed.</summary>
public sealed class CinemetaCatalogSource : ICatalogSource
{
    private readonly CinemetaClient _cinemeta;
    private readonly CinemetaCatalogOptions _options;
    private readonly ILogger<CinemetaCatalogSource> _logger;

    public CinemetaCatalogSource(
        CinemetaClient cinemeta,
        IOptions<JetioOptions> options,
        ILogger<CinemetaCatalogSource> logger)
    {
        _cinemeta = cinemeta;
        _options = options.Value.Catalogs.Cinemeta;
        _logger = logger;
    }

    public string Name => "cinemeta";

    public bool Enabled => _options.Enabled && _options.Catalogs.Count > 0;

    public async Task<IReadOnlyList<CatalogEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CatalogEntry>();

        foreach (var catalogPath in _options.Catalogs)
        {
            if (string.IsNullOrWhiteSpace(catalogPath))
            {
                continue;
            }

            var kind = catalogPath.TrimStart('/').StartsWith("series", StringComparison.OrdinalIgnoreCase)
                ? MediaKind.Series
                : MediaKind.Movie;

            var metas = await _cinemeta.GetCatalogAsync(catalogPath, cancellationToken).ConfigureAwait(false);

            var taken = metas
                .Select(m => m.ImdbId ?? m.Id)
                .Where(id => id.LooksLikeImdbId())
                .Take(_options.MaxItemsPerCatalog)
                .Select(id => new CatalogEntry(id, kind))
                .ToList();

            _logger.LogInformation("Cinemeta {Catalog}: {Count} entries", catalogPath, taken.Count);
            entries.AddRange(taken);
        }

        return entries;
    }
}
