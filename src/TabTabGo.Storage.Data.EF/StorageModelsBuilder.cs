using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using File = TabTabGo.Storage.Entities.File;
using TabTabGo.Storage.Enums;

namespace TabTabGo.Storage.Data.EF;

/// <summary>
/// Maps <see cref="File"/>. The mapping is identical to 6.0.8.4 (table <c>Files</c>, same columns, types and
/// nullability); a golden-model test enforces it. This library ships no migrations.
/// </summary>
public class StorageModelsBuilder
{
    /// <summary>Maps <see cref="File"/> exactly as 6.0.8.4 did.</summary>
    /// <param name="modelBuilder">Model builder.</param>
    /// <param name="schema">Accepted for compatibility and ignored, as before (applying it would move the table).</param>
    public static void OnModelCreating(ModelBuilder modelBuilder, string schema = "Storage")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<File>(mb => OnModelCreating(mb, schema));
    }

    /// <summary>Maps <see cref="File"/> exactly as 6.0.8.4 did.</summary>
    /// <param name="modelBuilder">Entity builder.</param>
    /// <param name="schema">Accepted for compatibility and ignored, as before.</param>
    public static void OnModelCreating(EntityTypeBuilder<File> modelBuilder, string schema)
    {
        OnModelCreating(modelBuilder, new StorageModelOptions());
    }

    /// <summary>Maps <see cref="File"/> with explicit options (schema, table name, comment column type).</summary>
    /// <param name="modelBuilder">Model builder.</param>
    /// <param name="options">Mapping options.</param>
    public static void OnModelCreating(ModelBuilder modelBuilder, StorageModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(options);
        modelBuilder.Entity<File>(mb => OnModelCreating(mb, options));
    }

    /// <summary>Maps <see cref="File"/> with explicit options (schema, table name, comment column type).</summary>
    /// <param name="modelBuilder">Entity builder.</param>
    /// <param name="options">Mapping options.</param>
    public static void OnModelCreating(EntityTypeBuilder<File> modelBuilder, StorageModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Schema is null)
        {
            modelBuilder.ToTable(options.TableName);
        }
        else
        {
            modelBuilder.ToTable(options.TableName, options.Schema);
        }

        modelBuilder.Property(f => f.FileId).ValueGeneratedOnAdd();
        modelBuilder.Property(f => f.ReferenceId);
        modelBuilder.Property(f => f.Title).HasMaxLength(500);
        modelBuilder.Property(f => f.ReferenceType).HasMaxLength(50);
        modelBuilder.Property(f => f.FilePath);

        modelBuilder.Property(f => f.FileExtension).HasMaxLength(10);
        modelBuilder.Property(f => f.OriginalFileName).HasMaxLength(500);
        modelBuilder.Property(f => f.OriginalMediaType).HasMaxLength(500);

        var comment = modelBuilder.Property(f => f.Comment);
        if (options.CommentColumnType is not null)
        {
            comment.HasColumnType(options.CommentColumnType);
        }

        modelBuilder.Property(f => f.IsCompressed).HasDefaultValue(true);
        modelBuilder.Property(f => f.FileType).HasColumnType("int")
            .HasConversion(f => (int)f, f => (FileType)f);

        modelBuilder.HasKey(f => f.FileId);
        TabTabGo.Data.EF.Extensions.EntityTypeBuilder.EntityBuilder(modelBuilder);

        // Core 10 makes CreatedBy/UpdatedBy nullable; EF reads that and would turn the columns optional in a
        // consumer's next migration. Pin them as required on every line so the Files table never changes.
        modelBuilder.Property(f => f.CreatedBy).IsRequired();
        modelBuilder.Property(f => f.UpdatedBy).IsRequired();
    }
}
