using System.Text;
using System.Text.RegularExpressions;

namespace TabTabGo.Storage.AzureStorage;

/// <summary>Turns a logical container name into a valid Azure container name (3-63 chars, [a-z0-9-], no "--").</summary>
internal static partial class ContainerNameNormalizer
{
    private const int MinLength = 3;
    private const int MaxLength = 63;

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex InvalidChars();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedHyphens();

    /// <summary>Normalises the name; names shorter than 3 characters are padded with <c>0</c>.</summary>
    /// <exception cref="ArgumentException">Nothing valid is left of the name.</exception>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Container name must not be empty.", nameof(input));
        }

        var name = input.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        name = InvalidChars().Replace(name, string.Empty);
        name = RepeatedHyphens().Replace(name, "-").Trim('-');
        if (name.Length > MaxLength)
        {
            name = name[..MaxLength].Trim('-');
        }

        if (name.Length == 0)
        {
            throw new ArgumentException("Container name has no valid characters.", nameof(input));
        }

        if (name.Length >= MinLength)
        {
            return name;
        }

        var padded = new StringBuilder(name);
        while (padded.Length < MinLength)
        {
            padded.Append('0');
        }

        return padded.ToString();
    }
}
