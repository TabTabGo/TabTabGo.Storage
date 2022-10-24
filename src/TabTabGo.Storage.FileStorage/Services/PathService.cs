using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using TabTabGo.Storage.Services;

namespace TabTabGo.Storage.FileStorage.Services;

public class PathService : IPathService
{
    private readonly IHostingEnvironment _hostingEnvironment;

    private readonly ILogger<PathService> _logger;

    public PathSettings PathSettings { get; private set; }

    public PathService(IHostingEnvironment hostingEnvironment, IOptions<PathSettings> configuration,
        ILogger<PathService> logger)
    {
        _hostingEnvironment = hostingEnvironment;
        _logger = logger;
        PathSettings = configuration.Value;
    }

    public string ResolvePath(string path)
    {
        //if(!string.IsNullOrEmpty(_hostingEnvironment.WebRootPath))
        //    return Path.Combine(_hostingEnvironment.WebRootPath, path);
        //else
        return Path.Combine(_hostingEnvironment.ContentRootPath, path);
    }

    public void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }
}