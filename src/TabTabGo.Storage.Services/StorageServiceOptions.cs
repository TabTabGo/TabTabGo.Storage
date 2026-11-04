namespace TabTabGo.Storage.Services;

/// <summary>Upload rules for <c>FileService&lt;TFile&gt;</c>.</summary>
public class StorageServiceOptions
{
    /// <summary>Longest accepted extension including the dot; matches the <c>FileExtension</c> column.</summary>
    public const int MaxExtensionLength = 10;

    /// <summary>Extensions that are always refused (with or without the dot, case-insensitive).</summary>
    public ISet<string> DeniedExtensions { get; set; } = new HashSet<string>(
        new[]
        {
            ".html", ".htm", ".svg", ".js", ".mjs", ".exe", ".dll", ".bat", ".cmd", ".ps1", ".sh", ".php", ".aspx", ".jsp",
            ".xml", ".xhtml", ".xht", ".xsl", ".xslt", ".svgz", ".shtml", ".hta",
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>When not empty, only these extensions are accepted.</summary>
    public ISet<string> AllowedExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Largest accepted upload in bytes. Defaults to 50 MiB; <c>null</c> means no limit.</summary>
    public long? MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024;
}
