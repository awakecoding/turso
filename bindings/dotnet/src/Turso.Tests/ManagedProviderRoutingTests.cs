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
    public void ManagedFileBackupReplacesNonemptyDestination()
    {
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using var source = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
            using var destination = new SqliteConnection($"Data Source={destinationPath};Local Provider=Managed");
            source.Open();
            destination.Open();
            source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
            destination.ExecuteNonQuery("CREATE TABLE destination_data(value TEXT); INSERT INTO destination_data VALUES ('destination');");

            source.BackupDatabase(destination);

            destination.State.Should().Be(System.Data.ConnectionState.Open);
            destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(1);
            destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
        }
        finally
        {
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
        nativeDestination.State.Should().Be(System.Data.ConnectionState.Closed);
        managedSource.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("managed");
    }

    [Test]
    public void NativeToManagedBackupRejectsMixedProvidersWithoutCopying()
    {
        NativeProviderTestFixture.EnsureRegistered();

        using var nativeSource = new PretendOpenSqliteConnection("Data Source=:memory:;Local Provider=Native");
        using var managedDestination = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        managedDestination.Open();
        managedDestination.ExecuteNonQuery("CREATE TABLE destination_data(value TEXT); INSERT INTO destination_data VALUES ('managed');");

        Assert.Throws<NotSupportedException>(() => nativeSource.BackupDatabase(managedDestination))!
            .Message.Should().Be("BackupDatabase does not support copying between managed and native providers.");

        nativeSource.State.Should().Be(System.Data.ConnectionState.Open);
        managedDestination.State.Should().Be(System.Data.ConnectionState.Open);
        managedDestination.ExecuteScalar<string>("SELECT value FROM destination_data;").Should().Be("managed");
    }

    [Test]
    public void ManagedBackupRejectsPhysicalAttachedPairsWithoutChangingThem()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        var sourceAttachmentPath = CreateManagedDatabasePath();
        var destinationAttachmentPath = CreateManagedDatabasePath();
        try
        {
            using var source = new SqliteConnection($"Data Source={sourcePath};Local Provider=Managed");
            using var destination = new SqliteConnection($"Data Source={destinationPath};Local Provider=Managed");
            source.Open();
            destination.Open();
            source.ExecuteNonQuery($"ATTACH DATABASE '{sourceAttachmentPath}' AS source_aux;");
            destination.ExecuteNonQuery($"ATTACH DATABASE '{destinationAttachmentPath}' AS destination_aux;");
            source.ExecuteNonQuery("CREATE TABLE source_aux.attached_data(value TEXT); INSERT INTO source_aux.attached_data VALUES ('attached');");
            destination.ExecuteNonQuery("CREATE TABLE old_data(value TEXT); INSERT INTO old_data VALUES ('old');");

            source.Invoking(connection => connection.BackupDatabase(destination, "main", "source_aux"))
                .Should().Throw<NotSupportedException>()
                .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupPhysicalFileIdentityNotSupported);
            destination.ExecuteScalar<string>("SELECT value FROM old_data;").Should().Be("old");
            source.ExecuteNonQuery("CREATE TABLE main_data(value TEXT); INSERT INTO main_data VALUES ('main');");
            using var reader = source.ExecuteReader("SELECT value FROM main_data;");
            reader.Read().Should().BeTrue();

            var activeReader = Assert.Throws<SqliteException>(
                () => source.BackupDatabase(source, "source_aux", "main"));

            activeReader!.SqliteErrorCode.Should().Be(5);
            source.ExecuteScalar<string>("SELECT value FROM source_aux.attached_data;").Should().Be("attached");
            reader.Dispose();
            source.Invoking(connection => connection.BackupDatabase(connection, "source_aux", "main"))
                .Should().Throw<NotSupportedException>()
                .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupPhysicalFileIdentityNotSupported);
            source.ExecuteScalar<string>("SELECT value FROM source_aux.attached_data;").Should().Be("attached");
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
            DeleteManagedDatabase(sourceAttachmentPath);
            DeleteManagedDatabase(destinationAttachmentPath);
        }
    }

    [Test]
    public void ManagedBackupRejectsTheSameDatabaseOnTheSameConnection()
    {
        using var source = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        source.Open();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");

        var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(source));

        exception!.SqliteErrorCode.Should().Be(1);
        exception.Message.Should().Contain("source and destination must be distinct");
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

    private sealed class PretendOpenSqliteConnection : SqliteConnection
    {
        private bool _pretendOpen;

        public PretendOpenSqliteConnection(string connectionString)
        {
            ConnectionString = connectionString;
            _pretendOpen = true;
        }

        public override System.Data.ConnectionState State
            => _pretendOpen ? System.Data.ConnectionState.Open : base.State;
    }
}
