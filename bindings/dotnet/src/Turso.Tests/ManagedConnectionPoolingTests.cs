using AwesomeAssertions;
using Turso.Data.Sqlite;
using Turso.Core;

namespace Turso.Tests;

[NonParallelizable]
public sealed class ManagedConnectionPoolingTests
{
    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    [Test]
    public void FileBackedConnectionReusesAResetPhysicalConnection()
    {
        var path = CreateDatabasePath();
        var attachedPath = CreateDatabasePath();
        try
        {
            using var connection = Open(path);
            connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
            connection.ExecuteNonQuery($"ATTACH DATABASE '{EscapeSqlLiteral(attachedPath)}' AS attached;");
            connection.ExecuteNonQuery("PRAGMA foreign_keys = ON; PRAGMA recursive_triggers = ON;");

            using var prepared = connection.CreateCommand();
            prepared.CommandText = "SELECT 1;";
            prepared.Prepare();

            using var readerCommand = connection.CreateCommand();
            readerCommand.CommandText = "SELECT 2;";
            using var reader = readerCommand.ExecuteReader();
            reader.Read().Should().BeTrue();

            var physicalConnection = connection.ManagedConnection;
            connection.Close();
            connection.Open();
            connection.ManagedConnection.Should().BeSameAs(physicalConnection);
            ReadDatabaseNames(connection).Should().Equal("main");

            var transaction = connection.BeginTransaction();
            connection.ExecuteNonQuery("INSERT INTO data VALUES (42);");
            connection.ExecuteNonQuery("PRAGMA query_only = ON;");

            connection.Close();

            reader.IsClosed.Should().BeTrue();
            transaction.Connection.Should().BeNull();
            connection.Open();

            connection.ManagedConnection.Should().BeSameAs(physicalConnection);
            prepared.ExecuteScalar().Should().Be(1L);
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);
            connection.ExecuteScalar<long>("PRAGMA foreign_keys;").Should().Be(0);
            connection.ExecuteScalar<long>("PRAGMA recursive_triggers;").Should().Be(0);
            connection.ExecuteScalar<long>("PRAGMA query_only;").Should().Be(0);
            connection.ExecuteScalar<long>("SELECT last_insert_rowid();").Should().Be(0);
        }
        finally
        {
            DeleteDatabase(path);
            DeleteDatabase(attachedPath);
        }
    }

    [Test]
    public void ClearPoolInvalidatesIdleAndRentedGenerations()
    {
        var path = CreateDatabasePath();
        try
        {
            using var idle = Open(path);
            var first = idle.ManagedConnection;
            idle.Close();
            idle.Open();
            idle.ManagedConnection.Should().BeSameAs(first);
            idle.Close();

            SqliteConnection.ClearPool(idle);
            first.Invoking(static connection => connection.Prepare("SELECT 1;"))
                .Should()
                .Throw<ObjectDisposedException>();
            idle.Open();
            var afterIdleClear = idle.ManagedConnection;
            afterIdleClear.Should().NotBeSameAs(first);

            SqliteConnection.ClearPool(idle);
            idle.Close();
            afterIdleClear.Invoking(static connection => connection.Prepare("SELECT 1;"))
                .Should()
                .Throw<ObjectDisposedException>();
            idle.Open();
            idle.ManagedConnection.Should().NotBeSameAs(afterIdleClear);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ClearAllPoolsInvalidatesEveryFilePool()
    {
        var firstPath = CreateDatabasePath();
        var secondPath = CreateDatabasePath();
        try
        {
            using var first = Open(firstPath);
            using var second = Open(secondPath);
            var firstPhysical = first.ManagedConnection;
            var secondPhysical = second.ManagedConnection;
            first.Close();
            second.Close();

            SqliteConnection.ClearAllPools();
            first.Open();
            second.Open();

            first.ManagedConnection.Should().NotBeSameAs(firstPhysical);
            second.ManagedConnection.Should().NotBeSameAs(secondPhysical);
        }
        finally
        {
            DeleteDatabase(firstPath);
            DeleteDatabase(secondPath);
        }
    }

    [Test]
    public void ClearPoolDoesNotRequireTheDatabaseFileToStillExist()
    {
        var sqlitePath = CreateDatabasePath();
        var tursoPath = CreateDatabasePath();
        try
        {
            using (var writer = new SqliteConnection(
                       $"Data Source={sqlitePath};Pooling=False;Local Provider=Managed"))
            {
                writer.Open();
                writer.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
            }

            using var readOnly = new SqliteConnection(
                $"Data Source={sqlitePath};Mode=ReadOnly;Pooling=True;Local Provider=Managed");
            readOnly.Open();
            readOnly.Close();
            DeleteDatabase(sqlitePath);

            Action clearSqlitePool = () => SqliteConnection.ClearPool(readOnly);
            clearSqlitePool.Should().NotThrow();

            using (var writer = new TursoConnection(
                       $"Data Source={tursoPath};Pooling=False;Local Provider=Managed"))
            {
                writer.Open();
                writer.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
            }

            using var tursoReadOnly = new TursoConnection(
                $"Data Source={tursoPath};Mode=ReadOnly;Pooling=True;Local Provider=Managed");
            tursoReadOnly.Open();
            tursoReadOnly.Close();
            DeleteDatabase(tursoPath);

            Action clearTursoPool = () => TursoConnection.ClearPool(tursoReadOnly);
            clearTursoPool.Should().NotThrow();
        }
        finally
        {
            DeleteDatabase(sqlitePath);
            DeleteDatabase(tursoPath);
        }
    }

    [Test]
    public void MemoryEncryptionAndCallbacksAreNotPooled()
    {
        using (var memory = new SqliteConnection("Data Source=:memory:;Pooling=True;Local Provider=Managed"))
        {
            memory.Open();
            var first = memory.ManagedConnection;
            memory.Close();
            memory.Open();
            memory.ManagedConnection.Should().NotBeSameAs(first);
        }

        var modeMemoryPath = CreateDatabasePath();
        using (var modeMemory = new SqliteConnection(
                   $"Data Source={modeMemoryPath};Mode=Memory;Pooling=True;Local Provider=Managed"))
        {
            modeMemory.Open();
            var first = modeMemory.ManagedConnection;
            modeMemory.Close();
            modeMemory.Open();
            modeMemory.ManagedConnection.Should().NotBeSameAs(first);
            File.Exists(modeMemoryPath).Should().BeFalse();
        }

        var callbackPath = CreateDatabasePath();
        var encryptedPath = CreateDatabasePath();
        try
        {
            using (var callback = new SqliteConnection(
                       $"Data Source={callbackPath};Pooling=True;Local Provider=Managed"))
            {
                callback.CreateFunction("pool_callback", static () => 1L);
                callback.Open();
                var first = callback.ManagedConnection;
                callback.Close();
                callback.Open();
                callback.ManagedConnection.Should().NotBeSameAs(first);
            }

            const string key = "000102030405060708090A0B0C0D0E0F"
                               + "101112131415161718191A1B1C1D1E1F";
            using var encrypted = new SqliteConnection(
                $"Data Source={encryptedPath};Pooling=True;Local Provider=Managed;"
                + $"Encryption Cipher=AES256GCM;Encryption Key={key}");
            encrypted.Open();
            var encryptedFirst = encrypted.ManagedConnection;
            encrypted.Close();
            encrypted.Open();
            encrypted.ManagedConnection.Should().NotBeSameAs(encryptedFirst);
        }
        finally
        {
            DeleteDatabase(callbackPath);
            DeleteDatabase(encryptedPath);
        }
    }

    [Test]
    public void FailedOpenDoesNotPoisonThePool()
    {
        var path = CreateDatabasePath();
        try
        {
            File.WriteAllText(path, "not a sqlite database");
            using var connection = new SqliteConnection(
                $"Data Source={path};Pooling=True;Local Provider=Managed");

            connection.Invoking(static value => value.Open()).Should().Throw<Exception>();

            File.Delete(path);
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE recovered(value INTEGER);");
            connection.Close();
            connection.Open();
            connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'recovered';").Should().Be(1);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ConcurrentRentReturnAndClearKeepsConnectionsUsable()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var setup = Open(path))
                setup.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (1);");

            using var poolIdentity = new SqliteConnection(
                $"Data Source={path};Pooling=True;Local Provider=Managed");
            var workers = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() =>
                {
                    for (var iteration = 0; iteration < 25; iteration++)
                    {
                        using var connection = Open(path);
                        connection.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(1);
                    }
                }))
                .ToArray();
            var clearer = Task.Run(() =>
            {
                for (var iteration = 0; iteration < 50; iteration++)
                {
                    SqliteConnection.ClearPool(poolIdentity);
                    if ((iteration & 7) == 0)
                        SqliteConnection.ClearAllPools();
                }
            });

            Task.WaitAll([.. workers, clearer]);

            using var final = Open(path);
            final.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(1);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void TursoConnectionOptInPoolingResetsRawTransactions()
    {
        var path = CreateDatabasePath();
        try
        {
            using var connection = new TursoConnection(
                $"Data Source={path};Pooling=True;Local Provider=Managed");
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
            connection.ExecuteNonQuery("BEGIN;");
            connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");
            connection.Close();

            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM data;";
            count.ExecuteScalar().Should().Be(0L);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void SqliteFacadePoolsEligibleFilesByDefault()
    {
        new SqliteConnectionStringBuilder().Pooling.Should().BeTrue();
        var path = CreateDatabasePath();
        try
        {
            using var connection = new SqliteConnection(
                $"Data Source={path};Local Provider=Managed");
            connection.Open();
            var physical = connection.ManagedConnection;
            connection.Close();
            connection.Open();

            connection.ManagedConnection.Should().BeSameAs(physical);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            $"Data Source={path};Pooling=True;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static string[] ReadDatabaseNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA database_list;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names.ToArray();
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-pooling-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"pool-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
