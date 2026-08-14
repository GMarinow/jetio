using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Endpoints;

/// <summary>
/// Serves a Jellyfin plugin repository from jetio itself, so the plugin can be installed and
/// updated from Jellyfin's dashboard rather than by copying a dll onto the server.
///
/// The manifest is generated per request because it has to embed an absolute URL back to this
/// service, which is only known from configuration at runtime.
/// </summary>
public static partial class PluginRepositoryEndpoints
{
    private const string PluginGuid = "b8f3d2a1-7c4e-4f89-9a2b-6d5e3c1f8a70";

    public static void MapPluginRepository(this WebApplication app)
    {
        app.MapGet("/plugin/manifest.json", GetManifestAsync);
    }

    private static async Task<IResult> GetManifestAsync(
        IWebHostEnvironment environment,
        IOptions<JetioOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Jetio.PluginRepository");
        var directory = Path.Combine(environment.WebRootPath ?? "wwwroot", "plugin");

        if (!Directory.Exists(directory))
        {
            logger.LogWarning("No plugin directory at {Path}; serving an empty repository", directory);
            return Results.Json(Array.Empty<object>());
        }

        var baseUrl = options.Value.PublicBaseUrl.TrimEnd('/');
        var versions = new List<object>();

        foreach (var file in Directory.EnumerateFiles(directory, "jellyfin-plugin-jetio-*.zip"))
        {
            var match = VersionRegex().Match(Path.GetFileName(file));
            if (!match.Success)
            {
                continue;
            }

            var checksum = await ComputeMd5Async(file, cancellationToken).ConfigureAwait(false);

            versions.Add(new
            {
                version = match.Groups[1].Value,
                changelog = "See the project README.",
                targetAbi = "10.11.0.0",
                sourceUrl = $"{baseUrl}/plugin/{Uri.EscapeDataString(Path.GetFileName(file))}",
                checksum,
                timestamp = File.GetLastWriteTimeUtc(file).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            });
        }

        if (versions.Count == 0)
        {
            logger.LogWarning("No plugin packages found in {Path}", directory);
        }

        return Results.Json(new[]
        {
            new
            {
                guid = PluginGuid,
                name = "jetio",
                description = "Offers Torrentio releases as selectable versions in the Jellyfin player.",
                overview = "Adds a version picker to items in your jetio library.",
                owner = "jetio",
                category = "General",
                imageUrl = string.Empty,
                versions,
            },
        });
    }

    /// <summary>Jellyfin verifies packages with MD5, so the algorithm is not a choice here.</summary>
    private static async Task<string> ComputeMd5Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [GeneratedRegex(@"^jellyfin-plugin-jetio-(\d+\.\d+\.\d+\.\d+)\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
