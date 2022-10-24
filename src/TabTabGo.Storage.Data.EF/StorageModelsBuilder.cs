using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;

namespace TabTabGo.Storage.Data.EF;

public class StorageModelsBuilder
{
    public static void OnModelCreating(ModelBuilder modelBuilder, string schema = "Storage")
    {
        modelBuilder.Entity<File>(mb => OnModelCreating(mb, schema));
    }

    public static void OnModelCreating(EntityTypeBuilder<File> modelBuilder, string schema)
    {
        modelBuilder.ToTable("Files");
        //Map Properties
        modelBuilder.Property(f => f.FileId).ValueGeneratedOnAdd();
        modelBuilder.Property(f => f.ReferenceId);
        modelBuilder.Property(f => f.Title).HasMaxLength(500);
        modelBuilder.Property(f => f.ReferenceType).HasMaxLength(50);
        modelBuilder.Property(f => f.FilePath);

        modelBuilder.Property(f => f.FileExtension).HasMaxLength(10);
        modelBuilder.Property(f => f.OriginalFileName).HasMaxLength(500);
        modelBuilder.Property(f => f.OriginalMediaType).HasMaxLength(500);

        modelBuilder.Property(f => f.Comment).HasColumnType("NTEXT");

        modelBuilder.Property(f => f.IsCompressed).HasDefaultValue(true);
        modelBuilder.Property(f => f.FileType).HasColumnType("int")
            .HasConversion(f => (int)f, f => (FileType)f);


        modelBuilder.HasKey(f => f.FileId);
        //Entity Builder
        TabTabGo.Data.EF.Extensions.EntityTypeBuilder.EntityBuilder(modelBuilder);

        //Relationship
    }
}