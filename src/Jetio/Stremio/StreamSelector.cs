using System.Globalization;
using System.Text.RegularExpressions;
using Jetio.Configuration;
using Microsoft.Extensions.Options;

namespace Jetio.Stremio;

/// <summary>A Torrentio stream with the numbers parsed out of its annotated title.</summary>
public sealed record StreamCandidate
{
    public required TorrentioStream Stream { get; init; }

    /// <summary>First line of the title — the release name, without the emoji annotations.</summary>
    public required string ReleaseName { get; init; }

    /// <summary>Normalised to "2160p" / "1080p" / "720p" / "480p", or null if undetectable.</summary>
    public string? Resolution { get; init; }

    public int Seeders { get; init; }

    public double SizeGb { get; init; }

    public int ResolutionRank { get; init; }

    public int PreferenceBonus { get; init; }

    /// <summary>Movie-only: the release bundles several films, so the right file is a guess.</summary>
    public bool IsPack { get; init; }

    /// <summary>The release appears to be a different title entirely.</summary>
    public bool IsWrongTitle { get; init; }

    public string? RejectionReason { get; init; }

    public bool IsEligible => RejectionReason is null;
}

public sealed partial class StreamSelector
{
    private readonly StreamSelectionOptions _options;
    private readonly ILogger<StreamSelector> _logger;
    private readonly IReadOnlyList<Regex> _excludePatterns;
    private readonly IReadOnlyList<Regex> _preferPatterns;
    private readonly IReadOnlyList<Regex> _moviePackPatterns;

    public StreamSelector(IOptions<JetioOptions> options, ILogger<StreamSelector> logger)
    {
        _options = options.Value.StreamSelection;
        _logger = logger;
        _excludePatterns = Compile(_options.ExcludePatterns);
        _preferPatterns = Compile(_options.PreferPatterns);
        _moviePackPatterns = Compile(_options.MoviePackExcludePatterns);
    }

    private static IReadOnlyList<Regex> Compile(IEnumerable<string> patterns) => patterns
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .ToList();

    /// <summary>Ranks every stream, best first. Ineligible ones sort last and carry a reason.</summary>
    /// <param name="expected">Title to verify releases against, or null to skip verification.</param>
    public IReadOnlyList<StreamCandidate> Rank(
        IEnumerable<TorrentioStream> streams,
        bool isMovie,
        TitleContext? expected = null)
    {
        return streams
            .Select(s => Evaluate(s, isMovie, expected))
            .OrderBy(c => c.IsEligible ? 0 : 1)
            .ThenBy(c => c.ResolutionRank)
            .ThenByDescending(c => c.PreferenceBonus)
            .ThenByDescending(c => c.Seeders)
            .ToList();
    }

    /// <summary>
    /// Picks the best playable stream. Falls back to the healthiest disqualified stream rather
    /// than returning nothing — a mediocre release beats an unplayable item in the library.
    /// </summary>
    public StreamCandidate? Select(
        IEnumerable<TorrentioStream> streams,
        bool isMovie,
        TitleContext? expected = null)
    {
        var ranked = Rank(streams, isMovie, expected);

        var best = ranked.FirstOrDefault(c => c.IsEligible);
        if (best is not null)
        {
            return best;
        }

        // Relaxing the filters is fine; relaxing into a pack or a mislabelled release is not,
        // since those play a different film rather than a worse copy of the right one.
        // Returning nothing is the correct answer when only wrong titles are on offer.
        var fallback = ranked
            .Where(c => c.Stream.InfoHash is not null || c.Stream.Url is not null)
            .Where(c => !c.IsPack && !c.IsWrongTitle)
            .MaxBy(c => c.Seeders);

        if (fallback is not null)
        {
            _logger.LogInformation(
                "No stream passed the filters; falling back to {Release} ({Reason})",
                fallback.ReleaseName,
                fallback.RejectionReason);
        }

        return fallback;
    }

    private StreamCandidate Evaluate(TorrentioStream stream, bool isMovie, TitleContext? expected)
    {
        var title = stream.Title ?? string.Empty;
        var releaseName = title.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? stream.BehaviorHints?.Filename
            ?? stream.Name
            ?? "unknown";

        var seeders = ParseSeeders(title);
        var sizeGb = ParseSizeGb(title);

        // The quality tag lives in `name` ("Torrentio\n4k HDR"); the release name is the backup.
        var searchText = $"{stream.Name}\n{title}\n{stream.BehaviorHints?.Filename}";
        var resolution = ParseResolution(searchText);

        var rank = resolution is null
            ? int.MaxValue - 1
            : _options.ResolutionPriority.FindIndex(r =>
                string.Equals(NormaliseResolution(r), resolution, StringComparison.OrdinalIgnoreCase));

        if (rank < 0)
        {
            // Detected, but the user did not list it — rank below everything they did list.
            rank = int.MaxValue - 2;
        }

        var bonus = _preferPatterns.Count(p => p.IsMatch(searchText));
        var packPattern = isMovie ? _moviePackPatterns.FirstOrDefault(p => p.IsMatch(releaseName)) : null;

        string? titleProblem = null;
        if (expected is not null
            && !TitleMatcher.Matches(releaseName, expected.Name, expected.Year, out var mismatch))
        {
            titleProblem = mismatch;
        }

        return new StreamCandidate
        {
            IsPack = packPattern is not null,
            IsWrongTitle = titleProblem is not null,
            Stream = stream,
            ReleaseName = releaseName.Trim(),
            Resolution = resolution,
            Seeders = seeders,
            SizeGb = sizeGb,
            ResolutionRank = rank,
            PreferenceBonus = bonus,
            RejectionReason = Disqualify(stream, searchText, packPattern, titleProblem, seeders, sizeGb, rank),
        };
    }

    private string? Disqualify(
        TorrentioStream stream,
        string searchText,
        Regex? packPattern,
        string? titleProblem,
        int seeders,
        double sizeGb,
        int rank)
    {
        if (stream.InfoHash is null && stream.Url is null)
        {
            return "no infoHash or url";
        }

        // Checked before quality: a pristine rip of the wrong film is the worst outcome.
        if (titleProblem is not null)
        {
            return titleProblem;
        }

        var excluded = _excludePatterns.FirstOrDefault(p => p.IsMatch(searchText));
        if (excluded is not null)
        {
            return $"matched exclude pattern /{excluded}/";
        }

        if (packPattern is not null)
        {
            return $"looks like a multi-film pack (/{packPattern}/)";
        }

        if (seeders < _options.MinSeeders)
        {
            return $"only {seeders} seeders (minimum {_options.MinSeeders})";
        }

        if (sizeGb > 0 && sizeGb > _options.MaxSizeGb)
        {
            return $"{sizeGb:0.##} GB exceeds max {_options.MaxSizeGb} GB";
        }

        if (sizeGb > 0 && sizeGb < _options.MinSizeGb)
        {
            return $"{sizeGb:0.##} GB below min {_options.MinSizeGb} GB";
        }

        if (rank >= int.MaxValue - 2)
        {
            return "resolution not in priority list";
        }

        return null;
    }

    internal static int ParseSeeders(string title)
    {
        var match = SeedersRegex().Match(title);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeders)
            ? seeders
            : 0;
    }

    internal static double ParseSizeGb(string title)
    {
        var match = SizeRegex().Match(title);
        if (!match.Success)
        {
            return 0d;
        }

        var raw = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return 0d;
        }

        return match.Groups[2].Value.ToUpperInvariant() switch
        {
            "TB" => value * 1024d,
            "MB" => value / 1024d,
            _ => value,
        };
    }

    internal static string? ParseResolution(string text)
    {
        if (ResolutionRegex2160().IsMatch(text))
        {
            return "2160p";
        }

        if (ResolutionRegex1080().IsMatch(text))
        {
            return "1080p";
        }

        if (ResolutionRegex720().IsMatch(text))
        {
            return "720p";
        }

        if (ResolutionRegex480().IsMatch(text))
        {
            return "480p";
        }

        return null;
    }

    /// <summary>Treats "4k" and "uhd" as aliases of 2160p so config can use either spelling.</summary>
    internal static string NormaliseResolution(string resolution) =>
        resolution.Trim().ToLowerInvariant() switch
        {
            "4k" or "uhd" or "2160" or "2160p" => "2160p",
            "1080" or "1080p" or "fhd" => "1080p",
            "720" or "720p" or "hd" => "720p",
            "480" or "480p" or "sd" => "480p",
            var other => other,
        };

    // "👤 84" — seeder count.
    [GeneratedRegex("\U0001F464\\s*(\\d+)")]
    private static partial Regex SeedersRegex();

    // "💾 51.3 GB" — total release size.
    [GeneratedRegex("\U0001F4BE\\s*([\\d.,]+)\\s*(TB|GB|MB)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\b(2160p?|4k|uhd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex2160();

    [GeneratedRegex(@"\b(1080p?|fhd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex1080();

    [GeneratedRegex(@"\b720p?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex720();

    [GeneratedRegex(@"\b(480p?|360p?|sd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex480();
}
