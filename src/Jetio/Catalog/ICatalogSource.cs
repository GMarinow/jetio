namespace Jetio.Catalog;

public enum MediaKind
{
    Movie,
    Series,
}

/// <summary>
/// One title a catalog wants in the library. Only the IMDb id really matters — everything
/// else is filled in from Cinemeta later, so all sources converge on the same metadata.
/// </summary>
public sealed record CatalogEntry(string ImdbId, MediaKind Kind, string? Name = null)
{
    public string StremioType => Kind == MediaKind.Movie ? "movie" : "series";
}

public interface ICatalogSource
{
    string Name { get; }

    bool Enabled { get; }

    Task<IReadOnlyList<CatalogEntry>> GetEntriesAsync(CancellationToken cancellationToken);
}

public static class CatalogEntryExtensions
{
    /// <summary>Valid IMDb title ids are "tt" followed by at least seven digits.</summary>
    public static bool LooksLikeImdbId(this string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
        && value.Length >= 9
        && value.AsSpan(2).ToString().All(char.IsDigit);
}
