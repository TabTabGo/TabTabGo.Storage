using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace TabTabGo.Storage.AzureStorage;

/// <summary>
/// Azure Blob provider configuration. Use either a <see cref="ConnectionString"/> (legacy; an account key in
/// configuration) or a <see cref="ServiceUri"/> with a host-supplied <see cref="Credential"/> such as a managed
/// identity (preferred; no secret in configuration, and SAS URLs use a user-delegation key).
/// </summary>
public class AzureBlobStorageOptions
{
    /// <summary>Name of the legacy connection string read when no other source is configured.</summary>
    public const string LegacyConnectionStringName = "AzureStorageConnection";

    /// <summary>Storage connection string. Falls back to <c>ConnectionStrings:AzureStorageConnection</c>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Blob service endpoint, for example <c>https://account.blob.core.windows.net</c>.</summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>Token credential used with <see cref="ServiceUri"/>. Set in code; it cannot come from configuration.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>Default SAS lifetime.</summary>
    public TimeSpan DefaultSasExpiry { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Lower-case blob names (existing blobs are lower-case). Disable for new, case-preserving deployments.</summary>
    public bool LowercaseBlobNames { get; set; } = true;
}

/// <summary>Validates <see cref="AzureBlobStorageOptions"/> at startup.</summary>
public sealed class AzureBlobStorageOptionsValidator : IValidateOptions<AzureBlobStorageOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AzureBlobStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ValidateOptionsResult.Success;
        }

        if (options.ServiceUri is not null && options.Credential is not null)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            "Configure AzureBlobStorage:ConnectionString (or ConnectionStrings:AzureStorageConnection), " +
            "or ServiceUri together with a Credential.");
    }
}

/// <summary>Creates the shared <see cref="BlobServiceClient"/> from <see cref="AzureBlobStorageOptions"/>.</summary>
public static class AzureBlobClientFactory
{
    /// <summary>Creates the client. Create it once per process and share it.</summary>
    /// <param name="options">Validated options.</param>
    /// <returns>A new client.</returns>
    /// <exception cref="InvalidOperationException">No usable source is configured.</exception>
    public static BlobServiceClient Create(AzureBlobStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobServiceClient(options.ConnectionString);
        }

        if (options.ServiceUri is not null && options.Credential is not null)
        {
            return new BlobServiceClient(options.ServiceUri, options.Credential);
        }

        throw new InvalidOperationException(
            "Azure Blob storage is not configured: set a connection string, or ServiceUri with a Credential.");
    }
}
