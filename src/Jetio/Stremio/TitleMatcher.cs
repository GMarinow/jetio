using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jetio.Stremio;

/// <summary>
/// What a release is expected to be. <paramref name="Year"/> is null for episodes, where the
/// release year belongs to the season or show rather than the episode.
/// </summary>
public sealed record TitleContext(string Name, string? Year);

/// <summary>
/// Checks that a release is actually the film that was asked for.
///
/// Torrentio's index is crowd-sourced and sometimes wrong: querying an unreleased title can
/// return a completely different film (a rip of Iron Man indexed under Spider-Man: Brand New
/// Day, for instance). Quality filters make this worse rather than better — for an unreleased
/// title every genuine release is a cam, so the mislabelled clean rip wins on quality.
/// </summary>
public static partial class TitleMatcher
{
    /// <summary>Words carrying no identifying weight, dropped before comparing.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "or", "in", "on", "at", "to", "for", "part", "vol",
    };

    /// <summary>Fraction of the expected title's words a release must contain.</summary>
    private const double RequiredOverlap = 0.6d;

    /// <summary>
    /// True when <paramref name="releaseName"/> plausibly refers to <paramref name="expectedTitle"/>.
    /// </summary>
    /// <param name="expectedYear">Release year from metadata, if known.</param>
    public static bool Matches(string releaseName, string expectedTitle, string? expectedYear, out string reason)
    {
        var expected = Tokenize(expectedTitle);
        var actual = Tokenize(releaseName);

        if (expected.Count == 0)
        {
            reason = string.Empty;
            return true;
        }

        var overlap = expected.Count(actual.Contains) / (double)expected.Count;

        if (overlap < RequiredOverlap)
        {
            var missing = string.Join(", ", expected.Where(t => !actual.Contains(t)).Take(4));
            reason = $"title mismatch, missing: {missing}";
            return false;
        }

        // A correct title with the wrong year is usually a different entry in a franchise.
        if (int.TryParse(expectedYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            var years = YearRegex().Matches(releaseName)
                .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                .Where(y => y is >= 1900 and <= 2100)
                .ToList();

            // Allow a year either side: releases disagree about premiere vs general release.
            if (years.Count > 0 && years.All(y => Math.Abs(y - year) > 1))
            {
                reason = $"year mismatch, expected {year} but release says {years[0]}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Reduces a title to comparable words: lowercase, accents stripped, separators and
    /// release-tag punctuation flattened, noise words removed.
    /// </summary>
    internal static HashSet<string> Tokenize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !Noise.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();
}
