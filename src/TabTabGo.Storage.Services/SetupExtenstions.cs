using Microsoft.Extensions.DependencyInjection;

namespace TabTabGo.Storage.Services;

/// <summary>Legacy registration helpers.</summary>
public static class SetupExtensions
{
    /// <summary>Legacy registration. It registers the interface <c>IFile</c> as the entity type, which cannot resolve.</summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same collection.</returns>
    [Obsolete("Use AddTabTabGoStorageServices<TFile>()")]
    public static IServiceCollection RegisterStorageServices(this IServiceCollection services)
    {
        services
            .AddTransient<IFileService<TabTabGo.Storage.Entities.IFile>,
                FileService<TabTabGo.Storage.Entities.IFile>>();
        services.AddTransient<IImageService, ImageService>();
        return services;
    }
}
