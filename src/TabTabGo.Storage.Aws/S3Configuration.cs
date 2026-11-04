namespace TabTabGo.Storage.Aws;

/// <summary>
/// S3 provider configuration. Bind it from the <c>aws</c> configuration section. Prefer an IAM role or the default
/// AWS credential chain: leave <see cref="AccessKey"/> and <see cref="SecretKey"/> empty.
/// </summary>
public class S3Configuration
{
    /// <summary>Optional static access key id. Leave empty to use the default credential chain.</summary>
    public string? AccessKey { get; set; }

    /// <summary>Optional static secret access key. Never logged. Leave empty to use the default credential chain.</summary>
    public string? SecretKey { get; set; }

    /// <summary>AWS region system name, for example <c>eu-west-1</c>. Required unless <see cref="ServiceUrl"/> is set.</summary>
    public string? Region { get; set; }

    /// <summary>Bucket that holds every container as a key prefix.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>Default lifetime in minutes of presigned URLs.</summary>
    public int ExpirationMinutes { get; set; } = 60;

    /// <summary>Custom endpoint for S3-compatible services and emulators (LocalStack, MinIO).</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Use path-style addressing (needed by most emulators).</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 100;
}
