using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TabTabGo.Storage.Services;

namespace TabTabGo.Storage.FileStorage.Services;

/// <summary>Resolves configured storage paths against the host's content root.</summary>
public class PathService : IPathService
{
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<PathService> _logger;

    /// <summary>Creates the service and warns when the storage root sits under the web root.</summary>
    /// <param name="hostEnvironment">Host environment (provides the content root).</param>
    /// <param name="configuration">Bound <see cref="PathSettings"/>.</param>
    /// <param name="logger">Logger.</param>
    public PathService(IHostEnvironment hostEnvironment, IOptions<PathSettings> configuration,
        ILogger<PathService> logger)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _hostEnvironment = hostEnvironment;
        _logger = logger;
        PathSettings = configuration.Value;
        WarnWhenUnderWebRoot();
    }

    /// <inheritdoc />
    public PathSettings PathSettings { get; private set; }

    /// <inheritdoc />
    public string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // Path.Combine keeps an absolute path as is, so an absolute RootPath is honoured.
        return Path.Combine(_hostEnvironment.ContentRootPath, path);
    }

    /// <inheritdoc />
    public void EnsureDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    // Files stored under the web root with an .html/.svg/.js extension are served by static files: stored XSS.
    private void WarnWhenUnderWebRoot()
    {
        var webRoot = _hostEnvironment.GetType().GetProperty("WebRootPath")?.GetValue(_hostEnvironment) as string;
        var candidates = new List<string> { PathSettings.RootPath ?? string.Empty };
        candidates.AddRange(PathSettings.FileTypePaths.Values.Select(v => Path.Combine(PathSettings.RootPath ?? string.Empty, v)));
        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(ResolvePath(candidate));
            var underWebRoot = !string.IsNullOrEmpty(webRoot) &&
                               full.StartsWith(Path.GetFullPath(webRoot), StringComparison.OrdinalIgnoreCase);
            var underWwwroot = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(s => string.Equals(s, "wwwroot", StringComparison.OrdinalIgnoreCase));
            if (underWebRoot || underWwwroot)
            {
                _logger.LogWarning(
                    "The storage folder {StorageFolder} is under the web root. Uploaded files may be served by static files; store them outside the web root.",
                    full);
                return;
            }
        }
    }
}
