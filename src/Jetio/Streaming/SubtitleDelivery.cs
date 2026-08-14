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

        return Order(tracks);
    }

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
