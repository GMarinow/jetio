using System.Threading.Channels;

namespace Jetio.Library;

/// <param name="ImdbId">Title to analyse, once Jellyfin has scanned it.</param>
public sealed record AnalysisRequest(string ImdbId, string? Name);

/// <summary>
/// Hands newly added titles to <see cref="MediaAnalysisService"/>. Analysis waits on a Jellyfin
/// library scan, which takes far longer than anyone should be left staring at an Add button.
/// </summary>
public sealed class MediaAnalysisQueue
{
    private readonly Channel<AnalysisRequest> _channel =
        Channel.CreateUnbounded<AnalysisRequest>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<AnalysisRequest> Reader => _channel.Reader;

    public void Enqueue(AnalysisRequest request) => _channel.Writer.TryWrite(request);
}
