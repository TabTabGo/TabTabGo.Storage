namespace TabTabGo.Storage.Services;

/// <summary>Resolves configured storage paths against the host's content root.</summary>
public interface IPathService
{
    /// <summary>Resolves a (possibly relative) path against the content root. Absolute paths are kept.</summary>
    /// <param name="path">Path to resolve.</param>
    /// <returns>An absolute path.</returns>
    string ResolvePath(string path);

    /// <summary>Creates the directory when it does not exist.</summary>
    /// <param name="path">Directory to create.</param>
    void EnsureDirectory(string path);

    /// <summary>The bound path configuration.</summary>
    PathSettings PathSettings { get; }
}

/// <summary>
/// File-system storage configuration. Bind it from the <c>PathSettings</c> configuration section:
/// <c>PathSettings:RootPath</c>, <c>PathSettings:FileTypePaths:Image</c>, and so on.
/// </summary>
public class PathSettings
{
    /// <summary>Maps a container (file type) name to a folder under <see cref="RootPath"/>. Keys are case-insensitive.</summary>
    public IDictionary<string, string> FileTypePaths { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Root folder. Relative values resolve against the content root. Keep it outside the web root.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Format of the date folder created for generated names. Defaults to <c>yyyyMMdd</c>.</summary>
    public string? DateFormat { get; set; }

    /// <summary>
    /// URL that serves <see cref="RootPath"/> publicly, if the host exposes it. Used by
    /// <c>GetPublicUrlAsync</c>; when <c>null</c> the file-system provider has no public URL.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Gets or sets the folder for a container. Getting an unmapped container returns <c>null</c>.</summary>
    /// <param name="fileTypeName">Container name (case-insensitive).</param>
    public string? this[string fileTypeName]
    {
        get => TryGetPath(fileTypeName, out var path) ? path : null;
        set
        {
            ArgumentNullException.ThrowIfNull(fileTypeName);
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            FileTypePaths[fileTypeName] = value;
        }
    }

    /// <summary>Looks up the folder for a container, ignoring case.</summary>
    /// <param name="fileTypeName">Container name.</param>
    /// <param name="path">The configured folder.</param>
    /// <returns><c>true</c> when the container is mapped.</returns>
    public bool TryGetPath(string fileTypeName, out string path)
    {
        path = string.Empty;
        if (fileTypeName is null)
        {
            return false;
        }

        if (FileTypePaths.TryGetValue(fileTypeName, out var exact))
        {
            path = exact;
            return true;
        }

        foreach (var pair in FileTypePaths)
        {
            if (string.Equals(pair.Key, fileTypeName, StringComparison.OrdinalIgnoreCase))
            {
                path = pair.Value;
                return true;
            }
        }

        return false;
    }
}
