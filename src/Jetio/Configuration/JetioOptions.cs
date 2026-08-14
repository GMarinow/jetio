namespace Jetio.Configuration;

public sealed class JetioOptions
{
    public const string SectionName = "Jetio";

    /// <summary>Root of the .strm tree. Jellyfin must be able to read this path.</summary>
    public string LibraryRoot { get; set; } = "/library";

    public string MoviesFolderName { get; set; } = "Movies";

    public string SeriesFolderName { get; set; } = "Shows";

    /// <summary>
    /// Children's titles are written to {LibraryRoot}/{KidsFolderName}/Movies and /Shows so they
    /// can be separate Jellyfin libraries with their own access. Empty disables the split.
    /// </summary>
    public string KidsFolderName { get; set; } = "Kids";

    /// <summary>
    /// Cinemeta genres that route a title into the Kids folders. Animation only by default:
    /// "Family" sweeps in plenty of live-action drama that is not what anyone means by kids' TV.
    /// </summary>
    public List<string> KidsGenres { get; set; } = new() { "Animation" };

    /// <summary>
    /// Base URL that Jellyfin (and its clients) use to reach jetio. This is baked into every
    /// .strm file, so it must be a stable LAN address, not localhost.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:9000";

    /// <summary>Write season 0 (specials) entries.</summary>
    public bool IncludeSpecials { get; set; }

    /// <summary>Write episodes whose air date is in the future.</summary>
    public bool IncludeUnairedEpisodes { get; set; }

    /// <summary>Delete .strm entries that no longer appear in any catalog.</summary>
    public bool PruneRemovedItems { get; set; } = true;

    public TorrentioOptions Torrentio { get; set; } = new();

    public StremioServerOptions StremioServer { get; set; } = new();

    public StreamSelectionOptions StreamSelection { get; set; } = new();

    public CatalogOptions Catalogs { get; set; } = new();

    public JellyfinOptions Jellyfin { get; set; } = new();

    public SyncOptions Sync { get; set; } = new();
}

public sealed class TorrentioOptions
{
    public string BaseUrl { get; set; } = "https://torrentio.strem.fun";

    /// <summary>
    /// Optional Torrentio config segment, exactly as it appears in a Stremio install URL —
    /// e.g. "providers=yts,eztv|sort=qualitysize". Leave empty for defaults.
    /// </summary>
    public string? Configuration { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class StremioServerOptions
{
    /// <summary>Base URL of the Stremio streaming server (the stremio/server container).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:11470";

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Extra trackers appended as ?tr= to help cold torrents find peers faster.</summary>
    public List<string> ExtraTrackers { get; set; } = new();
}

public sealed class StreamSelectionOptions
{
    /// <summary>Ordered resolution preference; earlier entries win.</summary>
    public List<string> ResolutionPriority { get; set; } = new() { "1080p", "720p", "2160p", "480p" };

    public int MinSeeders { get; set; } = 3;

    public double MaxSizeGb { get; set; } = 20d;

    public double MinSizeGb { get; set; } = 0.2d;

    /// <summary>Regexes matched against the release title; any hit disqualifies the stream.</summary>
    public List<string> ExcludePatterns { get; set; } = new()
    {
        @"\b(CAM|HDCAM|HDTS|TELESYNC|TELECINE|SCREENER|SCR)\b",
    };

    /// <summary>Regexes that boost a stream's score when matched against the release title.</summary>
    public List<string> PreferPatterns { get; set; } = new();

    /// <summary>
    /// Applied to movies only. Collections and multi-film packs often out-seed the single
    /// release, and picking one plays whichever file the engine guesses at at random.
    /// Series packs are left alone — Torrentio pins the episode with fileIdx and a filename.
    /// </summary>
    public List<string> MoviePackExcludePatterns { get; set; } = new()
    {
        // Non-English release groups spell these out too, hence the loose suffixes.
        @"\b(pack|collect\w*|colecc\w*|collez\w*|trilog\w*|duolog\w*|quadrilog\w*|antholog\w*|saga|boxset|box\s?set)\b",
        @"\b(imdb\s*top|top\s*\d{2,3})\b",
        @"\b\d+\s*(movies|films)\b",
        @"\b\d\s*-\s*\d\b",
    };

    /// <summary>
    /// Reject releases whose name does not match the title being requested. Torrentio
    /// occasionally indexes a completely different film against an IMDb id, and for
    /// unreleased titles that mislabelled copy outranks every genuine cam.
    /// </summary>
    public bool VerifyTitles { get; set; } = true;

    /// <summary>How long a resolved stream URL is reused before Torrentio is queried again.</summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary>
    /// How many releases the Jellyfin plugin offers in its version picker. Torrentio returns
    /// ~50 per title; surfacing all of them makes the dropdown useless.
    /// </summary>
    public int MaxSourcesExposed { get; set; } = 10;
}

public sealed class CatalogOptions
{
    public ManagedCatalogOptions Managed { get; set; } = new();

    public CinemetaCatalogOptions Cinemeta { get; set; } = new();

    public TmdbCatalogOptions Tmdb { get; set; } = new();

    public WatchlistCatalogOptions Watchlist { get; set; } = new();

    public TraktCatalogOptions Trakt { get; set; } = new();
}

/// <summary>Titles added through the jetio web UI. This is the list you curate by hand.</summary>
public sealed class ManagedCatalogOptions
{
    public bool Enabled { get; set; } = true;

    public string Path { get; set; } = "/config/library.json";
}

public sealed class CinemetaCatalogOptions
{
    /// <summary>
    /// Off by default. This catalog writes ~1700 .strm files of titles you did not choose;
    /// the web UI is the intended way to fill the library. Turn it on deliberately.
    /// </summary>
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://v3-cinemeta.strem.io";

    /// <summary>Catalog paths, e.g. "movie/top", "series/top", "movie/top/genre=Action".</summary>
    public List<string> Catalogs { get; set; } = new() { "movie/top", "series/top" };

    public int MaxItemsPerCatalog { get; set; } = 50;
}

public sealed class TmdbCatalogOptions
{
    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3";

    /// <summary>TMDB list paths, e.g. "movie/popular", "trending/movie/week", "tv/popular".</summary>
    public List<string> Lists { get; set; } = new() { "movie/popular", "tv/popular" };

    public int MaxItemsPerList { get; set; } = 40;

    public string Language { get; set; } = "en-US";
}

public sealed class WatchlistCatalogOptions
{
    /// <summary>Off by default; the managed library replaces it for most people.</summary>
    public bool Enabled { get; set; }

    /// <summary>Plain text file, one entry per line. See config/watchlist.txt.</summary>
    public string Path { get; set; } = "/config/watchlist.txt";
}

public sealed class TraktCatalogOptions
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://api.trakt.tv";

    /// <summary>Trakt application client id. Required.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth access token. Only needed for private lists or "me" shorthand.</summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Trakt list paths, e.g. "users/me/watchlist/movies", "users/me/watchlist/shows",
    /// "users/someone/lists/my-list/items/movies".
    /// </summary>
    public List<string> Lists { get; set; } = new()
    {
        "users/me/watchlist/movies",
        "users/me/watchlist/shows",
    };

    public int MaxItemsPerList { get; set; } = 100;
}

public sealed class JellyfinOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8096";

    /// <summary>Jellyfin API key. Without it jetio cannot trigger a library scan.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool TriggerRefresh { get; set; } = true;

    /// <summary>
    /// Names of the Jellyfin libraries backed by jetio, exactly as they appear in Jellyfin.
    /// Only these get rescanned. Leave empty and every library is rescanned instead, which is
    /// slow on a server that also holds a real media collection.
    /// </summary>
    public List<string> LibraryNames { get; set; } = new();

    /// <summary>
    /// After a title is added through the web UI, ask Jellyfin to analyse it once.
    ///
    /// Without this Jellyfin never looks inside the stream, so it has no container, codec or
    /// bitrate to reason about — it cannot judge whether a client can play the file, and falls
    /// back to handing over the URL. Analysing once also surfaces any subtitle tracks embedded
    /// in the release, which clients render far more reliably than external ones.
    ///
    /// Costs one ffprobe per added title, which pulls the opening chunk of the torrent.
    /// Deliberately NOT done during bulk catalog syncs — that would start every title at once.
    /// </summary>
    public bool AnalyzeAddedItems { get; set; } = true;

    /// <summary>How long to keep looking for a newly added item before giving up.</summary>
    public int AnalyzeMaxAttempts { get; set; } = 12;

    /// <summary>Gap between those attempts, while Jellyfin finishes its scan.</summary>
    public int AnalyzeDelaySeconds { get; set; } = 5;
}

public sealed class SyncOptions
{
    public bool RunOnStartup { get; set; } = true;

    public int IntervalHours { get; set; } = 12;

    /// <summary>Parallel Cinemeta metadata lookups during a sync.</summary>
    public int MaxConcurrency { get; set; } = 4;
}
