using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jetio;

/// <summary>
/// Attaches every Torrentio release for an item as a selectable version, so the user picks
/// the release in Jellyfin's own player UI instead of jetio choosing one at play time.
/// Jellyfin discovers this class automatically — no registration required.
/// </summary>
public class JetioMediaSourceProvider : IMediaSourceProvider
{
    private readonly JetioClient _jetio;
    private readonly ILogger<JetioMediaSourceProvider> _logger;

    public JetioMediaSourceProvider(JetioClient jetio, ILogger<JetioMediaSourceProvider> logger)
    {
        _jetio = jetio;
        _logger = logger;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.Enabled)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        // This runs for every item Jellyfin asks about, including real media files.
        // Only jetio's own .strm entries should trigger a Torrentio lookup.
        if (string.IsNullOrEmpty(item.Path)
            || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var candidates = item switch
        {
            Movie movie => await GetMovieSourcesAsync(movie, cancellationToken).ConfigureAwait(false),
            Episode episode => await GetEpisodeSourcesAsync(episode, cancellationToken).ConfigureAwait(false),
            _ => Array.Empty<JetioCandidate>(),
        };

        if (candidates.Count == 0)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        _logger.LogDebug("jetio offered {Count} versions for {Item}", candidates.Count, item.Name);

        return candidates
            .Select(candidate => ToMediaSource(candidate, item, configuration.ServeThroughJellyfin))
            .ToList();
    }

    private async Task<IReadOnlyList<JetioCandidate>> GetMovieSourcesAsync(
        Movie movie,
        CancellationToken cancellationToken)
    {
        var imdbId = movie.GetProviderId(MetadataProvider.Imdb);
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return Array.Empty<JetioCandidate>();
        }

        return await _jetio.GetMovieCandidatesAsync(imdbId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<JetioCandidate>> GetEpisodeSourcesAsync(
        Episode episode,
        CancellationToken cancellationToken)
    {
        // Torrentio keys episodes off the *series* IMDb id plus season/episode numbers.
        var imdbId = episode.Series?.GetProviderId(MetadataProvider.Imdb);
        var season = episode.ParentIndexNumber;
        var number = episode.IndexNumber;

        if (string.IsNullOrWhiteSpace(imdbId) || season is null || number is null)
        {
            return Array.Empty<JetioCandidate>();
        }

        return await _jetio
            .GetEpisodeCandidatesAsync(imdbId, season.Value, number.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private static MediaSourceInfo ToMediaSource(
        JetioCandidate candidate,
        BaseItem item,
        bool serveThroughJellyfin) => new()
    {
        Id = BuildId(candidate, item),
        Name = candidate.Name,
        Path = candidate.Url,
        Protocol = MediaProtocol.Http,
        Container = candidate.Container,
        IsRemote = true,
        // Refusing direct play pushes Jellyfin to remux, which keeps it in the data path so it
        // can deliver external subtitles. Video and audio are still copied, not re-encoded.
        SupportsDirectPlay = !serveThroughJellyfin,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        SupportsProbing = false,
        RequiresOpening = false,
        RequiresClosing = false,
        IsInfiniteStream = false,
        MediaStreams = [],
        MediaAttachments = [],
    };

    /// <summary>
    /// Jellyfin expects an opaque, stable id per source. Deriving it from the info hash keeps
    /// a user's chosen version selected across restarts, as long as the release still exists.
    /// </summary>
    private static string BuildId(JetioCandidate candidate, BaseItem item)
    {
        var bytes = Encoding.UTF8.GetBytes($"jetio:{item.Id}:{candidate.Id}");
        return Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant();
    }

    public Task<ILiveStream> OpenMediaSource(
        string openToken,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "jetio sources are plain HTTP URLs and set RequiresOpening=false, so they are never opened.");
}
