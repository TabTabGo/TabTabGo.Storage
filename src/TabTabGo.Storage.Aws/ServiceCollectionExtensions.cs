using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TabTabGo.Storage.Aws;

/// <summary>Dependency-injection entry points for the S3 provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the S3 provider, binding <see cref="S3Configuration"/> from a configuration section.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="section">The <c>aws</c> section.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoS3Storage(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        services.AddOptions<S3Configuration>().Bind(section);
        return AddCore(services);
    }

    /// <summary>Registers the S3 provider with code-based configuration.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddTabTabGoS3Storage(this IServiceCollection services,
        Action<S3Configuration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<S3Configuration>().Configure(configure);
        return AddCore(services);
    }

    private static IServiceCollection AddCore(IServiceCollection services)
    {
        services.TryAddSingleton<IValidateOptions<S3Configuration>, S3ConfigurationValidator>();
        services.AddOptions<S3Configuration>().ValidateOnStart();
        // One client per process; a host can register its own IAmazonS3 first to override it.
        services.TryAddSingleton<IAmazonS3>(sp => S3ClientFactory.Create(sp.GetRequiredService<IOptions<S3Configuration>>().Value));
        services.TryAddSingleton<S3StorageProvider>();
        services.TryAddSingleton<IStorageProvider>(sp => sp.GetRequiredService<S3StorageProvider>());
        return services;
    }

    private sealed class S3ConfigurationValidator : IValidateOptions<S3Configuration>
    {
        public ValidateOptionsResult Validate(string? name, S3Configuration options)
        {
            if (string.IsNullOrWhiteSpace(options.BucketName))
            {
                return ValidateOptionsResult.Fail("S3Configuration.BucketName is required.");
            }

            if (string.IsNullOrWhiteSpace(options.Region) && string.IsNullOrWhiteSpace(options.ServiceUrl))
            {
                return ValidateOptionsResult.Fail("S3Configuration.Region (or ServiceUrl) is required.");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
