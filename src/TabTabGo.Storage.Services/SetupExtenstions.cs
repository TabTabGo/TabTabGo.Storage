using Microsoft.Extensions.DependencyInjection;
using TabTabGo.Storage.Services;

namespace TabTabGo.Storage.Services;

public static class SetupExtensions
{
    public static IServiceCollection RegisterStorageServices(this IServiceCollection services)
    {
        services
            .AddTransient<IFileService<TabTabGo.Storage.Entities.IFile>,
                FileService<TabTabGo.Storage.Entities.IFile>>();
        services.AddTransient<IImageService, ImageService>();
        return services;
    }
}