using System.Globalization;
using System.Text;

namespace Jetio.Streaming;

/// <summary>
/// Builds the playlists a player reads. Everything here is computed from the duration alone —
/// no part of the release has to be read first, so playback starts without waiting.
///
/// Segment boundaries are nominal. Copying a stream rather than re-encoding it means ffmpeg cuts
/// at the nearest keyframe instead of exactly on the second, so real segments drift slightly from
/// the durations declared here. Players tolerate that, and Jellyfin's own remuxing path makes the
/// same trade — it is the reason seeking can work at all without re-encoding the video.
/// </summary>
public static class HlsPlaylists
{
    /// <summary>
    /// Six seconds balances two costs: every segment is a fresh seek into the torrent, so short
    /// segments mean more of them, while long ones make the player wait longer after a scrub.
    /// </summary>
    public const int SegmentSeconds = 6;

    public const string PlaylistContentType = "application/vnd.apple.mpegurl";

    public static int SegmentCount(TimeSpan duration) =>
        Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds / SegmentSeconds));

    /// <summary>
    /// The entry point a player is handed. It names the subtitle renditions and points at the
    /// video playlist — subtitles have to be advertised here rather than inside the video
    /// playlist, or the player never learns they exist.
    /// </summary>
    public static string BuildMaster(string baseUrl, IReadOnlyList<SubtitleTrack> tracks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#EXTM3U");
        builder.AppendLine("#EXT-X-VERSION:3");

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];

            builder.Append(CultureInfo.InvariantCulture, $"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\"");
            builder.Append(CultureInfo.InvariantCulture, $",NAME=\"{Escape(track.Title)}\"");

            if (track.Language is not null)
            {
                builder.Append(CultureInfo.InvariantCulture, $",LANGUAGE=\"{track.Language}\"");
            }

            // Exactly one rendition may be DEFAULT, or players pick unpredictably.
            builder.Append(CultureInfo.InvariantCulture, $",DEFAULT={(i == 0 ? "YES" : "NO")},AUTOSELECT=YES");
            builder.AppendLine(CultureInfo.InvariantCulture, $",URI=\"{baseUrl}/subtitles/{i}.m3u8\"");
        }

        // BANDWIDTH is required by the spec even when there is only one rendition to choose from.
        builder.Append(CultureInfo.InvariantCulture, $"#EXT-X-STREAM-INF:BANDWIDTH=8000000");

        if (tracks.Count > 0)
        {
            builder.Append(",SUBTITLES=\"subs\"");
        }

        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"{baseUrl}/video.m3u8");
        return builder.ToString();
    }

    /// <summary>
    /// The whole film, declared up front. VOD with an ENDLIST is what makes a player treat the
    /// timeline as complete and allow a scrub to anywhere — the property the muxed Matroska
    /// stream could never have, because its length was unknowable while it was being written.
    /// </summary>
    public static string BuildVideo(string baseUrl, TimeSpan duration)
    {
        var count = SegmentCount(duration);
        var remainder = duration.TotalSeconds - ((count - 1) * (double)SegmentSeconds);

        var builder = new StringBuilder();
        builder.AppendLine("#EXTM3U");
        builder.AppendLine("#EXT-X-VERSION:3");
        builder.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        builder.AppendLine(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{SegmentSeconds}");
        builder.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");

        for (var i = 0; i < count; i++)
        {
            var length = i == count - 1 ? Math.Max(0.1, remainder) : SegmentSeconds;
            builder.AppendLine(CultureInfo.InvariantCulture, $"#EXTINF:{length:0.000},");
            builder.AppendLine(CultureInfo.InvariantCulture, $"{baseUrl}/{i}.ts");
        }

        builder.AppendLine("#EXT-X-ENDLIST");
        return builder.ToString();
    }

    /// <summary>
    /// One WebVTT file covering the entire film, rather than a segment per interval. Splitting
    /// subtitles gains nothing — the file is a few hundred kilobytes — and a single entry cannot
    /// drift out of step with the video the way many small ones can.
    /// </summary>
    public static string BuildSubtitle(string baseUrl, int index, TimeSpan duration)
    {
        var seconds = Math.Ceiling(duration.TotalSeconds);

        var builder = new StringBuilder();
        builder.AppendLine("#EXTM3U");
        builder.AppendLine("#EXT-X-VERSION:3");
        builder.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        builder.AppendLine(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{seconds:0}");
        builder.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        builder.AppendLine(CultureInfo.InvariantCulture, $"#EXTINF:{seconds:0.000},");
        builder.AppendLine(CultureInfo.InvariantCulture, $"{baseUrl}/subtitles/{index}.vtt");
        builder.AppendLine("#EXT-X-ENDLIST");
        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal);
}
