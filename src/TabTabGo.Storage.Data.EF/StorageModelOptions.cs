namespace TabTabGo.Storage.Data.EF;

/// <summary>
/// Options for <see cref="StorageModelsBuilder"/>. Defaults reproduce the 6.0.8.4 mapping; changing any of them
/// changes the database schema and is the consumer's own migration.
/// </summary>
public sealed class StorageModelOptions
{
    /// <summary>Database schema. <c>null</c> (default) leaves the provider default, as 6.0.8.4 did.</summary>
    public string? Schema { get; set; }

    /// <summary>Table name. Defaults to <c>Files</c>.</summary>
    public string TableName { get; set; } = "Files";

    /// <summary>
    /// Column type of <c>Comment</c>. Defaults to <c>NTEXT</c> (SQL Server only, deprecated there). Set to
    /// <c>null</c> on other databases to use the provider default.
    /// </summary>
    public string? CommentColumnType { get; set; } = "NTEXT";
}
