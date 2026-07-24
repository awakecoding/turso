using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Turso.Data.Sqlite;

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT 1";

if (command.ExecuteScalar() is not 1L)
    throw new InvalidOperationException("The managed Turso package consumer returned an unexpected result.");

if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
        string.Equals(assembly.GetName().Name, "Turso.Raw", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("The managed Turso package consumer must not load Turso.Raw.");
}

EnsureNoNativeCompanionWasRestored();
await VerifyEntityFrameworkIntegrationAsync(connection);

Console.WriteLine("Managed package consumer succeeded.");

static async Task VerifyEntityFrameworkIntegrationAsync(SqliteConnection connection)
{
    var options = new DbContextOptionsBuilder<ManagedPackageContext>()
        .UseTurso(connection)
        .Options;

    await using var context = new ManagedPackageContext(options);
    await context.Database.EnsureCreatedAsync();
    context.Records.Add(new ManagedPackageRecord { Value = "entity-framework" });
    await context.SaveChangesAsync();

    var value = await context.Records.SingleAsync();
    if (value.Value != "entity-framework")
        throw new InvalidOperationException("The managed Entity Framework package consumer returned an unexpected result.");
}

static void EnsureNoNativeCompanionWasRestored()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var assetsPath = Path.Combine(directory.FullName, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
            continue;

        using var assetsStream = File.OpenRead(assetsPath);
        using var assets = JsonDocument.Parse(assetsStream);
        var nativePackage = assets.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Select(library => library.Name)
            .FirstOrDefault(IsNativeCompanionPackage);
        if (nativePackage is not null)
        {
            throw new InvalidOperationException(
                $"The managed Turso package consumer must not restore native companion package {nativePackage}.");
        }

        return;
    }

    throw new FileNotFoundException("Could not locate the managed consumer restore graph.");
}

static bool IsNativeCompanionPackage(string packageIdentity)
    => packageIdentity.StartsWith("Turso.Raw/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Native/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sync/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sqlite.Native/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sqlite.NativeAot", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sqlite.Sync/", StringComparison.OrdinalIgnoreCase);

sealed class ManagedPackageContext(DbContextOptions<ManagedPackageContext> options) : DbContext(options)
{
    public DbSet<ManagedPackageRecord> Records => Set<ManagedPackageRecord>();
}

sealed class ManagedPackageRecord
{
    public int Id { get; init; }

    public required string Value { get; init; }
}
