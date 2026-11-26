using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TabTabGo.Storage.FileStorage;
using TabTabGo.Storage.FileStorage.Services;
using TabTabGo.Storage.Services;
using TabTabGo.Storage.TestKit;
using Xunit;

namespace TabTabGo.Storage.FileStorage.Tests;

public class PathServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private PathService Create(PathSettings settings, FakeLogger<PathService>? logger = null, string? webRoot = null) =>
        new(new TestHostEnvironment(_temp.Path, webRoot), Options.Create(settings), logger ?? new FakeLogger<PathService>());

    [Fact]
    public void PathService_ResolvesAgainstContentRoot() =>
        Assert.Equal(Path.Combine(_temp.Path, "files", "x"), Create(new PathSettings()).ResolvePath(Path.Combine("files", "x")));

    [Fact]
    public void PathService_AbsoluteRoot_Kept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere-else");

        Assert.Equal(absolute, Create(new PathSettings()).ResolvePath(absolute));
    }

    [Fact]
    public void EnsureDirectory_CreatesMissingDirectory_AndIsIdempotent()
    {
        var service = Create(new PathSettings());
        var dir = Path.Combine(_temp.Path, "a", "b");

        service.EnsureDirectory(dir);
        service.EnsureDirectory(dir);

        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void PathSettings_ExposesBoundSettings()
    {
        var settings = new PathSettings { RootPath = "r" };

        Assert.Same(settings, Create(settings).PathSettings);
    }

    [Fact]
    public void PathService_RootUnderWwwroot_LogsWarning()
    {
        var logger = new FakeLogger<PathService>();

        Create(new PathSettings { RootPath = Path.Combine("wwwroot", "uploads") }, logger);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("web root", record.Message);
    }

    [Fact]
    public void PathService_MappedFolderUnderWwwroot_LogsWarning()
    {
        var logger = new FakeLogger<PathService>();

        Create(new PathSettings { RootPath = string.Empty, FileTypePaths = new Dictionary<string, string> { ["Image"] = "wwwroot\\images" } }, logger);

        Assert.Single(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void PathService_RootUnderEnvironmentWebRoot_LogsWarning()
    {
        var logger = new FakeLogger<PathService>();
        var web = Path.Combine(_temp.Path, "public");

        Create(new PathSettings { RootPath = Path.Combine("public", "u") }, logger, web);

        Assert.Single(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void PathService_RootInASiblingFolderWithTheWebRootAsPrefix_LogsNothing()
    {
        var logger = new FakeLogger<PathService>();

        Create(new PathSettings { RootPath = "public-private" }, logger, Path.Combine(_temp.Path, "public"));

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void PathService_RootEqualToTheWebRoot_LogsWarning()
    {
        var logger = new FakeLogger<PathService>();

        Create(new PathSettings { RootPath = "public" }, logger, Path.Combine(_temp.Path, "public"));

        Assert.Single(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void PathService_RootOutsideWebRoot_LogsNothing()
    {
        var logger = new FakeLogger<PathService>();

        Create(new PathSettings { RootPath = "private-store" }, logger, Path.Combine(_temp.Path, "public"));

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        var env = new TestHostEnvironment(_temp.Path);
        var options = Options.Create(new PathSettings());
        var logger = new FakeLogger<PathService>();

        Assert.Throws<ArgumentNullException>(() => new PathService(null!, options, logger));
        Assert.Throws<ArgumentNullException>(() => new PathService(env, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new PathService(env, options, null!));
    }

    [Fact]
    public void ResolvePath_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Create(new PathSettings()).ResolvePath(null!));
}

public class FileStorageDiTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(_temp.Path));
        return services;
    }

    private static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    [Fact]
    public void AddTabTabGoFileStorage_ResolvesProviderAsSingleton_FromAction()
    {
        var services = Services();
        services.AddTabTabGoFileStorage(s => s.RootPath = "r");

        using var provider = Build(services);

        var first = provider.GetRequiredService<IStorageProvider>();
        Assert.IsType<FileStorageProvider>(first);
        Assert.Same(first, provider.GetRequiredService<IStorageProvider>());
        Assert.Same(first, provider.GetRequiredService<FileStorageProvider>());
        Assert.Equal("r", provider.GetRequiredService<IPathService>().PathSettings.RootPath);
    }

    [Fact]
    public void AddTabTabGoFileStorage_BindsFromConfigurationSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PathSettings:RootPath"] = "cfg",
            ["PathSettings:FileTypePaths:Image"] = "img",
        }).Build();
        var services = Services();
        services.AddTabTabGoFileStorage(configuration.GetSection("PathSettings"));

        using var provider = Build(services);

        var settings = provider.GetRequiredService<IOptions<PathSettings>>().Value;
        Assert.Equal("cfg", settings.RootPath);
        Assert.Equal("img", settings["image"]);
    }

    [Fact]
    public void AddTabTabGoFileStorage_DoesNotOverrideAnExistingTimeProviderOrPathService()
    {
        var services = Services();
        var custom = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        services.AddSingleton<TimeProvider>(custom);
        services.AddTabTabGoFileStorage(_ => { });

        using var provider = Build(services);

        Assert.Same(custom, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void AddTabTabGoFileStorage_NullArguments_Throw()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddTabTabGoFileStorage(_ => { }));
        Assert.Throws<ArgumentNullException>(() => services.AddTabTabGoFileStorage((Action<PathSettings>)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddTabTabGoFileStorage((IConfiguration)null!));
    }
}
