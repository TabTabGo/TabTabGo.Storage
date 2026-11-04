namespace TabTabGo.Storage.Models;

/// <summary>Options for a streamed store.</summary>
public sealed class StorageWriteOptions
{
    /// <summary>Content type recorded on the object. Providers derive it from the extension when <c>null</c>.</summary>
    public string? ContentType { get; set; }

    /// <summary>Replace an existing object. Defaults to <c>true</c>, matching the file-system provider.</summary>
    public bool Overwrite { get; set; } = true;
}
