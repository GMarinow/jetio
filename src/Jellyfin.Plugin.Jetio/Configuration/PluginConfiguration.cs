using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jetio.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Base URL of the jetio service. Jellyfin queries it for releases, and the URLs it
    /// returns are handed to playback devices, so it must be a LAN address.
    /// </summary>
    public string JetioBaseUrl { get; set; } = "http://192.168.1.10:9000";

    /// <summary>Turns the version picker off without uninstalling the plugin.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Route playback through Jellyfin instead of letting the client fetch the stream directly.
    ///
    /// Jellyfin then remuxes — it repackages the container while copying video and audio
    /// untouched, so there is no re-encoding — which puts it back in the data path. That matters
    /// for external subtitles: several clients, the Android TV app among them, fail to side-load
    /// a subtitle onto a stream Jellyfin never touched, and show the track without rendering it.
    ///
    /// Costs: video travels via Jellyfin rather than straight to the player, and seeking is
    /// slower because the mux restarts on each jump. Off by default.
    /// </summary>
    public bool ServeThroughJellyfin { get; set; }

    /// <summary>
    /// Torrentio lookups happen while the user waits on the item page, so this is kept
    /// short deliberately — a slow answer is worse than no extra versions.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 20;
}
