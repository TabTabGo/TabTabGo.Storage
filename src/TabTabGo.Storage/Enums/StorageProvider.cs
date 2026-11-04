namespace TabTabGo.Storage.Enums;

/// <summary>Identifies a storage backend. Shares its simple name with the abstract <c>StorageProvider</c> class.</summary>
public enum StorageProvider
{
    /// <summary>Local file system.</summary>
    Local = 0,

    /// <summary>Azure Blob Storage.</summary>
    AzureBlob = 10,

    /// <summary>Azure Files (declared, never implemented).</summary>
    AzureFile = 12,

    /// <summary>AWS S3.</summary>
    AwsS3 = 20
}
