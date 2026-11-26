using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TabTabGo.Storage.FileStorage.Services;
using TabTabGo.Storage.Services;

namespace TabTabGo.Storage.FileStorage;

/// <summary>Dependency-injection entry points for the file-system provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the file-system provider, binding <see cref="PathSettings"/> from a configuration section.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="section">The <c>PathSettings</c> section.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoFileStorage(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        services.AddOptions<PathSettings>().Bind(section);
        return AddCore(services);
    }

    /// <summary>Registers the file-system provider with code-based <see cref="PathSettings"/>.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Configures the settings.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoFileStorage(this IServiceCollection services,
        Action<PathSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<PathSettings>().Configure(configure);
        return AddCore(services);
    }

    private static IServiceCollection AddCore(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPathService, PathService>();
        services.TryAddSingleton<FileStorageProvider>();
        services.TryAddSingleton<IStorageProvider>(sp => sp.GetRequiredService<FileStorageProvider>());
        return services;
    }
}
