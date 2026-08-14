using System.Text.Json;
using System.Text.Json.Serialization;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Catalog;

public sealed record ManagedItem
{
    public required string ImdbId { get; init; }

    public required MediaKind Kind { get; init; }

    public string? Name { get; init; }

    public string? Year { get; init; }

    public string? Poster { get; init; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The hand-curated list, persisted as JSON so the web UI can edit it programmatically
/// without trampling the comments in watchlist.txt.
/// </summary>
public sealed class ManagedLibraryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ManagedCatalogOptions _options;
    private readonly ILogger<ManagedLibraryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManagedLibraryStore(IOptions<JetioOptions> options, ILogger<ManagedLibraryStore> logger)
    {
        _options = options.Value.Catalogs.Managed;
        _logger = logger;
    }

    public string Path => _options.Path;

    public async Task<IReadOnlyList<ManagedItem>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>false when the title was already present.</returns>
    public async Task<bool> AddAsync(ManagedItem item, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();

            if (items.Any(i => Matches(i, item.ImdbId, item.Kind)))
            {
                return false;
            }

            items.Add(item);
            await WriteAsync(items, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>false when the title was not in the list.</returns>
    public async Task<bool> RemoveAsync(string imdbId, MediaKind kind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var removed = items.RemoveAll(i => Matches(i, imdbId, kind));

            if (removed == 0)
            {
                return false;
            }

            await WriteAsync(items, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Matches(ManagedItem item, string imdbId, MediaKind kind) =>
        item.Kind == kind && string.Equals(item.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<ManagedItem>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.Path))
        {
            return Array.Empty<ManagedItem>();
        }

        try
        {
            await using var stream = File.OpenRead(_options.Path);
            var items = await JsonSerializer
                .DeserializeAsync<List<ManagedItem>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return items ?? new List<ManagedItem>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt store must not take the service down; the UI can rebuild it.
            _logger.LogError(ex, "Could not read managed library at {Path}", _options.Path);
            return Array.Empty<ManagedItem>();
        }
    }

    private async Task WriteAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(_options.Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-then-move so an interrupted write cannot leave a truncated list behind.
        var temporary = _options.Path + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, items, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, _options.Path, overwrite: true);
    }
}
