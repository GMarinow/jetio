using System.Text.Json;
using Jetio.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jetio.Jellyfin;

/// <summary>Nudges Jellyfin to pick up newly written .strm files.</summary>
public sealed class JellyfinClient
{
    private const string LibraryIdCacheKey = "jellyfin:library-ids";

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly JellyfinOptions _options;
    private readonly ILogger<JellyfinClient> _logger;

    public JellyfinClient(
        HttpClient http,
        IMemoryCache cache,
        IOptions<JetioOptions> options,
        ILogger<JellyfinClient> logger)
    {
        _http = http;
        _cache = cache;
        _options = options.Value.Jellyfin;
        _logger = logger;
    }

    private string ApiKey => _options.ApiKey.Trim().Trim('"', '\'');

    public async Task<bool> TriggerLibraryRefreshAsync(CancellationToken cancellationToken)
    {
        if (!_options.TriggerRefresh)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            _logger.LogWarning("No Jellyfin ApiKey configured; skipping library refresh");
            return false;
        }

        // Refreshing named libraries only. /Library/Refresh rescans *everything*, which on a
        // server with a large real media collection takes minutes and swamps the few seconds
        // of work jetio actually caused.
        if (_options.LibraryNames.Count > 0)
        {
            var ids = await GetLibraryIdsAsync(cancellationToken).ConfigureAwait(false);

            if (ids.Count > 0)
            {
                var refreshed = 0;
                foreach (var (name, id) in ids)
                {
                    if (await RefreshItemAsync(id, cancellationToken).ConfigureAwait(false))
                    {
                        refreshed++;
                        _logger.LogInformation("Refreshed Jellyfin library {Library}", name);
                    }
                }

                return refreshed > 0;
            }

            _logger.LogWarning(
                "None of the configured LibraryNames ({Names}) matched a Jellyfin library; falling back to a full refresh",
                string.Join(", ", _options.LibraryNames));
        }
        else
        {
            _logger.LogWarning(
                "Jellyfin.LibraryNames is empty, so every library will be rescanned. "
                + "Name your jetio libraries there to keep refreshes fast.");
        }

        return await RefreshEverythingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps configured library names to Jellyfin item ids. Cached; libraries rarely change.</summary>
    private async Task<IReadOnlyList<(string Name, string Id)>> GetLibraryIdsAsync(
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(LibraryIdCacheKey, out IReadOnlyList<(string, string)>? cached) && cached is not null)
        {
            return cached;
        }

        var found = new List<(string, string)>();

        try
        {
            using var request = Authorize(new HttpRequestMessage(
                HttpMethod.Get,
                $"{_options.BaseUrl.TrimEnd('/')}/Library/VirtualFolders"));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not list Jellyfin libraries: {Status}", (int)response.StatusCode);
                return found;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var folder in document.RootElement.EnumerateArray())
            {
                if (!folder.TryGetProperty("Name", out var nameNode)
                    || !folder.TryGetProperty("ItemId", out var idNode))
                {
                    continue;
                }

                var name = nameNode.GetString();
                var id = idNode.GetString();

                if (name is null || id is null)
                {
                    continue;
                }

                if (_options.LibraryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add((name, id));
                }
            }

            _cache.Set(LibraryIdCacheKey, (IReadOnlyList<(string, string)>)found, TimeSpan.FromMinutes(30));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Could not list Jellyfin libraries");
        }

        return found;
    }

    private async Task<bool> RefreshItemAsync(string itemId, CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/Items/{itemId}/Refresh"
            + "?Recursive=true&ImageRefreshMode=Default&MetadataRefreshMode=Default"
            + "&ReplaceAllImages=false&ReplaceAllMetadata=false";

        return await PostAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> RefreshEverythingAsync(CancellationToken cancellationToken) =>
        PostAsync($"{_options.BaseUrl.TrimEnd('/')}/Library/Refresh", cancellationToken);

    /// <summary>
    /// Finds a Jellyfin item by IMDb id. Returns null while Jellyfin has not scanned it yet,
    /// which is normal for the first few seconds after a title is written.
    /// </summary>
    public async Task<string?> FindItemIdAsync(string imdbId, CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/Items"
            + $"?recursive=true&limit=1&anyProviderIdEquals=imdb.{Uri.EscapeDataString(imdbId)}";

        try
        {
            using var request = Authorize(new HttpRequestMessage(HttpMethod.Get, url));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Item lookup for {ImdbId} returned {Status}", imdbId, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("Items", out var items)
                || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() == 0)
            {
                return null;
            }

            return items[0].TryGetProperty("Id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Item lookup failed for {ImdbId}", imdbId);
            return null;
        }
    }

    /// <summary>
    /// Makes Jellyfin re-read the item, which for a .strm means probing the stream behind it.
    /// Images and trickplay are explicitly left alone — regenerating those would seek through
    /// the whole torrent.
    /// </summary>
    public Task<bool> AnalyzeItemAsync(string itemId, CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/Items/{itemId}/Refresh"
            + "?metadataRefreshMode=FullRefresh&imageRefreshMode=None"
            + "&replaceAllMetadata=false&replaceAllImages=false&regenerateTrickplay=false";

        return PostAsync(url, cancellationToken);
    }

    private async Task<bool> PostAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Authorize(new HttpRequestMessage(HttpMethod.Post, url));
            request.Content = new StringContent(string.Empty);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning("Jellyfin refresh returned {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not reach Jellyfin at {BaseUrl}", _options.BaseUrl);
            return false;
        }
    }

    /// <summary>
    /// Newer Jellyfin prefers the Authorization form; X-Emby-Token is the legacy header.
    /// Sending both keeps this working across versions.
    /// </summary>
    private HttpRequestMessage Authorize(HttpRequestMessage request)
    {
        request.Headers.Add("X-Emby-Token", ApiKey);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"MediaBrowser Client=\"jetio\", Device=\"jetio\", DeviceId=\"jetio\", Version=\"1.0\", Token=\"{ApiKey}\"");
        return request;
    }
}
