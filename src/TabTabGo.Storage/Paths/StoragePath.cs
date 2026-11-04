namespace TabTabGo.Storage.Paths;

/// <summary>
/// The single choke point for object paths. Every provider routes every path through <see cref="Normalize"/> so that
/// traversal, rooted paths and platform-specific separators never reach a backend.
/// </summary>
public static class StoragePath
{
    /// <summary>
    /// Converts <paramref name="path"/> to a relative, <c>/</c>-separated path.
    /// </summary>
    /// <remarks>
    /// Backslashes become <c>/</c>, duplicate separators collapse and trailing separators are dropped.
    /// Rejected: empty or whitespace paths, rooted/UNC/drive paths, segments made only of dots or spaces
    /// (<c>.</c>, <c>..</c>, <c>...</c>), the <c>:</c> character (drive letters, NTFS streams) and control characters.
    /// Percent-encoded sequences such as <c>%2e%2e</c> are treated as literal text.
    /// </remarks>
    /// <param name="path">Path as supplied by a caller.</param>
    /// <returns>The normalised relative path.</returns>
    /// <exception cref="ArgumentException">The path is unsafe or empty.</exception>
    public static string Normalize(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        foreach (var c in path)
        {
            if (c < ' ' || c == '\u007f')
            {
                throw new ArgumentException("Path must not contain control characters.", nameof(path));
            }

            if (c == ':')
            {
                throw new ArgumentException("Path must not contain ':'.", nameof(path));
            }
        }

        var unified = path.Replace('\\', '/');
        if (unified[0] == '/')
        {
            throw new ArgumentException("Path must be relative.", nameof(path));
        }

        var segments = new List<string>();
        foreach (var segment in unified.Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (IsDotsOrSpaces(segment))
            {
                throw new ArgumentException("Path must not contain '.' or '..' segments.", nameof(path));
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        return string.Join('/', segments);
    }

    /// <summary>Joins segments and normalises the result.</summary>
    /// <param name="segments">Path segments (each may itself contain separators).</param>
    /// <returns>The normalised relative path.</returns>
    /// <exception cref="ArgumentException">The combined path is unsafe or empty.</exception>
    public static string Combine(params string[] segments)
    {
        if (segments is null)
        {
            throw new ArgumentNullException(nameof(segments));
        }

        // Not trimmed on purpose: a rooted first segment must still be rejected by Normalize.
        return Normalize(string.Join('/', segments.Where(s => !string.IsNullOrEmpty(s))));
    }

    /// <summary>Returns whether <paramref name="path"/> passes <see cref="Normalize"/>.</summary>
    /// <param name="path">Path to test.</param>
    /// <returns><c>true</c> when the path is safe.</returns>
    public static bool IsSafe(string? path)
    {
        if (path is null)
        {
            return false;
        }

        try
        {
            Normalize(path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsDotsOrSpaces(string segment)
    {
        foreach (var c in segment)
        {
            if (c != '.' && c != ' ')
            {
                return false;
            }
        }

        return true;
    }
}
