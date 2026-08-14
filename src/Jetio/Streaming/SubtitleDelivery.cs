using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Streaming;

/// <summary>
/// Decides whether a title is served through jetio or handed straight to the streaming server,
/// and in what order its subtitles are offered.
///
/// Only titles with subtitle files go through jetio. Everything else keeps the original
/// behaviour — a redirect, with jetio out of the data path entirely — so the cost is paid per
/// title rather than across the library.
/// </summary>
public sealed class SubtitleDelivery
{
    private readonly SubtitleLocator _locator;
    private readonly SubtitleOptions _options;

    public SubtitleDelivery(SubtitleLocator locator, IOptions<JetioOptions> options)
    {
        _locator = locator;
        _options = options.Value.Subtitles;
    }

    public IReadOnlyList<SubtitleTrack> For(TitleRef title)
    {
        var tracks = title.Season is null || title.Episode is null
            ? _locator.ForMovie(title.ImdbId)
            : _locator.ForEpisode(title.ImdbId, title.Season.Value, title.Episode.Value);

        return Order(NameUnknownLanguages(tracks));
    }

    /// <summary>
    /// A file with no language suffix would otherwise be advertised as undetermined, and players
    /// will not switch a text track on by themselves when they cannot tell what language it is —
    /// ExoPlayer needs a language to match against before it will enable one. Falling back to the
    /// configured preference is far better than leaving the track unusable, since a library whose
    /// subtitles are all one language is the normal case.
    /// </summary>
    private IReadOnlyList<SubtitleTrack> NameUnknownLanguages(IReadOnlyList<SubtitleTrack> tracks)
    {
        if (string.IsNullOrWhiteSpace(_options.DefaultLanguage))
        {
            return tracks;
        }

        var fallback = SubtitleLocator.ExtractLanguage($"x.{_options.DefaultLanguage}.srt");

        if (fallback is null)
        {
            return tracks;
        }

        return tracks
            .Select(t => t.Language is null
                ? t with { Language = fallback, Title = SubtitleLocator.DescribeLanguage(fallback) }
                : t)
            .ToList();
    }

    /// <summary>
    /// What the playlist offers as selectable tracks. Nothing, when subtitles are drawn into the
    /// picture — the viewer would otherwise be given a track that paints a second copy on top.
    /// </summary>
    public IReadOnlyList<SubtitleTrack> AdvertisedTracks(TitleRef title) =>
        _options.BurnIn ? Array.Empty<SubtitleTrack>() : For(title);

    public bool ShouldServeThroughJetio(IReadOnlyList<SubtitleTrack> tracks) =>
        _options.MuxIntoStream && tracks.Count > 0;

    /// <summary>
    /// The preferred language is moved to the front, because the playlist marks the first
    /// rendition as the default one and only one rendition may claim that.
    /// </summary>
    private IReadOnlyList<SubtitleTrack> Order(IReadOnlyList<SubtitleTrack> tracks)
    {
        if (string.IsNullOrWhiteSpace(_options.DefaultLanguage) || tracks.Count < 2)
        {
            return tracks;
        }

        var preferred = SubtitleLocator.ExtractLanguage($"x.{_options.DefaultLanguage}.srt")
            ?? _options.DefaultLanguage;

        return tracks
            .OrderBy(t => string.Equals(t.Language, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }
}
