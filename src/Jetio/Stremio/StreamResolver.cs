using System.Globalization;
using Jetio.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jetio.Stremio;

public sealed record ResolvedStream(string Url, StreamCandidate Candidate);

/// <summary>One selectable release, as offered to the Jellyfin version picker.</summary>
public sealed record StreamOption(string Id, string Name, string Url, string Container, StreamCandidate Candidate);

/// <summary>
/// Turns a Stremio id into a URL a player can open, right now. Resolution deliberately happens
/// at playback time rather than at sync time — release availability drifts, and debrid links expire.
/// </summary>
public sealed class StreamResolver
{
    private readonly TorrentioClient _torrentio;
    private readonly StreamSelector _selector;
    private readonly StremioServerClient _stremioServer;
    private readonly CinemetaClient _cinemeta;
    private readonly IMemoryCache _cache;
    private readonly StreamSelectionOptions _options;
    private readonly ILogger<StreamResolver> _logger;

    public StreamResolver(
        TorrentioClient torrentio,
        StreamSelector selector,
        StremioServerClient stremioServer,
        CinemetaClient cinemeta,
        IMemoryCache cache,
        IOptions<JetioOptions> options,
        ILogger<StreamResolver> logger)
    {
        _torrentio = torrentio;
        _selector = selector;
        _stremioServer = stremioServer;
        _cinemeta = cinemeta;
        _cache = cache;
        _options = options.Value.StreamSelection;
        _logger = logger;
    }

    /// <summary>
    /// The title a release is supposed to be, used to catch mislabelled torrents. Cached for
    /// a long time — a film's name and year do not change.
    /// </summary>
    private async Task<TitleContext?> GetExpectedTitleAsync(
        string type,
        string stremioId,
        CancellationToken cancellationToken)
    {
        if (!_options.VerifyTitles)
        {
            return null;
        }

        var imdbId = stremioId.Split(':')[0];
        var cacheKey = $"expected:{type}:{imdbId}";

        if (_cache.TryGetValue(cacheKey, out TitleContext? cached))
        {
            return cached;
        }

        var meta = await _cinemeta.GetMetaAsync(type, imdbId, cancellationToken).ConfigureAwait(false);

        TitleContext? expected = null;
        if (meta is not null && !string.IsNullOrWhiteSpace(meta.Name))
        {
            // Episode releases carry the show's name but not a per-episode year, so the year
            // check only applies to films.
            var year = IsMovie(type) ? ExtractYear(meta) : null;
            expected = new TitleContext(meta.Name, year);
        }

        _cache.Set(cacheKey, expected, TimeSpan.FromHours(12));
        return expected;
    }

    private static string? ExtractYear(CinemetaMeta meta)
    {
        foreach (var candidate in new[] { meta.Year, meta.ReleaseInfo })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var digits = new string(candidate.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 4)
            {
                return digits;
            }
        }

        return null;
    }

    public async Task<ResolvedStream?> ResolveAsync(
        string type,
        string stremioId,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"resolve:{type}:{stremioId}";

        if (!bypassCache && _cache.TryGetValue(cacheKey, out ResolvedStream? cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Type}/{Id}", type, stremioId);
            return cached;
        }

        var expected = await GetExpectedTitleAsync(type, stremioId, cancellationToken).ConfigureAwait(false);
        var streams = await _torrentio.GetStreamsAsync(type, stremioId, cancellationToken).ConfigureAwait(false);
        var best = _selector.Select(streams, IsMovie(type), expected);

        if (best is null)
        {
            _logger.LogWarning("No usable stream for {Type}/{Id}", type, stremioId);
            return null;
        }

        // A debrid-configured Torrentio hands back a direct URL; otherwise the Stremio
        // streaming server is the torrent engine that makes the infoHash playable.
        var url = !string.IsNullOrWhiteSpace(best.Stream.Url)
            ? best.Stream.Url!
            : _stremioServer.BuildStreamUrl(best.Stream);

        var resolved = new ResolvedStream(url, best);

        _cache.Set(cacheKey, resolved, TimeSpan.FromMinutes(_options.CacheMinutes));

        _logger.LogInformation(
            "Resolved {Type}/{Id} -> {Release} [{Resolution}, {Seeders} seeders, {Size:0.##} GB]",
            type,
            stremioId,
            best.ReleaseName,
            best.Resolution ?? "unknown",
            best.Seeders,
            best.SizeGb);

        return resolved;
    }

    /// <summary>
    /// Ranked, playable options for the Jellyfin plugin's version picker. Unlike
    /// <see cref="ResolveAsync"/> this commits to nothing — the user picks.
    /// </summary>
    public async Task<IReadOnlyList<StreamOption>> ListOptionsAsync(
        string type,
        string stremioId,
        CancellationToken cancellationToken)
    {
        // Jellyfin calls the provider on every item-page view and again on playback info,
        // so this has to be cached or a browsing session hammers Torrentio.
        var cacheKey = $"options:{type}:{stremioId}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<StreamOption>? cached) && cached is not null)
        {
            return cached;
        }

        var ranked = await DescribeAsync(type, stremioId, cancellationToken).ConfigureAwait(false);

        var options = ranked
            .Where(c => c.IsEligible)
            .Take(Math.Max(1, _options.MaxSourcesExposed))
            .Select(ToOption)
            .Where(o => o is not null)
            .Select(o => o!)
            .ToList();

        _cache.Set(cacheKey, (IReadOnlyList<StreamOption>)options, TimeSpan.FromMinutes(_options.CacheMinutes));

        return options;
    }

    private StreamOption? ToOption(StreamCandidate candidate)
    {
        string url;
        try
        {
            url = !string.IsNullOrWhiteSpace(candidate.Stream.Url)
                ? candidate.Stream.Url!
                : _stremioServer.BuildStreamUrl(candidate.Stream);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var release = candidate.ReleaseName.Length > 48
            ? candidate.ReleaseName[..48].TrimEnd() + "…"
            : candidate.ReleaseName;

        var name = string.Join(
            " · ",
            new[]
            {
                candidate.Resolution ?? "unknown",
                $"{candidate.Seeders} seeders",
                candidate.SizeGb > 0 ? $"{candidate.SizeGb:0.##} GB" : null,
                release,
            }.Where(part => !string.IsNullOrWhiteSpace(part)));

        var id = $"{candidate.Stream.InfoHash ?? "direct"}:{candidate.Stream.FileIdx?.ToString(CultureInfo.InvariantCulture) ?? "-1"}";

        return new StreamOption(id, name, url, GuessContainer(candidate), candidate);
    }

    /// <summary>
    /// Declaring the container spares Jellyfin an ffprobe of the remote stream, which on a
    /// torrent means starting a download just to identify the file.
    /// </summary>
    private static string GuessContainer(StreamCandidate candidate)
    {
        var source = candidate.Stream.BehaviorHints?.Filename ?? candidate.ReleaseName;
        var extension = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();

        return extension is "mkv" or "mp4" or "avi" or "m4v" or "ts" or "webm"
            ? extension
            : "mkv";
    }

    /// <summary>Full ranked candidate list, for the ?debug=1 view.</summary>
    public async Task<IReadOnlyList<StreamCandidate>> DescribeAsync(
        string type,
        string stremioId,
        CancellationToken cancellationToken)
    {
        var expected = await GetExpectedTitleAsync(type, stremioId, cancellationToken).ConfigureAwait(false);
        var streams = await _torrentio.GetStreamsAsync(type, stremioId, cancellationToken).ConfigureAwait(false);
        return _selector.Rank(streams, IsMovie(type), expected);
    }

    private static bool IsMovie(string type) => type.Equals("movie", StringComparison.OrdinalIgnoreCase);
}
