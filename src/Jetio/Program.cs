using Jetio.Catalog;
using Jetio.Configuration;
using Jetio.Endpoints;
using Jetio.Jellyfin;
using Jetio.Library;
using Jetio.Streaming;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// A bind-mounted override so the container image never has to be rebuilt for a config change.
builder.Configuration.AddJsonFile("/config/jetio.json", optional: true, reloadOnChange: true);

// Re-append the environment variables. Configuration is last-one-wins, so without this
// the file above would silently outrank anything docker-compose passes in — including
// the host addresses, which are the one thing that must not come from a checked-in sample.
builder.Configuration.AddEnvironmentVariables();

// .NET binds arrays from indexed keys (…LibraryNames__0), which is miserable to write in a
// .env file. Accept a comma-separated list and expand it into the indexed form.
var libraryNames = builder.Configuration["JELLYFIN_LIBRARY_NAMES"];
if (!string.IsNullOrWhiteSpace(libraryNames))
{
    builder.Configuration.AddInMemoryCollection(
        libraryNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((name, index) =>
                new KeyValuePair<string, string?>($"Jetio:Jellyfin:LibraryNames:{index}", name)));
}

builder.Services.Configure<JetioOptions>(builder.Configuration.GetSection(JetioOptions.SectionName));
builder.Services.AddMemoryCache();

static void Configure(IServiceProvider services, HttpClient http, Func<JetioOptions, int> timeoutSeconds)
{
    var options = services.GetRequiredService<IOptions<JetioOptions>>().Value;
    http.Timeout = TimeSpan.FromSeconds(timeoutSeconds(options));
    http.DefaultRequestHeaders.UserAgent.ParseAdd("jetio/1.0");
}

builder.Services.AddHttpClient<TorrentioClient>((sp, http) => Configure(sp, http, o => o.Torrentio.TimeoutSeconds));
builder.Services.AddHttpClient<CinemetaClient>((sp, http) => Configure(sp, http, _ => 30));
builder.Services.AddHttpClient<StremioServerClient>((sp, http) => Configure(sp, http, o => o.StremioServer.TimeoutSeconds));
builder.Services.AddHttpClient<JellyfinClient>((sp, http) => Configure(sp, http, _ => 30));
builder.Services.AddHttpClient<TmdbCatalogSource>((sp, http) => Configure(sp, http, _ => 30));
builder.Services.AddHttpClient<TraktCatalogSource>((sp, http) => Configure(sp, http, _ => 30));

builder.Services.AddSingleton<StreamSelector>();
builder.Services.AddSingleton<StrmLibraryWriter>();
builder.Services.AddSingleton<SyncState>();
builder.Services.AddSingleton<ManagedLibraryStore>();
builder.Services.AddSingleton<MediaAnalysisQueue>();

builder.Services.AddSingleton<SubtitleLocator>();
builder.Services.AddSingleton<MediaProbe>();
builder.Services.AddSingleton<FfmpegRunner>();
builder.Services.AddSingleton<HlsStreamer>();
builder.Services.AddSingleton<SubtitleDelivery>();

builder.Services.AddScoped<StreamResolver>();
builder.Services.AddScoped<LibrarySynchronizer>();
builder.Services.AddScoped<ManagedLibraryService>();

builder.Services.AddScoped<ICatalogSource, ManagedLibraryCatalogSource>();
builder.Services.AddScoped<ICatalogSource, CinemetaCatalogSource>();
builder.Services.AddScoped<ICatalogSource, WatchlistFileCatalogSource>();
builder.Services.AddScoped<ICatalogSource>(sp => sp.GetRequiredService<TmdbCatalogSource>());
builder.Services.AddScoped<ICatalogSource>(sp => sp.GetRequiredService<TraktCatalogSource>());

builder.Services.AddHostedService<LibrarySyncService>();
builder.Services.AddHostedService<MediaAnalysisService>();

var app = builder.Build();

// Serves the search-and-add UI from wwwroot at the site root.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapJetioEndpoints();
app.MapHls();
app.MapLibraryApi();
app.MapBrowseApi();
app.MapPluginRepository();

app.Services.GetRequiredService<StrmLibraryWriter>().EnsureRootsExist();
WarnAboutUnreachableBaseUrl(app);

app.Run();

// Every .strm file embeds PublicBaseUrl. If it resolves to loopback, playback breaks for
// every device except the one jetio runs on — and it breaks at play time, not at sync time.
static void WarnAboutUnreachableBaseUrl(WebApplication app)
{
    var options = app.Services.GetRequiredService<IOptions<JetioOptions>>().Value;
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Jetio.Startup");

    if (Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback)
    {
        logger.LogWarning(
            "PublicBaseUrl is {Url}, which only works if Jellyfin and its players run on this same host. "
            + "Set it to a LAN address reachable from Jellyfin.",
            options.PublicBaseUrl);
    }

    logger.LogInformation(
        "jetio ready. Library root {LibraryRoot}, public base {PublicBaseUrl}, streaming server {StremioServer}",
        options.LibraryRoot,
        options.PublicBaseUrl,
        options.StremioServer.BaseUrl);
}
