namespace Jetio.Streaming;

/// <summary>
/// Works out what encoding a subtitle file is in, so ffmpeg can be told.
///
/// ffmpeg assumes UTF-8 and aborts on anything else — `Invalid UTF-8 in decoded subtitles text` —
/// which fails the whole conversion rather than one line. Cyrillic subtitles are still commonly
/// distributed as Windows-1251, so this is not a rare case.
/// </summary>
public static class SubtitleEncoding
{
    /// <summary>Returns null when the file is already UTF-8 and needs no override.</summary>
    public static string? Detect(string path)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (IsUtf8(bytes))
        {
            return null;
        }

        // Decoding to compare would mean registering the code-pages provider, which .NET does not
        // ship enabled. The byte layout answers it just as well: CP1251 holds Cyrillic across
        // 0xC0-0xFF, whereas in CP1252 that range is accented Latin — common in a word, rare in bulk.
        var nonAscii = bytes.Count(b => b >= 0x80);
        var cyrillicRange = bytes.Count(b => b >= 0xC0);

        return nonAscii > 0 && cyrillicRange >= nonAscii * 0.8 ? "CP1251" : "CP1252";
    }

    /// <summary>
    /// The default UTF-8 decoder substitutes a replacement character for bad input rather than
    /// failing, which would make every file look valid. This one throws instead.
    /// </summary>
    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }
}
