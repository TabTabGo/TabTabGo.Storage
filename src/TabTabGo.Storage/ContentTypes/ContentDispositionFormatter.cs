using System.Text;

namespace TabTabGo.Storage.ContentTypes;

/// <summary>
/// Builds <c>Content-Disposition</c> header values from untrusted file names (RFC 6266 / RFC 5987). Control
/// characters and quotes are removed so a file name can never inject a header or break out of the quoted string.
/// </summary>
public static class ContentDispositionFormatter
{
    /// <summary>Formats a disposition value.</summary>
    /// <param name="fileName">Untrusted file name.</param>
    /// <param name="inline"><c>true</c> for <c>inline</c>, otherwise <c>attachment</c>.</param>
    /// <returns>For example <c>attachment; filename="a.png"; filename*=UTF-8''a.png</c>.</returns>
    public static string Format(string? fileName, bool inline = false)
    {
        var disposition = inline ? "inline" : "attachment";
        var clean = Clean(fileName);
        if (clean.Length == 0)
        {
            return disposition;
        }

        var ascii = new StringBuilder(clean.Length);
        foreach (var c in clean)
        {
            ascii.Append(c is >= ' ' and < '\u007f' && c is not ('"' or '\\' or '%' or ';') ? c : '_');
        }

        return $"{disposition}; filename=\"{ascii}\"; filename*=UTF-8''{EncodeRfc5987(clean)}";
    }

    /// <summary>
    /// Returns whether a character is a bidirectional control (U+202A-202E, U+2066-2069, U+200E/F, U+061C). They let a
    /// name such as <c>photo[RLO]gnp.exe</c> display as <c>photoexe.png</c> and are removed from stored and served names.
    /// </summary>
    /// <param name="c">Character to test.</param>
    /// <returns><c>true</c> for a bidi control character.</returns>
    public static bool IsBidiControl(char c) =>
        c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200E' or '\u200F' or '\u061C';

    /// <summary>
    /// Returns whether a character is invisible or reorders text: the bidi controls of <see cref="IsBidiControl"/>, the
    /// zero-width characters U+200B-200D, U+2060 and U+FEFF, the line / paragraph separators U+2028 and U+2029, and every
    /// other character of the Unicode "format" (Cf) category (soft hyphen U+00AD, U+180E, invisible operators U+2061-2064, ...).
    /// They make two different file names look identical, or hide an extension, and are removed from names.
    /// </summary>
    /// <param name="c">Character to test.</param>
    /// <returns><c>true</c> for an invisible or reordering character.</returns>
    public static bool IsInvisibleControl(char c) =>
        IsBidiControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ||
        c is >= '\u200B' and <= '\u200D' or '\u2060' or '\uFEFF' or '\u2028' or '\u2029';

    /// <summary>
    /// Removes from a name every control character, DEL, every invisible or reordering character (see
    /// <see cref="IsInvisibleControl"/>), every Unicode "format" (Cf) code point including those outside the BMP (tag
    /// characters U+E0000-E007F, musical format controls U+1D173-1D17A, ...), and lone surrogates. The string is walked by
    /// code point, so a valid surrogate pair (an emoji) is kept or dropped as a whole.
    /// </summary>
    /// <param name="value">Name to clean.</param>
    /// <param name="keepQuote">Whether the double quote is kept (it is removed for Content-Disposition).</param>
    /// <returns>The cleaned name (not trimmed).</returns>
    public static string StripInvisible(string value, bool keepQuote = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var rune = new Rune(c, value[i + 1]);
                if (Rune.GetUnicodeCategory(rune) != System.Globalization.UnicodeCategory.Format)
                {
                    builder.Append(c).Append(value[i + 1]);
                }

                i++;
                continue;
            }

            if (char.IsSurrogate(c) || c < ' ' || c == '\u007f' || (!keepQuote && c == '"') || IsInvisibleControl(c))
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Clean(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return StripInvisible(name, keepQuote: false).Trim();
    }

    private static string EncodeRfc5987(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "!#$&+-.^_`|~".Contains(c)))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
