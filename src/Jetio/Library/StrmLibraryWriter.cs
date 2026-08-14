using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jetio.Catalog;
using Jetio.Configuration;
using Jetio.Stremio;
using Microsoft.Extensions.Options;

namespace Jetio.Library;

/// <param name="Paths">Every file this title owns, so pruning can keep them.</param>
/// <param name="Changed">How many were actually new or updated this pass.</param>
public sealed record WriteResult(IReadOnlyList<string> Paths, int Changed);

/// <summary>
/// Writes the .strm tree using Jellyfin's own naming conventions, so Jellyfin's built-in
/// scanner supplies artwork and metadata without jetio having to.
/// </summary>
public sealed partial class StrmLibraryWriter
{
    private readonly JetioOptions _options;
    private readonly ILogger<StrmLibraryWriter> _logger;

    public StrmLibraryWriter(IOptions<JetioOptions> options, ILogger<StrmLibraryWriter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string MoviesRoot => Path.Combine(_options.LibraryRoot, _options.MoviesFolderName);

    public string SeriesRoot => Path.Combine(_options.LibraryRoot, _options.SeriesFolderName);

    private bool KidsEnabled => !string.IsNullOrWhiteSpace(_options.KidsFolderName);

    public string KidsMoviesRoot =>
        Path.Combine(_options.LibraryRoot, _options.KidsFolderName, _options.MoviesFolderName);

    public string KidsSeriesRoot =>
        Path.Combine(_options.LibraryRoot, _options.KidsFolderName, _options.SeriesFolderName);

    /// <summary>Every root jetio owns. Pruning walks all of them.</summary>
    public IEnumerable<string> AllRoots
    {
        get
        {
            yield return MoviesRoot;
            yield return SeriesRoot;

            if (KidsEnabled)
            {
                yield return KidsMoviesRoot;
                yield return KidsSeriesRoot;
            }
        }
    }

    /// <summary>
    /// Creates every root up front so the Jellyfin libraries can be pointed at them before a
    /// single title exists. Jellyfin will not accept a path that is not there yet.
    /// </summary>
    public void EnsureRootsExist()
    {
        foreach (var root in AllRoots)
        {
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not create library folder {Path}", root);
            }
        }

        _logger.LogInformation("Library folders ready: {Roots}", string.Join(", ", AllRoots));
    }

    /// <summary>Roots a title of this kind could live in — normal first, then kids.</summary>
    private IEnumerable<string> RootsFor(MediaKind kind)
    {
        yield return kind == MediaKind.Movie ? MoviesRoot : SeriesRoot;

        if (KidsEnabled)
        {
            yield return kind == MediaKind.Movie ? KidsMoviesRoot : KidsSeriesRoot;
        }
    }

    /// <summary>Where this title belongs, based on its Cinemeta genres.</summary>
    private string RootFor(CinemetaMeta meta, MediaKind kind)
    {
        var genres = meta.Genres ?? meta.Genre;

        var isKids = KidsEnabled
            && genres is not null
            && genres.Any(g => _options.KidsGenres.Contains(g, StringComparer.OrdinalIgnoreCase));

        if (!isKids)
        {
            return kind == MediaKind.Movie ? MoviesRoot : SeriesRoot;
        }

        return kind == MediaKind.Movie ? KidsMoviesRoot : KidsSeriesRoot;
    }

    public async Task<WriteResult> WriteMovieAsync(CinemetaMeta meta, CancellationToken cancellationToken)
    {
        var imdbId = meta.ImdbId ?? meta.Id;
        var folderName = BuildTitleFolderName(meta.Name, ExtractYear(meta), imdbId);
        var folder = Path.Combine(RootFor(meta, MediaKind.Movie), folderName);
        var file = Path.Combine(folder, $"{folderName}.strm");
        var url = $"{_options.PublicBaseUrl.TrimEnd('/')}/resolve/movie/{imdbId}";

        var changed = await WriteIfChangedAsync(file, url, cancellationToken).ConfigureAwait(false);
        return new WriteResult([Path.GetFullPath(file)], changed ? 1 : 0);
    }

    /// <summary>
    /// Rewrites the whole episode list from current metadata, which is how a newly released
    /// season turns up: Cinemeta gains the episodes, and the next sync writes them.
    /// </summary>
    public async Task<WriteResult> WriteSeriesAsync(CinemetaMeta meta, CancellationToken cancellationToken)
    {
        var imdbId = meta.ImdbId ?? meta.Id;
        var folderName = BuildTitleFolderName(meta.Name, ExtractYear(meta), imdbId);
        var showFolder = Path.Combine(RootFor(meta, MediaKind.Series), folderName);
        var showTitle = Sanitize(meta.Name);
        var written = new List<string>();
        var changed = 0;

        var now = DateTimeOffset.UtcNow;

        foreach (var video in meta.Videos ?? new List<CinemetaVideo>())
        {
            var season = video.Season;
            var episode = video.Episode > 0 ? video.Episode : video.Number ?? 0;

            if (episode <= 0)
            {
                continue;
            }

            if (season == 0 && !_options.IncludeSpecials)
            {
                continue;
            }

            if (!_options.IncludeUnairedEpisodes && video.Released is { } released && released > now)
            {
                continue;
            }

            var seasonFolder = Path.Combine(showFolder, $"Season {season.ToString("00", CultureInfo.InvariantCulture)}");
            var fileName = string.Create(
                CultureInfo.InvariantCulture,
                $"{showTitle} S{season:00}E{episode:00}.strm");
            var file = Path.Combine(seasonFolder, fileName);

            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"{_options.PublicBaseUrl.TrimEnd('/')}/resolve/series/{imdbId}/{season}/{episode}");

            if (await WriteIfChangedAsync(file, url, cancellationToken).ConfigureAwait(false))
            {
                changed++;
            }

            written.Add(Path.GetFullPath(file));
        }

        if (changed > 0)
        {
            _logger.LogInformation("{Show}: {Count} new episode(s)", meta.Name, changed);
        }

        return new WriteResult(written, changed);
    }

    /// <summary>
    /// Rewriting an unchanged file bumps its mtime, which makes Jellyfin re-probe the item on
    /// every scan. Comparing first keeps repeat syncs free.
    /// </summary>
    private async Task<bool> WriteIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existing.Trim(), content, StringComparison.Ordinal))
            {
                return false;
            }
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Wrote {Path}", path);
        return true;
    }

    /// <summary>
    /// The folder holding one title, matched on the [imdbid-…] tag the writer puts in its name.
    /// Checks the kids root too, since a title's genres may have changed since it was written.
    /// </summary>
    public string? FindTitleFolder(MediaKind kind, string imdbId)
    {
        var marker = $"[imdbid-{imdbId}]";

        foreach (var root in RootsFor(kind))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (System.IO.Path.GetFileName(directory).Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return directory;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Deletes one title's folder outright, matched on the [imdbid-…] tag in its name.
    /// Used when a title is removed through the UI, so it vanishes immediately.
    /// </summary>
    public int RemoveTitle(MediaKind kind, string imdbId)
    {
        var marker = $"[imdbid-{imdbId}]";
        var removed = 0;

        // Checks the kids root too: a title's genres may have changed since it was written.
        foreach (var root in RootsFor(kind))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!System.IO.Path.GetFileName(directory).Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                    _logger.LogInformation("Deleted {Path}", directory);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not delete {Path}", directory);
                }
            }
        }

        return removed;
    }

    /// <summary>Removes .strm files jetio no longer owns, then any directories left empty.</summary>
    public int Prune(IReadOnlySet<string> keepPaths)
    {
        if (!_options.PruneRemovedItems)
        {
            return 0;
        }

        var removed = 0;

        foreach (var root in AllRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.strm", SearchOption.AllDirectories))
            {
                if (keepPaths.Contains(Path.GetFullPath(file)))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                    _logger.LogInformation("Pruned {Path}", file);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not prune {Path}", file);
                }
            }

            RemoveEmptyDirectories(root);
        }

        return removed;
    }

    private void RemoveEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not remove empty directory {Path}", directory);
            }
        }
    }

    /// <summary>
    /// "The Matrix (1999) [imdbid-tt0133093]" — the imdbid tag is what stops Jellyfin from
    /// guessing wrong on remakes and same-titled releases.
    /// </summary>
    internal static string BuildTitleFolderName(string name, string? year, string imdbId)
    {
        var safeName = Sanitize(name);
        var yearPart = string.IsNullOrEmpty(year) ? string.Empty : $" ({year})";
        return $"{safeName}{yearPart} [imdbid-{imdbId}]";
    }

    /// <summary>Cinemeta gives "2008" for films and ranges like "2008–2013" for shows.</summary>
    internal static string? ExtractYear(CinemetaMeta meta)
    {
        foreach (var candidate in new[] { meta.Year, meta.ReleaseInfo })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var match = YearRegex().Match(candidate);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }

    internal static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => ' ',
                _ when char.IsControl(ch) => ' ',
                _ => ch,
            });
        }

        var collapsed = WhitespaceRegex().Replace(builder.ToString(), " ").Trim();

        // Windows rejects names ending in a dot or space, and Jellyfin runs on both platforms.
        collapsed = collapsed.TrimEnd('.', ' ');

        if (collapsed.Length > 120)
        {
            collapsed = collapsed[..120].TrimEnd('.', ' ');
        }

        return collapsed.Length == 0 ? "Untitled" : collapsed;
    }

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
