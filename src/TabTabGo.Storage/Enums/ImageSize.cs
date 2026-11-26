namespace TabTabGo.Storage.Enums;

/// <summary>Image size presets. <see cref="Default"/> is the stored original.</summary>
public enum ImageSize
{
    /// <summary>The stored original.</summary>
    Default,

    /// <summary>Large thumbnail (180 x 180).</summary>
    ThumbLarge,

    /// <summary>Medium thumbnail (160 x 160).</summary>
    ThumbMedium,

    /// <summary>Small thumbnail (120 x 120).</summary>
    ThumbSmall,
}
