using System.Net.Http.Json;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Stremio;

/// <summary>Queries Torrentio for the available releases behind an IMDb id.</summary>
public sealed class TorrentioClient
{
    private readonly HttpClient _http;
    private readonly TorrentioOptions _options;
    private readonly ILogger<TorrentioClient> _logger;

    public TorrentioClient(HttpClient http, IOptions<JetioOptions> options, ILogger<TorrentioClient> logger)
    {
        _http = http;
        _options = options.Value.Torrentio;
        _logger = logger;
    }

    /// <param name="type">"movie" or "series".</param>
    /// <param name="stremioId">"tt0133093" for a movie, "tt0903747:1:1" for an episode.</param>
    public async Task<IReadOnlyList<TorrentioStream>> GetStreamsAsync(
        string type,
        string stremioId,
        CancellationToken cancellationToken)
    {
        var config = _options.Configuration?.Trim().Trim('/');
        var path = string.IsNullOrEmpty(config)
            ? $"stream/{type}/{stremioId}.json"
            : $"{config}/stream/{type}/{stremioId}.json";

        var url = $"{_options.BaseUrl.TrimEnd('/')}/{path}";

        try
        {
            var response = await _http.GetFromJsonAsync<StreamsResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            var streams = response?.Streams ?? new List<TorrentioStream>();
            _logger.LogDebug("Torrentio returned {Count} streams for {Type}/{Id}", streams.Count, type, stremioId);
            return streams;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Torrentio lookup failed for {Type}/{Id}", type, stremioId);
            return Array.Empty<TorrentioStream>();
        }
    }
}
