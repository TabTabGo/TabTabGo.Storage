using Amazon;
using Amazon.Runtime;
using Amazon.S3;

namespace TabTabGo.Storage.Aws;

/// <summary>Builds the shared <see cref="IAmazonS3"/> client from <see cref="S3Configuration"/>.</summary>
public static class S3ClientFactory
{
    /// <summary>
    /// Returns static credentials when both keys are configured, otherwise <c>null</c>, which makes the SDK use its
    /// default credential chain (IAM role, environment, profile).
    /// </summary>
    /// <param name="configuration">The S3 configuration.</param>
    /// <returns>Basic credentials or <c>null</c>.</returns>
    public static AWSCredentials? ResolveCredentials(S3Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return string.IsNullOrEmpty(configuration.AccessKey) || string.IsNullOrEmpty(configuration.SecretKey)
            ? null
            : new BasicAWSCredentials(configuration.AccessKey, configuration.SecretKey);
    }

    /// <summary>Builds the client configuration (region or custom endpoint, addressing style, timeout).</summary>
    /// <param name="configuration">The S3 configuration.</param>
    /// <returns>An <see cref="AmazonS3Config"/>.</returns>
    public static AmazonS3Config CreateConfig(S3Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var config = new AmazonS3Config
        {
            ForcePathStyle = configuration.ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds > 0 ? configuration.TimeoutSeconds : 100),
        };

        if (!string.IsNullOrWhiteSpace(configuration.ServiceUrl))
        {
            config.ServiceURL = configuration.ServiceUrl;
            config.AuthenticationRegion = string.IsNullOrWhiteSpace(configuration.Region) ? "us-east-1" : configuration.Region;
            // S3-compatible services and emulators generally do not implement the newer default checksums.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(configuration.Region);
        }

        return config;
    }

    /// <summary>Creates the client. Create it once per process and share it.</summary>
    /// <param name="configuration">The S3 configuration.</param>
    /// <returns>A new client.</returns>
    public static IAmazonS3 Create(S3Configuration configuration)
    {
        var config = CreateConfig(configuration);
        var credentials = ResolveCredentials(configuration);
        return credentials is null ? new AmazonS3Client(config) : new AmazonS3Client(credentials, config);
    }
}
