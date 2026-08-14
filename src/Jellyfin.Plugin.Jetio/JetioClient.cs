using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jetio;

public sealed class JetioCandidate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("container")]
    public string Container { get; set; } = "mkv";

    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("seeders")]
    public int Seeders { get; set; }

    [JsonPropertyName("sizeGb")]
    public double SizeGb { get; set; }
}

/// <summary>Thin HTTP client over the jetio service. All ranking lives server-side.</summary>
public sealed class JetioClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JetioClient> _logger;

    public JetioClient(IHttpClientFactory httpClientFactory, ILogger<JetioClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<IReadOnlyList<JetioCandidate>> GetMovieCandidatesAsync(
        string imdbId,
        CancellationToken cancellationToken) =>
        GetAsync($"candidates/movie/{imdbId}", cancellationToken);

    public Task<IReadOnlyList<JetioCandidate>> GetEpisodeCandidatesAsync(
        string imdbId,
        int season,
        int episode,
        CancellationToken cancellationToken) =>
        GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"candidates/series/{imdbId}/{season}/{episode}"),
            cancellationToken);

    private async Task<IReadOnlyList<JetioCandidate>> GetAsync(string path, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.JetioBaseUrl))
        {
            return Array.Empty<JetioCandidate>();
        }

        var url = $"{configuration.JetioBaseUrl.TrimEnd('/')}/{path}";

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, configuration.TimeoutSeconds));

            var candidates = await client
                .GetFromJsonAsync<List<JetioCandidate>>(url, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return candidates ?? (IReadOnlyList<JetioCandidate>)Array.Empty<JetioCandidate>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A jetio outage must not break the item page; the .strm source still plays.
            _logger.LogWarning(ex, "jetio lookup failed for {Url}", url);
            return Array.Empty<JetioCandidate>();
        }
    }
}
