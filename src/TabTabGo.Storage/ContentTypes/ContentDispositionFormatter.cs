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

    private static string Clean(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < ' ' || c == '\u007f' || c == '"')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
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
