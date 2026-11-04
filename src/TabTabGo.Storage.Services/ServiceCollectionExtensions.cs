using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TabTabGo.Storage.Entities;

namespace TabTabGo.Storage.Services;

/// <summary>Dependency-injection entry points for the storage services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <c>IFileService&lt;TFile&gt;</c> and <see cref="IImageService"/>. The host registers an
    /// <c>IStorageProvider</c> (one of the <c>AddTabTabGo…Storage</c> adapters) and an <c>IUnitOfWork</c>.
    /// </summary>
    /// <typeparam name="TFile">File entity type (for example <see cref="Entities.File"/>).</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Upload rules.</param>
    /// <param name="configureImages">Image options.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoStorageServices<TFile>(this IServiceCollection services,
        Action<StorageServiceOptions>? configure = null, Action<ImageServiceOptions>? configureImages = null)
        where TFile : class, IFile
    {
        ArgumentNullException.ThrowIfNull(services);
        var storage = services.AddOptions<StorageServiceOptions>();
        if (configure is not null)
        {
            storage.Configure(configure);
        }

        var images = services.AddOptions<ImageServiceOptions>();
        if (configureImages is not null)
        {
            images.Configure(configureImages);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IImageService, ImageService>();
        services.TryAddScoped<IFileService<TFile>, FileService<TFile>>();
        return services;
    }
}
