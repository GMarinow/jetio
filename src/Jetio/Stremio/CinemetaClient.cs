using System.Net.Http.Json;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Stremio;

/// <summary>
/// Stremio's own metadata addon. IMDb-native, so its ids line up with Torrentio's
/// without any cross-database mapping.
/// </summary>
public sealed class CinemetaClient
{
    private readonly HttpClient _http;
    private readonly CinemetaCatalogOptions _options;
    private readonly ILogger<CinemetaClient> _logger;

    public CinemetaClient(HttpClient http, IOptions<JetioOptions> options, ILogger<CinemetaClient> logger)
    {
        _http = http;
        _options = options.Value.Catalogs.Cinemeta;
        _logger = logger;
    }

    /// <param name="catalogPath">e.g. "movie/top" or "series/top/genre=Action".</param>
    public async Task<IReadOnlyList<CinemetaMeta>> GetCatalogAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/catalog/{catalogPath.Trim('/')}.json";

        try
        {
            var response = await _http.GetFromJsonAsync<CinemetaCatalogResponse>(url, cancellationToken)
                .ConfigureAwait(false);
            return response?.Metas ?? new List<CinemetaMeta>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Cinemeta catalog {Catalog} failed", catalogPath);
            return Array.Empty<CinemetaMeta>();
        }
    }

    /// <summary>Free-text search. Stremio expresses this as a filter on the "top" catalog.</summary>
    /// <param name="type">"movie" or "series".</param>
    public async Task<IReadOnlyList<CinemetaMeta>> SearchAsync(
        string type,
        string query,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/catalog/{type}/top/search={Uri.EscapeDataString(query)}.json";

        try
        {
            var response = await _http.GetFromJsonAsync<CinemetaCatalogResponse>(url, cancellationToken)
                .ConfigureAwait(false);
            return response?.Metas ?? new List<CinemetaMeta>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Cinemeta search failed for {Type}/{Query}", type, query);
            return Array.Empty<CinemetaMeta>();
        }
    }

    /// <param name="type">"movie" or "series".</param>
    public async Task<CinemetaMeta?> GetMetaAsync(string type, string imdbId, CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/meta/{type}/{imdbId}.json";

        try
        {
            var response = await _http.GetFromJsonAsync<CinemetaMetaResponse>(url, cancellationToken)
                .ConfigureAwait(false);
            return response?.Meta;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Cinemeta meta lookup failed for {Type}/{Id}", type, imdbId);
            return null;
        }
    }
}
