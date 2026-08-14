using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Catalog;

/// <summary>Feeds the titles curated through the web UI into the normal sync.</summary>
public sealed class ManagedLibraryCatalogSource : ICatalogSource
{
    private readonly ManagedLibraryStore _store;
    private readonly ManagedCatalogOptions _options;
    private readonly ILogger<ManagedLibraryCatalogSource> _logger;

    public ManagedLibraryCatalogSource(
        ManagedLibraryStore store,
        IOptions<JetioOptions> options,
        ILogger<ManagedLibraryCatalogSource> logger)
    {
        _store = store;
        _options = options.Value.Catalogs.Managed;
        _logger = logger;
    }

    public string Name => "managed";

    public bool Enabled => _options.Enabled;

    public async Task<IReadOnlyList<CatalogEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var items = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Managed library: {Count} entries", items.Count);

        return items
            .Select(i => new CatalogEntry(i.ImdbId, i.Kind, i.Name))
            .ToList();
    }
}
