namespace TabTabGo.Storage.Enums;

/// <summary>Kind of stored content. Stored as <c>int</c>; the numeric values are part of the schema.</summary>
public enum FileType
{
    /// <summary>Unclassified content.</summary>
    Unknown = 0,

    /// <summary>Image; thumbnails are generated.</summary>
    Image = 10,

    /// <summary>Audio.</summary>
    Audio = 20,

    /// <summary>Video.</summary>
    Video = 30,

    /// <summary>Plain text.</summary>
    Text = 40,

    /// <summary>PDF document.</summary>
    PDF = 41,

    /// <summary>Word document.</summary>
    WordDocument = 42,
}
