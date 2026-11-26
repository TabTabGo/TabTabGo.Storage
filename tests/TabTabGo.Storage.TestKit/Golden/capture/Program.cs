// Captures the EF Core model of TabTabGo.Storage.Entities.File exactly as the 6.0.8.4 code (git a6c506b) builds it.
// Usage: capture <sqlite|sqlserver|extra>   (JSON on stdout, nothing else)
//   sqlite     relational metadata with the SQLite provider (in-memory connection, opened)
//   sqlserver  relational metadata with the SQL Server provider; the connection string is never used to connect
//   extra      the text a 6.x build writes to Files.ExtraProperties (Newtonsoft-era shape), via SQLite
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TabTabGo.Storage.Data.EF;

public sealed class Ctx : DbContext
{
    public Ctx(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder mb) => StorageModelsBuilder.OnModelCreating(mb);
}

public static class Program
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

    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "sqlite";
        if (mode == "sqlserver")
        {
            using var context = new Ctx(new DbContextOptionsBuilder().UseSqlServer("Server=.;Database=golden;Trusted_Connection=True;TrustServerCertificate=True").Options);
            Console.WriteLine(Model(context));
            return 0;
        }

        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var sqlite = new Ctx(new DbContextOptionsBuilder().UseSqlite(connection).Options);
        if (mode == "extra")
        {
            sqlite.Database.EnsureCreated();
            var file = new TabTabGo.Storage.Entities.File
            {
                Title = "t", ReferenceType = "r", FileExtension = ".png", OriginalFileName = "o", FilePath = "p",
                Comment = "c", OriginalMediaType = "m", CreatedBy = "u", UpdatedBy = "u",
            };
            file.ExtraProperties = new Dictionary<string, object>
            {
                ["a"] = 1, ["b"] = "x", ["d"] = new Dictionary<string, object> { ["nested"] = true },
            };
            sqlite.Add(file);
            sqlite.SaveChanges();
            using var command = connection.CreateCommand();
            command.CommandText = "select ExtraProperties from Files";
            Console.Write(command.ExecuteScalar());
            return 0;
        }

        Console.WriteLine(Model(sqlite));
        return 0;
    }

    private static string Model(DbContext context)
    {
        var entity = context.Model.FindEntityType(typeof(TabTabGo.Storage.Entities.File))!;
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
        return JsonSerializer.Serialize(new
        {
            Table = entity.GetTableName(),
            Schema = entity.GetSchema(),
            Key = entity.FindPrimaryKey()!.Properties.Select(p => p.Name),
            Properties = properties,
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
