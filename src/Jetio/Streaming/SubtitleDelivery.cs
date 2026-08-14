using System.Globalization;
using Jetio.Configuration;
using Jetio.Stremio;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Jetio.Streaming;

/// <summary>
/// Serves a release with its subtitle files muxed in, and translates between how players seek
/// and how ffmpeg seeks.
///
/// A player treats this as a progressive download and seeks by byte offset. ffmpeg can only be
/// told a time. There is no exact conversion for a variable-bitrate file, so the offset is placed
/// proportionally along the timeline — accurate enough to land within a few seconds, and the
/// reason this only engages for titles that actually have subtitles to add.
/// </summary>
public sealed class SubtitleDelivery
{
    private readonly SubtitleLocator _locator;
    private readonly MediaProbe _probe;
    private readonly SubtitleMuxer _muxer;
    private readonly SubtitleOptions _options;
    private readonly ILogger<SubtitleDelivery> _logger;

    public SubtitleDelivery(
        SubtitleLocator locator,
        MediaProbe probe,
        SubtitleMuxer muxer,
        IOptions<JetioOptions> options,
        ILogger<SubtitleDelivery> logger)
    {
        _locator = locator;
        _probe = probe;
        _muxer = muxer;
        _options = options.Value.Subtitles;
        _logger = logger;
    }

    public IReadOnlyList<SubtitleTrack> ForMovie(string imdbId) => _locator.ForMovie(imdbId);

    public IReadOnlyList<SubtitleTrack> ForEpisode(string imdbId, int season, int episode) =>
        _locator.ForEpisode(imdbId, season, episode);

    public bool ShouldMux(IReadOnlyList<SubtitleTrack> tracks) =>
        _options.MuxIntoStream && tracks.Count > 0;

    public async Task<IResult> StreamAsync(
        ResolvedStream resolved,
        IReadOnlyList<SubtitleTrack> tracks,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // Duration and size are what make a byte offset meaningful. Without them the stream is
        // still served, but seeking is declined rather than sent to the wrong place.
        var metrics = await _probe.GetAsync(resolved.Url, cancellationToken).ConfigureAwait(false);
        var requestedStart = ParseRangeStart(context.Request);

        var response = context.Response;
        response.ContentType = SubtitleMuxer.ContentType;
        response.Headers[HeaderNames.AcceptRanges] = metrics is null ? "none" : "bytes";

        var seek = TimeSpan.Zero;

        if (metrics is not null && requestedStart is { } start)
        {
            if (start >= metrics.Size)
            {
                response.Headers[HeaderNames.ContentRange] = $"bytes */{metrics.Size}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            seek = metrics.Duration * ((double)start / metrics.Size);

            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers[HeaderNames.ContentRange] =
                $"bytes {start}-{metrics.Size - 1}/{metrics.Size}";
        }

        // Content-Length is deliberately absent. The muxed container is a slightly different size
        // from the release it was built from, and announcing a length that the body then misses
        // fails the request outright — chunked leaves the player to read until the stream ends.
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return Results.Empty;
        }

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        _logger.LogInformation(
            "Muxing {Count} subtitle(s) into {Release}{Seek}",
            tracks.Count,
            resolved.Candidate.ReleaseName,
            seek > TimeSpan.Zero
                ? string.Create(CultureInfo.InvariantCulture, $" from {seek:hh\\:mm\\:ss}")
                : string.Empty);

        var request = new MuxRequest(resolved.Url, resolved.Container, tracks, seek);

        await _muxer.StreamAsync(request, response.Body, cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }

    /// <summary>
    /// Only the start of the first range is honoured. Players ask for an open-ended "from here on"
    /// when seeking a progressive stream, and a multi-range request has no sensible answer from a
    /// container being generated as it is sent.
    /// </summary>
    internal static long? ParseRangeStart(HttpRequest request)
    {
        var header = request.Headers[HeaderNames.Range].ToString();

        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var first = header["bytes=".Length..].Split(',')[0];
        var dash = first.IndexOf('-');

        if (dash <= 0)
        {
            // A suffix range ("-500") counts back from the end, which cannot be placed on a
            // stream whose final size is not yet known.
            return null;
        }

        return long.TryParse(
            first[..dash],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var start) && start >= 0
            ? start
            : null;
    }
}
