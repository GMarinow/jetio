using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
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
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<JetioMediaSourceProvider> _logger;

    public JetioMediaSourceProvider(
        JetioClient jetio,
        IMediaSourceManager mediaSourceManager,
        ILogger<JetioMediaSourceProvider> logger)
    {
        _jetio = jetio;
        _mediaSourceManager = mediaSourceManager;
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

        var subtitles = GetExternalSubtitleStreams(item);

        _logger.LogDebug(
            "jetio offered {Count} versions for {Item}, each carrying {Subtitles} external subtitle(s)",
            candidates.Count,
            item.Name,
            subtitles.Count);

        return candidates
            .Select(candidate => ToMediaSource(
                candidate,
                item,
                configuration.ServeThroughJellyfin,
                CopySubtitleStreams(subtitles)))
            .ToList();
    }

    /// <summary>
    /// The subtitle files sitting next to the .strm — fetched by Subbuzz or Open Subtitles, or
    /// dropped in by hand. Jellyfin indexes those against the *item*, and only attaches them to
    /// the source it builds from the .strm itself. The sources this plugin returns are separate,
    /// so unless they are copied across, picking a release from the version picker leaves the
    /// player with no subtitle tracks to offer at all.
    ///
    /// External only, deliberately: an embedded track belongs to whichever release the .strm
    /// currently resolves to, and its index means nothing inside a different release's container.
    /// </summary>
    private IReadOnlyList<MediaStream> GetExternalSubtitleStreams(BaseItem item) =>
        _mediaSourceManager
            .GetMediaStreams(new MediaStreamQuery { ItemId = item.Id, Type = MediaStreamType.Subtitle })
            .Where(stream => stream.IsExternal && !string.IsNullOrEmpty(stream.Path))
            .ToList();

    /// <summary>
    /// Every source needs its own copies. Jellyfin writes the delivery method and URL onto these
    /// objects while answering a playback request, and the URL carries the source id — so sharing
    /// instances would leave all versions pointing at whichever one was processed last.
    /// </summary>
    private static List<MediaStream> CopySubtitleStreams(IReadOnlyList<MediaStream> streams) =>
        streams
            .Select((stream, index) => new MediaStream
            {
                // Numbered from zero because these sources carry nothing else. The index is what
                // Jellyfin puts in the subtitle URL and looks straight back up on this source.
                Index = index,
                Type = MediaStreamType.Subtitle,
                Codec = stream.Codec,
                Path = stream.Path,
                IsExternal = true,
                SupportsExternalStream = true,
                Language = stream.Language,
                Title = stream.Title,
                IsDefault = stream.IsDefault,
                IsForced = stream.IsForced,
                IsHearingImpaired = stream.IsHearingImpaired,
            })
            .ToList();

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
        bool serveThroughJellyfin,
        List<MediaStream> subtitleStreams) => new()
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
        MediaStreams = subtitleStreams,
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
