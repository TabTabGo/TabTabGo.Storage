using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TabTabGo.Storage.AzureStorage;

/// <summary>Dependency-injection entry points for the Azure Blob provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the provider reading the legacy <c>ConnectionStrings:AzureStorageConnection</c> setting.</summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoAzureBlobStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<AzureBlobStorageOptions>();
        return AddCore(services);
    }

    /// <summary>Registers the provider, binding <see cref="AzureBlobStorageOptions"/> from a configuration section.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="section">The <c>AzureBlobStorage</c> section.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoAzureBlobStorage(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        services.AddOptions<AzureBlobStorageOptions>().Bind(section);
        return AddCore(services);
    }

    /// <summary>Registers the provider with code-based options.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoAzureBlobStorage(this IServiceCollection services,
        Action<AzureBlobStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<AzureBlobStorageOptions>().Configure(configure);
        return AddCore(services);
    }

    /// <summary>Registers the provider using a service endpoint and a token credential (managed identity).</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="serviceUri">Blob service endpoint.</param>
    /// <param name="credential">Credential chosen by the host, for example <c>DefaultAzureCredential</c>.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoAzureBlobStorage(this IServiceCollection services, Uri serviceUri,
        TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(credential);
        return services.AddTabTabGoAzureBlobStorage(o =>
        {
            o.ServiceUri = serviceUri;
            o.Credential = credential;
        });
    }

    private static IServiceCollection AddCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AzureBlobStorageOptions>, AzureBlobStorageOptionsValidator>());
        services.AddOptions<AzureBlobStorageOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<AzureBlobStorageOptions>, LegacyConnectionStringPostConfigure>());
        services.TryAddSingleton(sp => AzureBlobClientFactory.Create(sp.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value));
        // Explicit factory: the class has a second (obsolete) constructor that is also satisfiable once an
        // IConfiguration is registered, which makes constructor selection ambiguous.
        services.TryAddSingleton(sp => new BlobStorageProvider(
            sp.GetRequiredService<BlobServiceClient>(),
            sp.GetRequiredService<IOptions<AzureBlobStorageOptions>>(),
            sp.GetRequiredService<ILogger<BlobStorageProvider>>()));
        services.TryAddSingleton<IStorageProvider>(sp => sp.GetRequiredService<BlobStorageProvider>());
        return services;
    }

    // The 6.x setting stays the default source when nothing else is configured.
    private sealed class LegacyConnectionStringPostConfigure(IServiceProvider services)
        : IPostConfigureOptions<AzureBlobStorageOptions>
    {
        public void PostConfigure(string? name, AzureBlobStorageOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.ConnectionString) || options.ServiceUri is not null)
            {
                return;
            }

            var legacy = services.GetService<IConfiguration>()
                ?.GetConnectionString(AzureBlobStorageOptions.LegacyConnectionStringName);
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                options.ConnectionString = legacy;
            }
        }
    }
}
