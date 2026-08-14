using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Catalog;

/// <summary>
/// TMDB popular/trending lists. TMDB is TMDB-keyed, so every hit needs a follow-up
/// external_ids call to get back to the IMDb id Torrentio understands.
/// </summary>
public sealed class TmdbCatalogSource : ICatalogSource
{
    private readonly HttpClient _http;
    private readonly TmdbCatalogOptions _options;
    private readonly ILogger<TmdbCatalogSource> _logger;

    public TmdbCatalogSource(HttpClient http, IOptions<JetioOptions> options, ILogger<TmdbCatalogSource> logger)
    {
        _http = http;
        _options = options.Value.Catalogs.Tmdb;
        _logger = logger;
    }

    public string Name => "tmdb";

    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<IReadOnlyList<CatalogEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CatalogEntry>();

        foreach (var list in _options.Lists)
        {
            if (string.IsNullOrWhiteSpace(list))
            {
                continue;
            }

            var kind = list.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Equals("tv", StringComparison.OrdinalIgnoreCase))
                ? MediaKind.Series
                : MediaKind.Movie;

            var tmdbIds = await GetListIdsAsync(list, cancellationToken).ConfigureAwait(false);
            var resolved = await ResolveImdbIdsAsync(tmdbIds, kind, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "TMDB {List}: {Resolved}/{Total} resolved to IMDb ids",
                list,
                resolved.Count,
                tmdbIds.Count);

            entries.AddRange(resolved);
        }

        return entries;
    }

    private async Task<IReadOnlyList<long>> GetListIdsAsync(string list, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        var page = 1;

        while (ids.Count < _options.MaxItemsPerList && page <= 10)
        {
            var url = BuildUrl($"{list.Trim('/')}", $"language={Uri.EscapeDataString(_options.Language)}&page={page}");

            using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDB list {List} page {Page} failed", list, page);
                break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in results.EnumerateArray())
            {
                if (ids.Count >= _options.MaxItemsPerList)
                {
                    break;
                }

                if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var value))
                {
                    ids.Add(value);
                }
            }

            page++;
        }

        return ids;
    }

    private async Task<IReadOnlyList<CatalogEntry>> ResolveImdbIdsAsync(
        IReadOnlyList<long> tmdbIds,
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var segment = kind == MediaKind.Movie ? "movie" : "tv";
        var resolved = new ConcurrentBag<CatalogEntry>();

        await Parallel.ForEachAsync(
            tmdbIds,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
            async (tmdbId, ct) =>
            {
                var url = BuildUrl($"{segment}/{tmdbId}/external_ids", null);

                using var response = await SendAsync(url, ct).ConfigureAwait(false);
                if (response is null || !response.IsSuccessStatusCode)
                {
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                if (document.RootElement.TryGetProperty("imdb_id", out var imdb)
                    && imdb.ValueKind == JsonValueKind.String)
                {
                    var imdbId = imdb.GetString();
                    if (imdbId.LooksLikeImdbId())
                    {
                        resolved.Add(new CatalogEntry(imdbId!, kind));
                    }
                }
            }).ConfigureAwait(false);

        return resolved.ToList();
    }

    private string BuildUrl(string path, string? query)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(query))
        {
            parts.Add(query);
        }

        // v3 keys travel as a query parameter; v4 read tokens go in the Authorization header.
        if (!UsesBearerToken)
        {
            parts.Add($"api_key={Uri.EscapeDataString(_options.ApiKey)}");
        }

        var suffix = parts.Count > 0 ? $"?{string.Join("&", parts)}" : string.Empty;
        return $"{_options.BaseUrl.TrimEnd('/')}/{path}{suffix}";
    }

    private bool UsesBearerToken =>
        _options.ApiKey.StartsWith("ey", StringComparison.Ordinal) && _options.ApiKey.Contains('.');

    private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (UsesBearerToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "TMDB request failed");
            return null;
        }
    }
}
