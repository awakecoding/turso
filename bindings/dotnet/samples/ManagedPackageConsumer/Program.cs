using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Turso.Data.Sqlite;

var databasePath = Path.Combine(AppContext.BaseDirectory, $"managed-package-{Guid.NewGuid():N}.db");
using var connection = new SqliteConnection(
    $"Data Source={databasePath};Pooling=True;Local Provider=Managed");
try
{
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1";

    if (command.ExecuteScalar() is not 1L)
        throw new InvalidOperationException("The managed Turso package consumer returned an unexpected result.");

    connection.Close();
    connection.Open();
    if (command.ExecuteScalar() is not 1L)
        throw new InvalidOperationException("The managed Turso package pool returned an unexpected result.");

    SqliteConnection.ClearPool(connection);
    connection.Close();
    SqliteConnection.ClearAllPools();

    var options = new DbContextOptionsBuilder<ConsumerContext>()
        .UseTurso(connection)
        .Options;
    using (var context = new ConsumerContext(options))
    {
        context.Database.EnsureCreated();
        context.Records.Add(new ConsumerRecord { Id = 1, Value = "packaged" });
        context.SaveChanges();

        if (context.Records.Single().Value != "packaged")
            throw new InvalidOperationException("The packaged Turso EF Core provider returned an unexpected result.");
    }

    if (typeof(DbContext).Assembly.GetName().Version?.Major != 9)
        throw new InvalidOperationException("The managed Turso package consumer must run against EF Core 9.x.");

    try
    {
        _ = new DbContextOptionsBuilder<ConsumerContext>()
            .UseTurso("Data Source=libsql://example-org.turso.io");
        throw new InvalidOperationException("UseTurso must reject remote URLs during configuration.");
    }
    catch (NotSupportedException exception) when (
        exception.Message.Contains("retry and transaction semantics", StringComparison.Ordinal))
    {
    }

    const string encryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    var encryptedPath = Path.Combine(Path.GetTempPath(), $"turso-managed-package-{Guid.NewGuid():N}.db");
    try
    {
        var encryptedConnectionString =
            $"Data Source={encryptedPath};Local Provider=Managed;Encryption Cipher=AES256GCM;Encryption Key={encryptionKey}";
        using (var encrypted = new SqliteConnection(encryptedConnectionString))
        {
            encrypted.Open();
            encrypted.ExecuteNonQuery("CREATE TABLE encrypted_data(value TEXT); INSERT INTO encrypted_data VALUES ('package');");
        }

        using (var reopened = new SqliteConnection(encryptedConnectionString))
        {
            reopened.Open();
            if (reopened.ExecuteScalar<string>("SELECT value FROM encrypted_data;") != "package")
                throw new InvalidOperationException("The managed package did not reopen its encrypted database.");
        }

        using var unsupported = new SqliteConnection(
            $"Data Source={encryptedPath};Local Provider=Managed;Encryption Cipher=AEGIS256;Encryption Key={encryptionKey}");
        try
        {
            unsupported.Open();
            throw new InvalidOperationException("The managed package accepted an unsupported encryption cipher.");
        }
        catch (NotSupportedException exception) when (
            exception.Message.Contains("cipher ID 1", StringComparison.Ordinal)
            && exception.Message.Contains("cipher ID 2", StringComparison.Ordinal))
        {
        }
    }
    finally
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            File.Delete(encryptedPath + suffix);
    }

    if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            string.Equals(assembly.GetName().Name, "Turso.Raw", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("The managed Turso package consumer must not load Turso.Raw.");
    }

    EnsureNoNativeCompanionWasRestored();
    await VerifyEntityFrameworkIntegrationAsync(connection);

    Console.WriteLine(
        $"Managed package consumer succeeded on {AppContext.TargetFrameworkName} with EF Core {typeof(DbContext).Assembly.GetName().Version}.");
}
finally
{
    connection.Close();
    SqliteConnection.ClearAllPools();
    DeleteDatabase(databasePath);
}

static async Task VerifyEntityFrameworkIntegrationAsync(SqliteConnection connection)
{
    var options = new DbContextOptionsBuilder<ManagedPackageContext>()
        .UseTurso(connection)
        .Options;

    await using var context = new ManagedPackageContext(options);
    await context.Database.EnsureCreatedAsync();
    context.Records.Add(new ManagedPackageRecord { Value = "entity-framework" });
    await context.SaveChangesAsync();

    var value = await context.Records.SingleAsync(record => record.Value == "entity-framework");
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

static void DeleteDatabase(string path)
{
    foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
    {
        var candidate = path + suffix;
        if (File.Exists(candidate))
            File.Delete(candidate);
    }
}

sealed class ConsumerContext(DbContextOptions<ConsumerContext> options) : DbContext(options)
{
    public DbSet<ConsumerRecord> Records => Set<ConsumerRecord>();
}

sealed class ConsumerRecord
{
    public int Id { get; set; }

    public required string Value { get; set; }
}

sealed class ManagedPackageContext(DbContextOptions<ManagedPackageContext> options) : DbContext(options)
{
    public DbSet<ManagedPackageRecord> Records => Set<ManagedPackageRecord>();
}

sealed class ManagedPackageRecord
{
    public int Id { get; init; }

    public required string Value { get; init; }
}
