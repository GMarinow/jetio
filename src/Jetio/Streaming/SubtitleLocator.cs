using System.Globalization;
using Jetio.Catalog;
using Jetio.Configuration;
using Jetio.Library;
using Microsoft.Extensions.Options;

namespace Jetio.Streaming;

/// <param name="Path">Absolute path to the subtitle file.</param>
/// <param name="Language">ISO 639-2 code for the Matroska track, or null if the name carried none.</param>
/// <param name="Title">Track label shown in the player's subtitle menu.</param>
public sealed record SubtitleTrack(string Path, string? Language, string Title);

/// <summary>
/// Finds the subtitle files sitting beside a title's .strm — the ones Subbuzz and Open Subtitles
/// write, or that were dropped in by hand.
///
/// Matching is on filename rather than a stored index deliberately: subtitles appear long after
/// a title is written, and jetio never sees them arrive.
/// </summary>
public sealed class SubtitleLocator
{
    private readonly StrmLibraryWriter _library;
    private readonly SubtitleOptions _options;
    private readonly ILogger<SubtitleLocator> _logger;

    public SubtitleLocator(
        StrmLibraryWriter library,
        IOptions<JetioOptions> options,
        ILogger<SubtitleLocator> logger)
    {
        _library = library;
        _options = options.Value.Subtitles;
        _logger = logger;
    }

    public IReadOnlyList<SubtitleTrack> ForMovie(string imdbId)
    {
        var folder = _library.FindTitleFolder(MediaKind.Movie, imdbId);
        return folder is null ? Array.Empty<SubtitleTrack>() : Collect(folder, matchAnyBaseName: true, null);
    }

    public IReadOnlyList<SubtitleTrack> ForEpisode(string imdbId, int season, int episode)
    {
        var showFolder = _library.FindTitleFolder(MediaKind.Series, imdbId);
        if (showFolder is null)
        {
            return Array.Empty<SubtitleTrack>();
        }

        var seasonFolder = Path.Combine(
            showFolder,
            $"Season {season.ToString("00", CultureInfo.InvariantCulture)}");

        if (!Directory.Exists(seasonFolder))
        {
            return Array.Empty<SubtitleTrack>();
        }

        // A season folder holds every episode, so the file must be pinned to this one.
        var marker = string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}");
        return Collect(seasonFolder, matchAnyBaseName: false, marker);
    }

    /// <summary>
    /// A movie folder holds exactly one title, so any subtitle in it belongs to that title —
    /// which also catches files named differently from the .strm, as several downloaders do.
    /// Season folders need the episode marker instead.
    /// </summary>
    private IReadOnlyList<SubtitleTrack> Collect(string folder, bool matchAnyBaseName, string? marker)
    {
        var extensions = _options.Extensions
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToArray();

        List<SubtitleTrack> found = [];

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);

                if (!extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!matchAnyBaseName
                    && (marker is null || !name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var language = ExtractLanguage(name);
                found.Add(new SubtitleTrack(file, language, BuildTitle(language)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read subtitles from {Folder}", folder);
            return Array.Empty<SubtitleTrack>();
        }

        // Named languages first, so an untagged file never takes the default slot from a real one.
        return found
            .OrderBy(t => t.Language is null ? 1 : 0)
            .ThenBy(t => t.Language, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, _options.MaxTracks))
            .ToList();
    }

    /// <summary>
    /// "Film (2016) [imdbid-tt123].bg.srt" → "bul". Matroska wants ISO 639-2, and the two-letter
    /// form is what downloaders write, so the codes are translated rather than passed through.
    /// </summary>
    internal static string? ExtractLanguage(string fileName)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var lastDot = withoutExtension.LastIndexOf('.');

        if (lastDot < 0)
        {
            return null;
        }

        var candidate = withoutExtension[(lastDot + 1)..];

        // "forced" and "hi" are qualifiers, not languages; step back one segment to find the code.
        if (candidate.Equals("forced", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("hi", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("sdh", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = withoutExtension[..lastDot];
            return ExtractLanguage(trimmed + ".x");
        }

        if (candidate.Length is not (2 or 3) || !candidate.All(char.IsLetter))
        {
            return null;
        }

        try
        {
            return new CultureInfo(candidate).ThreeLetterISOLanguageName;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string BuildTitle(string? language)
    {
        if (language is null)
        {
            return "External";
        }

        try
        {
            return CultureInfo.GetCultureInfo(language).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }
}
