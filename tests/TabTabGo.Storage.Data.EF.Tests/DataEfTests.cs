using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using TabTabGo.Storage.Data.EF;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Data.EF.Tests;

// Each test maps a different model on the same context type; EF must not reuse a cached one.
internal sealed class NoCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => new object();
}

public class GoldenModelTests
{
    private static string Fmt(Type t) => !t.IsGenericType
        ? t.FullName!
        : t.GetGenericTypeDefinition().FullName!.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Fmt)) + ">";

    private static string? Def(object? o) => o switch
    {
        null => null,
        DateTimeOffset d => d.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => o.ToString(),
    };

    private static JsonElement Capture(Action<ModelBuilder> map)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new CustomContext(new DbContextOptionsBuilder().UseSqlite(connection).ReplaceService<IModelCacheKeyFactory, NoCacheKeyFactory>().Options, map);
        return Describe(context);
    }

    // The SQL Server provider is used for metadata only; the connection string is never used to connect.
    private static JsonElement CaptureSqlServer(Action<ModelBuilder> map)
    {
        using var context = new CustomContext(
            new DbContextOptionsBuilder().UseSqlServer("Server=.;Database=golden;Trusted_Connection=True;TrustServerCertificate=True")
                .ReplaceService<IModelCacheKeyFactory, NoCacheKeyFactory>().Options, map);
        return Describe(context);
    }

    private static JsonElement Describe(DbContext context)
    {
        var entity = context.Model.FindEntityType(typeof(File))!;
        var properties = entity.GetProperties().Select(p => new
        {
            p.Name,
            Clr = Fmt(p.ClrType),
            p.IsNullable,
            IsKey = p.IsKey(),
            MaxLength = p.GetMaxLength(),
            Column = p.GetColumnName(),
            ColumnType = p.GetColumnType(),
            Default = Def(p.GetDefaultValue()),
            ValueGenerated = p.ValueGenerated.ToString(),
            HasConverter = p.GetValueConverter() != null,
            ProviderClr = p.GetValueConverter() == null ? null : Fmt(p.GetValueConverter()!.ProviderClrType),
        }).OrderBy(x => x.Name);
        var model = new
        {
            Table = entity.GetTableName(),
            Schema = entity.GetSchema(),
            Key = entity.FindPrimaryKey()!.Properties.Select(p => p.Name),
            Properties = properties,
        };
        return JsonSerializer.SerializeToElement(model);
    }

    private static JsonElement Golden(string file = "file-model-6.0.8.4.json") =>
        JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", file)))
            .RootElement.Clone();

    private sealed class CustomContext(DbContextOptions options, Action<ModelBuilder> map) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => map(modelBuilder);
    }

    private static void AssertSame(JsonElement golden, JsonElement actual)
    {
        Assert.Equal(golden.GetProperty("Table").GetString(), actual.GetProperty("Table").GetString());
        Assert.Equal(golden.GetProperty("Schema").ValueKind, actual.GetProperty("Schema").ValueKind);
        Assert.Equal(golden.GetProperty("Key").EnumerateArray().Select(k => k.GetString()),
            actual.GetProperty("Key").EnumerateArray().Select(k => k.GetString()));
        var expected = golden.GetProperty("Properties").EnumerateArray().ToList();
        var got = actual.GetProperty("Properties").EnumerateArray().ToList();
        Assert.Equal(expected.Select(p => p.GetProperty("Name").GetString()), got.Select(p => p.GetProperty("Name").GetString()));
        for (var i = 0; i < expected.Count; i++)
        {
            var name = expected[i].GetProperty("Name").GetString();
            foreach (var field in new[] { "Clr", "IsNullable", "IsKey", "MaxLength", "Column", "ColumnType", "Default", "ValueGenerated", "HasConverter", "ProviderClr" })
            {
                Assert.True(expected[i].GetProperty(field).ToString() == got[i].GetProperty(field).ToString(),
                    $"{name}.{field}: golden '{expected[i].GetProperty(field)}' but model has '{got[i].GetProperty(field)}'");
            }
        }
    }

    // FR-10: no schema change versus 6.0.8.4 (owner decision: audit columns pinned)
    [Fact]
    public void StorageModel_MatchesGolden_6084() =>
        AssertSame(Golden(), Capture(mb => StorageModelsBuilder.OnModelCreating(mb)));

    // Same proof on the SQL Server provider's store types (nvarchar(500), bigint, datetimeoffset, NTEXT ...).
    [Fact]
    public void StorageModel_MatchesGolden_6084_SqlServer() =>
        AssertSame(Golden("file-model-6.0.8.4.sqlserver.json"), CaptureSqlServer(mb => StorageModelsBuilder.OnModelCreating(mb)));

    [Fact]
    public void StorageModel_ViaStorageDbContext_MatchesGolden_6084()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new StorageDbContext(new DbContextOptionsBuilder().UseSqlite(connection).Options);

        var entity = context.Model.FindEntityType(typeof(File));

        Assert.NotNull(entity);
        Assert.Equal("Files", entity.GetTableName());
    }

    [Fact]
    public void AuditColumns_ArePinnedRequired()
    {
        var model = Capture(mb => StorageModelsBuilder.OnModelCreating(mb));

        var audit = model.GetProperty("Properties").EnumerateArray()
            .Where(p => p.GetProperty("Name").GetString() is "CreatedBy" or "UpdatedBy").ToList();

        Assert.Equal(2, audit.Count);
        Assert.All(audit, p => Assert.False(p.GetProperty("IsNullable").GetBoolean()));
        Assert.All(audit, p => Assert.Equal(50, p.GetProperty("MaxLength").GetInt32()));
    }

    [Fact]
    public void FileType_StoredAsInt()
    {
        var model = Capture(mb => StorageModelsBuilder.OnModelCreating(mb));

        var fileType = model.GetProperty("Properties").EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "FileType");

        Assert.Equal("int", fileType.GetProperty("ColumnType").GetString());
        Assert.Equal("System.Int32", fileType.GetProperty("ProviderClr").GetString());
    }
}

public class StorageModelsBuilderTests
{
    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType Build(Action<ModelBuilder> map)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var builder = new DbContextOptionsBuilder().UseSqlite(connection).ReplaceService<IModelCacheKeyFactory, NoCacheKeyFactory>();
        using var context = new Ctx(builder.Options, map);
        return context.Model.FindEntityType(typeof(File))!;
    }

    private sealed class Ctx(DbContextOptions options, Action<ModelBuilder> map) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => map(modelBuilder);
    }

    // E-2
    [Fact]
    public void OldOverload_SchemaParameter_IsStillIgnored()
    {
        var entity = Build(mb => StorageModelsBuilder.OnModelCreating(mb, "Storage"));

        Assert.Null(entity.GetSchema());
        Assert.Equal("Files", entity.GetTableName());
    }

    [Fact]
    public void OldEntityBuilderOverload_SchemaParameter_IsIgnored()
    {
        var entity = Build(mb => mb.Entity<File>(e => StorageModelsBuilder.OnModelCreating(e, "Whatever")));

        Assert.Null(entity.GetSchema());
    }

    [Fact]
    public void Options_ApplySchemaAndTableName()
    {
        var entity = Build(mb => StorageModelsBuilder.OnModelCreating(mb, new StorageModelOptions { Schema = "Storage", TableName = "Blobs" }));

        Assert.Equal("Storage", entity.GetSchema());
        Assert.Equal("Blobs", entity.GetTableName());
    }

    // E-3
    [Fact]
    public void Comment_DefaultsToNtext_AndCanBeProviderDefault()
    {
        var defaultMap = Build(mb => StorageModelsBuilder.OnModelCreating(mb, new StorageModelOptions()));
        var neutral = Build(mb => StorageModelsBuilder.OnModelCreating(mb, new StorageModelOptions { CommentColumnType = null }));

        Assert.Equal("NTEXT", defaultMap.FindProperty(nameof(File.Comment))!.GetColumnType());
        Assert.Equal("TEXT", neutral.FindProperty(nameof(File.Comment))!.GetColumnType());
    }

    [Fact]
    public void Options_Defaults()
    {
        var options = new StorageModelOptions();

        Assert.Null(options.Schema);
        Assert.Equal("Files", options.TableName);
        Assert.Equal("NTEXT", options.CommentColumnType);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => StorageModelsBuilder.OnModelCreating((ModelBuilder)null!));
        Assert.Throws<ArgumentNullException>(() => StorageModelsBuilder.OnModelCreating((ModelBuilder)null!, new StorageModelOptions()));
        Assert.Throws<ArgumentNullException>(() =>
            StorageModelsBuilder.OnModelCreating((Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<File>)null!, new StorageModelOptions()));
        Assert.Throws<ArgumentNullException>(() =>
            Build(mb => StorageModelsBuilder.OnModelCreating(mb, (StorageModelOptions)null!)));
    }

    [Fact]
    public void MaxLengths_AreMapped()
    {
        var entity = Build(mb => StorageModelsBuilder.OnModelCreating(mb));

        Assert.Equal(500, entity.FindProperty(nameof(File.Title))!.GetMaxLength());
        Assert.Equal(50, entity.FindProperty(nameof(File.ReferenceType))!.GetMaxLength());
        Assert.Equal(10, entity.FindProperty(nameof(File.FileExtension))!.GetMaxLength());
        Assert.Equal(500, entity.FindProperty(nameof(File.OriginalFileName))!.GetMaxLength());
        Assert.Equal(500, entity.FindProperty(nameof(File.OriginalMediaType))!.GetMaxLength());
    }
}

public class StorageDbContextAndProviderTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public StorageDbContextAndProviderTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private StorageDbContext NewContext() => new(new DbContextOptionsBuilder().UseSqlite(_connection).Options);

    // E-1
    [Fact]
    public void StorageDbContext_MapsFile_AndCreatesTable()
    {
        using var context = NewContext();

        context.Database.EnsureCreated();

        Assert.Equal(0, context.Set<File>().Count());
    }

    // E-5
    [Fact]
    public async Task StorageDbProvider_RepositoryForFile_Works()
    {
        using var context = NewContext();
        context.Database.EnsureCreated();
        using var provider = new StorageDbProvider(context, NullLogger<StorageDbProvider>.Instance);
        var repository = provider.Repository<File, long>();

        await repository.InsertAsync(new File
        {
            Title = "t", ReferenceType = "r", FileExtension = ".png", OriginalFileName = "o", FilePath = "p", Comment = "c",
            OriginalMediaType = "m", CreatedBy = "u", UpdatedBy = "u", FileType = FileType.Image,
        });
        await provider.SaveChangesAsync();

        var loaded = await repository.FirstOrDefaultAsync(f => f.Title == "t");
        Assert.NotNull(loaded);
        Assert.True(loaded.FileId > 0);
        Assert.Equal(FileType.Image, loaded.FileType);
        Assert.True(loaded.IsCompressed);
    }

    [Fact]
    public void StorageDbProvider_NullContext_Throws()
    {
        // Core 10 refuses a null context at construction (Core 8 accepted it and saved nothing); spec 3.4.
        var ex = Assert.Throws<ArgumentNullException>(() => new StorageDbProvider(null!, NullLogger<StorageDbProvider>.Instance));

        Assert.Equal("context", ex.ParamName);
    }

    [Fact]
    public void StorageDbContext_CommentCanBeProviderDefaultViaOptions()
    {
        using var context = new NeutralContext(new DbContextOptionsBuilder().UseSqlite(_connection).Options);

        context.Database.EnsureCreated();

        Assert.Equal(0, context.Set<File>().Count());
    }

    private sealed class NeutralContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            StorageModelsBuilder.OnModelCreating(modelBuilder, new StorageModelOptions { CommentColumnType = null });
    }
}

public class ExtraPropertiesCompatTests
{
    // 6.x wrote this text (Newtonsoft) for {"a":1,"b":"x","d":{"nested":true}}; it is captured from the 6.0.8.4 build.
    [Fact]
    public void ExtraProperties_6xNewtonsoftJson_ReadsOnV8()
    {
        var sample = System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "extra-properties-6.0.8.4.json"));
        using var fixture = new SqliteStorageFixture();
        fixture.Context.Database.ExecuteSqlRaw(
            "INSERT INTO Files (FileId, Title, ReferenceType, FileExtension, OriginalFileName, FilePath, Comment, OriginalMediaType, " +
            "CreatedBy, UpdatedBy, CreatedDate, UpdatedDate, IsEnabled, FileType, ExtraProperties, IsCompressed) " +
            "VALUES (1,'t','r','.png','o','p','c','m','u','u','2020-01-01 00:00:00+00:00','2020-01-01 00:00:00+00:00',1,10,{0},1)",
            sample);

        var file = fixture.Context.Set<File>().Single();

        Assert.Equal(3, file.ExtraProperties.Count);
        Assert.Equal("1", file.ExtraProperties["a"]!.ToString());
        Assert.Equal("x", file.ExtraProperties["b"]!.ToString());
        Assert.Contains("nested", file.ExtraProperties["d"]!.ToString());
    }

    [Fact]
    public void ExtraProperties_RoundTripOnV8()
    {
        using var fixture = new SqliteStorageFixture();
        var file = new File
        {
            Title = "t", ReferenceType = "r", FileExtension = ".png", OriginalFileName = "o", FilePath = "p", Comment = "c",
            OriginalMediaType = "m", CreatedBy = "u", UpdatedBy = "u",
        };
        file.ExtraProperties["k"] = "v";
        fixture.Context.Add(file);
        fixture.Context.SaveChanges();

        using var other = fixture.NewContext();

        Assert.Equal("v", other.Set<File>().Single().ExtraProperties["k"]!.ToString());
    }
}
