using System.Text;
using System.Text.RegularExpressions;

namespace TabTabGo.Storage.AzureStorage;

/// <summary>
/// Maps a logical container name to an Azure container name. For every name that 6.x could already store, the result is
/// exactly the 6.0.8.4 output on every non-Turkic culture (trim, invariant lower-case, space to hyphen, drop everything but [a-z0-9-], cut at 63, trim
/// hyphens) - otherwise existing blobs become unreachable. Only names that Azure rejected before (a "--" run, or fewer
/// than 3 characters) are adjusted: hyphen runs collapse and short names are padded with <c>0</c>.
/// </summary>
internal static partial class ContainerNameNormalizer
{
    private const int MinLength = 3;
    private const int MaxLength = 63;

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex InvalidChars();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedHyphens();

    /// <summary>Normalises the name.</summary>
    /// <exception cref="ArgumentException">Nothing valid is left of the name.</exception>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            throw new ArgumentException("Container name must not be empty.", nameof(input));
        }

        // The 6.0.8.4 algorithm, unchanged.
        var name = input.Trim().ToLowerInvariant().Replace(" ", "-");
        name = InvalidChars().Replace(name, string.Empty);
        if (name.Length > MaxLength)
        {
            name = name[..MaxLength];
        }

        name = name.Trim('-');
        if (name.Length < 1)
        {
            throw new ArgumentException("Container name has no valid characters.", nameof(input));
        }

        // Names Azure used to reject (400 InvalidResourceName) are the only ones that may change.
        if (name.Contains("--", StringComparison.Ordinal))
        {
            name = RepeatedHyphens().Replace(name, "-").Trim('-');
            if (name.Length < 1)
            {
                throw new ArgumentException("Container name has no valid characters.", nameof(input));
            }
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
