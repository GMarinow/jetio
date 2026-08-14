using System.Globalization;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Stremio;

/// <summary>
/// Builds playable URLs against the Stremio streaming server, which is the torrent engine.
/// Format taken from stremio-core: {base}/{infoHash}/{fileIdx or -1}?tr=...&amp;f=...
/// </summary>
public sealed class StremioServerClient
{
    private const string TrackerPrefix = "tracker:";

    private readonly HttpClient _http;
    private readonly StremioServerOptions _options;
    private readonly ILogger<StremioServerClient> _logger;

    public StremioServerClient(HttpClient http, IOptions<JetioOptions> options, ILogger<StremioServerClient> logger)
    {
        _http = http;
        _options = options.Value.StremioServer;
        _logger = logger;
    }

    public string BuildStreamUrl(TorrentioStream stream)
    {
        if (string.IsNullOrWhiteSpace(stream.InfoHash))
        {
            throw new ArgumentException("Stream has no infoHash", nameof(stream));
        }

        var infoHash = stream.InfoHash.Trim().ToLowerInvariant();
        var fileIdx = stream.FileIdx?.ToString(CultureInfo.InvariantCulture) ?? "-1";

        var query = new List<string>();

        foreach (var tracker in CollectTrackers(stream))
        {
            query.Add($"tr={Uri.EscapeDataString(tracker)}");
        }

        // With no fileIdx the server picks the largest file, which is wrong for season packs.
        // The filename hint narrows it back down to the episode we actually asked for.
        if (stream.FileIdx is null && !string.IsNullOrWhiteSpace(stream.BehaviorHints?.Filename))
        {
            query.Add($"f={Uri.EscapeDataString(stream.BehaviorHints.Filename)}");
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/{infoHash}/{fileIdx}";
        return query.Count == 0 ? url : $"{url}?{string.Join("&", query)}";
    }

    private IEnumerable<string> CollectTrackers(TorrentioStream stream)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in stream.Sources ?? Enumerable.Empty<string>())
        {
            if (source.StartsWith(TrackerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var tracker = source[TrackerPrefix.Length..].Trim();
                if (tracker.Length > 0 && seen.Add(tracker))
                {
                    yield return tracker;
                }
            }
        }

        foreach (var tracker in _options.ExtraTrackers)
        {
            if (!string.IsNullOrWhiteSpace(tracker) && seen.Add(tracker.Trim()))
            {
                yield return tracker.Trim();
            }
        }
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync($"{_options.BaseUrl.TrimEnd('/')}/settings", cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Stremio streaming server unreachable at {BaseUrl}", _options.BaseUrl);
            return false;
        }
    }
}
