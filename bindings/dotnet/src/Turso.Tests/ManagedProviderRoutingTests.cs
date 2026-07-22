using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedProviderRoutingTests
{
    [Test]
    public void ConnectionStringBuildersDefaultToNativeAndParseManagedLocalProvider()
    {
        new TursoConnectionStringBuilder().LocalProvider.Should().Be(TursoLocalProvider.Native);
        new SqliteConnectionStringBuilder().LocalProvider.Should().Be(TursoLocalProvider.Native);

        var tursoBuilder = new TursoConnectionStringBuilder("LocalProvider=Managed");
        var sqliteBuilder = new SqliteConnectionStringBuilder("Local Provider=Managed");

        tursoBuilder.LocalProvider.Should().Be(TursoLocalProvider.Managed);
        sqliteBuilder.LocalProvider.Should().Be(TursoLocalProvider.Managed);
    }

    [Test]
    public void TursoConnectionRoutesExplicitManagedProviderWithoutNativeInterop()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 42;";
        command.ExecuteScalar().Should().Be(42L);
    }

    [Test]
    public void SqliteConnectionRoutesExplicitManagedProviderWithoutNativeInterop()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        connection.ExecuteScalar<long>("SELECT 42;").Should().Be(42);
    }

    [Test]
    public void ManagedProviderSupportsSavepointsAcrossNestingReleaseAndRollback()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE t(value INTEGER);");

        using var transaction = connection.BeginTransaction();
        transaction.SupportsSavepoints.Should().BeTrue();

        transaction.Save("sp1");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        transaction.Save("sp2");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (2);");

        // ROLLBACK TO keeps sp2 alive and discards only row 2.
        transaction.Rollback("sp2");
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(1);

        // RELEASE folds sp1's remaining work into the outer transaction, which then commits.
        transaction.Release("sp1");
        transaction.Commit();

        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(1);
    }

    [Test]
    public void ManagedProviderSavepointChangesAreDiscardedByOuterRollback()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE t(value INTEGER);");

        using (var transaction = connection.BeginTransaction())
        {
            transaction.Save("checkpoint");
            connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
            transaction.Rollback();
        }

        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(0);
    }

    [Test]
    public void TursoConnectionPersistsManagedFileDatabaseWithoutNativeFallback()
    {
        var path = CreateManagedDatabasePath();
        try
        {
            using (var connection = new TursoConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();

                using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);";
                create.ExecuteNonQuery();

                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO t VALUES (7, 'seven');";
                insert.ExecuteNonQuery();
            }

            File.Exists(path).Should().BeTrue("the managed engine must persist to a real file, not fall back to native or memory");

            using (var reopened = new TursoConnection($"Data Source={path};Local Provider=Managed"))
            {
                reopened.Open();

                using var query = reopened.CreateCommand();
                query.CommandText = "SELECT name FROM t WHERE id = 7;";
                query.ExecuteScalar().Should().Be("seven");
            }
        }
        finally
        {
            DeleteManagedDatabase(path);
        }
    }

    [Test]
    public void SqliteConnectionPersistsManagedFileDatabase()
    {
        var path = CreateManagedDatabasePath();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteNonQuery("CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT);");
                connection.ExecuteNonQuery("INSERT INTO t VALUES (7, 'seven');");
            }

            File.Exists(path).Should().BeTrue("the managed engine must persist to a real file, not fall back to native or memory");

            using (var reopened = new SqliteConnection($"Data Source={path};Local Provider=Managed"))
            {
                reopened.Open();
                reopened.ExecuteScalar<string>("SELECT name FROM t WHERE id = 7;").Should().Be("seven");
            }
        }
        finally
        {
            DeleteManagedDatabase(path);
        }
    }

    [Test]
    public void ManagedFileBackupRejectsNonemptyDestinationWithoutMutatingIt()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using var source = new SqliteConnection($"Data Source={sourcePath};Local Provider=Managed");
            using var destination = new SqliteConnection($"Data Source={destinationPath};Local Provider=Managed");
            source.Open();
            destination.Open();
            source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
            destination.ExecuteNonQuery("CREATE TABLE destination_data(value TEXT); INSERT INTO destination_data VALUES ('destination');");

            Assert.Throws<InvalidOperationException>(() => source.BackupDatabase(destination))!
                .Message.Should().Be("BackupDatabase requires an empty destination when Local Provider=Managed.");

            destination.State.Should().Be(System.Data.ConnectionState.Open);
            destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(1);
            destination.ExecuteScalar<string>("SELECT value FROM destination_data;").Should().Be("destination");
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedInMemoryBackupCopiesToAnEmptyDestinationAndLeavesConnectionsUsable()
    {
        using var source = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        using var destination = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        source.Open();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");

        destination.State.Should().Be(System.Data.ConnectionState.Closed);
        source.BackupDatabase(destination);

        source.State.Should().Be(System.Data.ConnectionState.Open);
        destination.State.Should().Be(System.Data.ConnectionState.Open);
        destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");

        source.Dispose();
        source.State.Should().Be(System.Data.ConnectionState.Closed);
        destination.State.Should().Be(System.Data.ConnectionState.Open);
        destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");

        destination.Dispose();
        destination.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Test]
    public void ManagedBackupRejectsMixedProvidersWithoutCopying()
    {
        NativeProviderTestFixture.EnsureRegistered();

        using var managedSource = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        using var nativeDestination = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
        managedSource.Open();
        managedSource.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('managed');");

        Assert.Throws<NotSupportedException>(() => managedSource.BackupDatabase(nativeDestination))!
            .Message.Should().Be("BackupDatabase does not support copying between managed and native providers.");

        managedSource.State.Should().Be(System.Data.ConnectionState.Open);
        nativeDestination.State.Should().Be(System.Data.ConnectionState.Open);
        nativeDestination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        managedSource.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("managed");
    }

    [Test]
    public void NativeToManagedBackupRejectsMixedProvidersWithoutCopying()
    {
        NativeProviderTestFixture.EnsureRegistered();

        using var nativeSource = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
        using var managedDestination = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        nativeSource.Open();
        nativeSource.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('native');");

        Assert.Throws<NotSupportedException>(() => nativeSource.BackupDatabase(managedDestination))!
            .Message.Should().Be("BackupDatabase does not support copying between managed and native providers.");

        nativeSource.State.Should().Be(System.Data.ConnectionState.Open);
        managedDestination.State.Should().Be(System.Data.ConnectionState.Open);
        managedDestination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        nativeSource.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("native");
    }

    [TestCase("attached", "main")]
    [TestCase("main", "attached")]
    public void ManagedBackupRejectsNonMainDatabaseNames(string destinationName, string sourceName)
    {
        using var source = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        using var destination = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        source.Open();
        destination.Open();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");

        Assert.Throws<NotSupportedException>(() => source.BackupDatabase(destination, destinationName, sourceName))!
            .Message.Should().Be("BackupDatabase supports only the main database when Local Provider=Managed.");

        source.State.Should().Be(System.Data.ConnectionState.Open);
        destination.State.Should().Be(System.Data.ConnectionState.Open);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
    }

    [Test]
    public void ManagedBackupRejectsTheSameConnection()
    {
        using var source = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        source.Open();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");

        var exception = Assert.Throws<ArgumentException>(() => source.BackupDatabase(source));

        exception!.Message.Should().Be(
            "BackupDatabase requires distinct source and destination connections when Local Provider=Managed. (Parameter 'destination')");
        source.State.Should().Be(System.Data.ConnectionState.Open);
        source.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
    }

    [Test]
    public void ManagedProviderRejectsNativeExtensionAndVfsApis()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");

        connection.EnableExtensions(false);
        Assert.Throws<NotSupportedException>(() => connection.EnableExtensions())!
            .Message.Should().Be("SQLite extension loading is not supported when Local Provider=Managed because extensions require the native SQLite loader.");
        Assert.Throws<NotSupportedException>(() => connection.LoadExtension("example"))!
            .Message.Should().Be("SQLite extension loading is not supported when Local Provider=Managed because extensions require the native SQLite loader.");

        using var vfsConnection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed;Vfs=win32-longpath");
        Assert.Throws<NotSupportedException>(() => vfsConnection.Open())!
            .Message.Should().Be("Vfs is not supported when Local Provider=Managed because the managed engine does not use native SQLite VFS implementations.");
    }

    private static string CreateManagedDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-routing-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"routing-{Guid.NewGuid():N}.db");
    }

    private static void DeleteManagedDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    [Test]
    public void ManagedLocalProviderCannotBeSelectedForRemoteConnection()
    {
        using var connection = new TursoConnection("Data Source=libsql://example.com;Local Provider=Managed");

        connection.Invoking(static value => value.Open())
            .Should().Throw<NotSupportedException>()
            .WithMessage("Local Provider=Managed is supported only for local database connections.");
    }
}
