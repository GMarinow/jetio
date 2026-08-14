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

        var lower = candidate.ToLowerInvariant();

        // Deliberately a table rather than CultureInfo. The runtime image has no ICU data, so
        // every culture lookup throws there — the sort of difference that passes every test on a
        // developer machine and then silently drops the language on the server.
        if (TwoToThreeLetter.TryGetValue(lower, out var mapped))
        {
            return mapped;
        }

        // Already three letters, or a code not in the table: pass it through. ffmpeg and Matroska
        // accept any string here, and a slightly wrong tag beats no tag at all — players will not
        // enable a subtitle track whose language they cannot determine.
        return lower.Length == 3 ? lower : null;
    }

    /// <summary>
    /// ISO 639-1 to 639-2/T for the languages subtitles actually arrive in. Matroska, HLS and
    /// ffmpeg all expect the three-letter form.
    /// </summary>
    private static readonly Dictionary<string, string> TwoToThreeLetter = new(StringComparer.Ordinal)
    {
        ["bg"] = "bul", ["cs"] = "ces", ["da"] = "dan", ["de"] = "deu", ["el"] = "ell",
        ["en"] = "eng", ["es"] = "spa", ["et"] = "est", ["fi"] = "fin", ["fr"] = "fra",
        ["he"] = "heb", ["hr"] = "hrv", ["hu"] = "hun", ["is"] = "isl", ["it"] = "ita",
        ["ja"] = "jpn", ["ko"] = "kor", ["lt"] = "lit", ["lv"] = "lav", ["mk"] = "mkd",
        ["nl"] = "nld", ["no"] = "nor", ["pl"] = "pol", ["pt"] = "por", ["ro"] = "ron",
        ["ru"] = "rus", ["sk"] = "slk", ["sl"] = "slv", ["sr"] = "srp", ["sv"] = "swe",
        ["tr"] = "tur", ["uk"] = "ukr", ["zh"] = "zho",
    };

    private static string BuildTitle(string? language) =>
        language is null ? "External" : DescribeLanguage(language);

    /// <summary>
    /// "bul" → "Bulgarian", for the label a player shows in its subtitle menu. A table again, for
    /// the same reason: culture lookups throw in the runtime image.
    /// </summary>
    internal static string DescribeLanguage(string language) =>
        LanguageNames.TryGetValue(language, out var name) ? name : language.ToUpperInvariant();

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.Ordinal)
    {
        ["bul"] = "Bulgarian", ["ces"] = "Czech", ["dan"] = "Danish", ["deu"] = "German",
        ["ell"] = "Greek", ["eng"] = "English", ["spa"] = "Spanish", ["est"] = "Estonian",
        ["fin"] = "Finnish", ["fra"] = "French", ["heb"] = "Hebrew", ["hrv"] = "Croatian",
        ["hun"] = "Hungarian", ["isl"] = "Icelandic", ["ita"] = "Italian", ["jpn"] = "Japanese",
        ["kor"] = "Korean", ["lit"] = "Lithuanian", ["lav"] = "Latvian", ["mkd"] = "Macedonian",
        ["nld"] = "Dutch", ["nor"] = "Norwegian", ["pol"] = "Polish", ["por"] = "Portuguese",
        ["ron"] = "Romanian", ["rus"] = "Russian", ["slk"] = "Slovak", ["slv"] = "Slovenian",
        ["srp"] = "Serbian", ["swe"] = "Swedish", ["tur"] = "Turkish", ["ukr"] = "Ukrainian",
        ["zho"] = "Chinese",
    };
}
