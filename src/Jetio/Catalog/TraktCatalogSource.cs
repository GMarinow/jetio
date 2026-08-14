using System.Net.Http.Headers;
using System.Text.Json;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Catalog;

/// <summary>
/// Trakt watchlists and custom lists. Public lists need only a client id; "users/me/..."
/// and private lists additionally need an OAuth access token.
/// </summary>
public sealed class TraktCatalogSource : ICatalogSource
{
    private readonly HttpClient _http;
    private readonly TraktCatalogOptions _options;
    private readonly ILogger<TraktCatalogSource> _logger;

    public TraktCatalogSource(HttpClient http, IOptions<JetioOptions> options, ILogger<TraktCatalogSource> logger)
    {
        _http = http;
        _options = options.Value.Catalogs.Trakt;
        _logger = logger;
    }

    public string Name => "trakt";

    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ClientId);

    public async Task<IReadOnlyList<CatalogEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CatalogEntry>();

        foreach (var list in _options.Lists)
        {
            if (string.IsNullOrWhiteSpace(list))
            {
                continue;
            }

            if (list.Contains("/me/", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(_options.AccessToken))
            {
                _logger.LogWarning("Trakt list {List} needs an AccessToken; skipping", list);
                continue;
            }

            var listEntries = await GetListAsync(list, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Trakt {List}: {Count} entries", list, listEntries.Count);
            entries.AddRange(listEntries);
        }

        return entries;
    }

    private async Task<IReadOnlyList<CatalogEntry>> GetListAsync(string list, CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/{list.Trim('/')}?limit={_options.MaxItemsPerList}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", _options.ClientId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(_options.AccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Trakt list {List} returned {Status}", list, (int)response.StatusCode);
                return Array.Empty<CatalogEntry>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CatalogEntry>();
            }

            var entries = new List<CatalogEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var entry = ParseItem(item);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Trakt list {List} failed", list);
            return Array.Empty<CatalogEntry>();
        }
    }

    /// <summary>
    /// List items wrap the title under "movie" or "show" depending on the endpoint;
    /// bare objects show up on some list types, so fall through to the item itself.
    /// </summary>
    private static CatalogEntry? ParseItem(JsonElement item)
    {
        if (item.TryGetProperty("movie", out var movie))
        {
            return Build(movie, MediaKind.Movie);
        }

        if (item.TryGetProperty("show", out var show))
        {
            return Build(show, MediaKind.Series);
        }

        if (item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var kind = type.GetString() switch
            {
                "movie" => MediaKind.Movie,
                "show" => MediaKind.Series,
                _ => (MediaKind?)null,
            };

            if (kind is not null)
            {
                return Build(item, kind.Value);
            }
        }

        return null;
    }

    private static CatalogEntry? Build(JsonElement node, MediaKind kind)
    {
        if (!node.TryGetProperty("ids", out var ids)
            || !ids.TryGetProperty("imdb", out var imdb)
            || imdb.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var imdbId = imdb.GetString();
        if (!imdbId.LooksLikeImdbId())
        {
            return null;
        }

        var name = node.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString()
            : null;

        return new CatalogEntry(imdbId!, kind, name);
    }
}
