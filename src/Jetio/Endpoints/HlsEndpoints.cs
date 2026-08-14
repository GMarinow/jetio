using System.Globalization;
using Jetio.Configuration;
using Jetio.Streaming;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

namespace Jetio.Endpoints;

/// <summary>
/// Serves a title as HLS, which is what makes seeking work.
///
/// A single long stream cannot be scrubbed: ffmpeg writes the duration and seek index by going
/// back over its own output, and it is writing to a pipe. A playlist has no such problem — the
/// whole film is declared up front from the duration alone, and jumping to two thirds of the way
/// through is a request for a different segment rather than a guess into an unindexed container.
/// </summary>
public static class HlsEndpoints
{
    public static void MapHls(this WebApplication app)
    {
        app.MapGet("/hls/{title}/master.m3u8", GetMasterAsync);
        app.MapGet("/hls/{title}/video.m3u8", GetVideoAsync);
        app.MapGet("/hls/{title}/{index:int}.ts", GetSegmentAsync);
        app.MapGet("/hls/{title}/subtitles/{index:int}.m3u8", GetSubtitlePlaylistAsync);
        app.MapGet("/hls/{title}/subtitles/{index:int}.vtt", GetSubtitleAsync);
    }

    /// <summary>Where a player is pointed, and what <c>/resolve</c> hands back.</summary>
    public static string MasterUrl(JetioOptions options, TitleRef title) =>
        $"{BaseUrl(options, title)}/master.m3u8";

    private static string BaseUrl(JetioOptions options, TitleRef title) =>
        $"{options.PublicBaseUrl.TrimEnd('/')}/hls/{title.Slug}";

    private static async Task<IResult> GetMasterAsync(
        string title,
        StreamResolver resolver,
        SubtitleDelivery subtitles,
        IOptions<JetioOptions> options,
        CancellationToken cancellationToken)
    {
        if (TitleRef.Parse(title) is not { } reference)
        {
            return Results.NotFound(new { error = "Unknown title reference" });
        }

        // Resolving here as well as in /resolve costs nothing — it is cached — but it means a
        // player that kept a playlist URL from an earlier session still gets a live stream.
        if (await resolver.ResolveAsync(reference.Type, reference.StremioId, false, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Results.NotFound(new { error = "No playable stream found" });
        }

        // Burned-in subtitles are already in the picture. Advertising them as renditions too
        // would show the viewer a track that draws a second copy over the first.
        var tracks = subtitles.AdvertisedTracks(reference);

        return Results.Text(
            HlsPlaylists.BuildMaster(BaseUrl(options.Value, reference), tracks),
            HlsPlaylists.PlaylistContentType);
    }

    private static async Task<IResult> GetVideoAsync(
        string title,
        StreamResolver resolver,
        MediaProbe probe,
        IOptions<JetioOptions> options,
        CancellationToken cancellationToken)
    {
        if (TitleRef.Parse(title) is not { } reference)
        {
            return Results.NotFound(new { error = "Unknown title reference" });
        }

        var resolved = await resolver
            .ResolveAsync(reference.Type, reference.StremioId, false, cancellationToken)
            .ConfigureAwait(false);

        if (resolved is null)
        {
            return Results.NotFound(new { error = "No playable stream found" });
        }

        // Without a duration there is no segment list, and therefore no playlist to build.
        var metrics = await probe.GetAsync(resolved.Url, cancellationToken).ConfigureAwait(false);

        if (metrics is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Text(
            HlsPlaylists.BuildVideo(BaseUrl(options.Value, reference), metrics.Duration),
            HlsPlaylists.PlaylistContentType);
    }

    private static async Task<IResult> GetSegmentAsync(
        string title,
        int index,
        HttpContext context,
        StreamResolver resolver,
        SubtitleDelivery subtitles,
        HlsStreamer streamer,
        CancellationToken cancellationToken)
    {
        if (TitleRef.Parse(title) is not { } reference || index < 0)
        {
            return Results.NotFound(new { error = "Unknown segment" });
        }

        var resolved = await resolver
            .ResolveAsync(reference.Type, reference.StremioId, false, cancellationToken)
            .ConfigureAwait(false);

        if (resolved is null)
        {
            return Results.NotFound(new { error = "No playable stream found" });
        }

        context.Response.ContentType = HlsStreamer.SegmentContentType;

        await streamer
            .WriteSegmentAsync(resolved, index, subtitles.For(reference), context.Response.Body, cancellationToken)
            .ConfigureAwait(false);

        return Results.Empty;
    }

    private static IResult GetSubtitlePlaylistAsync(
        string title,
        int index,
        SubtitleDelivery subtitles,
        MediaProbe probe,
        StreamResolver resolver,
        IOptions<JetioOptions> options,
        CancellationToken cancellationToken) =>
        TitleRef.Parse(title) is { } reference && index >= 0 && index < subtitles.For(reference).Count
            ? Results.Text(
                HlsPlaylists.BuildSubtitle(
                    BaseUrl(options.Value, reference),
                    index,
                    // The declared length only has to cover the film; the cues carry the timing.
                    TimeSpan.FromHours(6)),
                HlsPlaylists.PlaylistContentType)
            : Results.NotFound(new { error = "Unknown subtitle" });

    private static async Task<IResult> GetSubtitleAsync(
        string title,
        int index,
        HttpContext context,
        SubtitleDelivery subtitles,
        HlsStreamer streamer,
        CancellationToken cancellationToken)
    {
        if (TitleRef.Parse(title) is not { } reference)
        {
            return Results.NotFound(new { error = "Unknown title reference" });
        }

        var tracks = subtitles.For(reference);

        if (index < 0 || index >= tracks.Count)
        {
            return Results.NotFound(new { error = "Unknown subtitle" });
        }

        context.Response.ContentType = HlsStreamer.SubtitleContentType;

        await streamer
            .WriteSubtitleAsync(tracks[index], context.Response.Body, cancellationToken)
            .ConfigureAwait(false);

        return Results.Empty;
    }
}
