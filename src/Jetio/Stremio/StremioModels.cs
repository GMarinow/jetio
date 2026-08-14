using System.Text.Json.Serialization;

namespace Jetio.Stremio;

public sealed class StreamsResponse
{
    [JsonPropertyName("streams")]
    public List<TorrentioStream> Streams { get; set; } = new();
}

public sealed class TorrentioStream
{
    /// <summary>Short label, e.g. "Torrentio\n4k HDR". Carries the quality tags.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Release name plus "👤 seeders 💾 size ⚙️ tracker" annotations.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("infoHash")]
    public string? InfoHash { get; set; }

    [JsonPropertyName("fileIdx")]
    public int? FileIdx { get; set; }

    /// <summary>Set only when a debrid service is configured; then it is directly playable.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Entries like "tracker:udp://..." and "dht:&lt;hash&gt;".</summary>
    [JsonPropertyName("sources")]
    public List<string>? Sources { get; set; }

    [JsonPropertyName("behaviorHints")]
    public StreamBehaviorHints? BehaviorHints { get; set; }
}

public sealed class StreamBehaviorHints
{
    [JsonPropertyName("bingeGroup")]
    public string? BingeGroup { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }
}

public sealed class CinemetaCatalogResponse
{
    [JsonPropertyName("metas")]
    public List<CinemetaMeta> Metas { get; set; } = new();
}

public sealed class CinemetaMetaResponse
{
    [JsonPropertyName("meta")]
    public CinemetaMeta? Meta { get; set; }
}

public sealed class CinemetaMeta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Either "2008" or a range like "2008–2013".</summary>
    [JsonPropertyName("releaseInfo")]
    public string? ReleaseInfo { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    /// <summary>Cinemeta sends both spellings; "genres" is the newer one.</summary>
    [JsonPropertyName("genres")]
    public List<string>? Genres { get; set; }

    [JsonPropertyName("genre")]
    public List<string>? Genre { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("videos")]
    public List<CinemetaVideo>? Videos { get; set; }
}

public sealed class CinemetaVideo
{
    /// <summary>Stremio video id, e.g. "tt0903747:1:1".</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("season")]
    public int Season { get; set; }

    [JsonPropertyName("episode")]
    public int Episode { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("released")]
    public DateTimeOffset? Released { get; set; }
}
