using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Pins the interaction between managed connection pooling and the exclusive
/// main-file ownership lock described in the "Managed physical databases are not
/// concurrently interoperable with ordinary SQLite clients" section of
/// <c>README.md</c>.
///
/// Disposing the logical connections is not sufficient to hand a database back to
/// an ordinary SQLite client: the SQLite facade defaults to <c>Pooling=True</c>, so
/// a returned handle stays in the managed physical pool and keeps ownership. The
/// rest of the managed suite clears pools in <c>SetUp</c>/<c>TearDown</c> as
/// hygiene, which masks this everywhere else.
/// </summary>
[NonParallelizable]
public sealed class ManagedOwnershipHandoffPoolingTests
{
    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    [Test]
    public void DisposingAPooledManagedConnectionRetainsOwnershipUntilThePoolIsCleared()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path);

            using (var managed = OpenManaged(path, pooling: true))
            {
                managed.ExecuteScalarLong("SELECT COUNT(*) FROM t;").Should().Be(1);
            }

            // The logical connection is disposed, but the physical handle remains
            // pooled and still owns SQLite's main-file lock-byte range.
            TryOpenWithOrdinarySqlite(path).Should().NotBeNull(
                "a pooled managed handle retains exclusive ownership after disposal");

            SqliteConnection.ClearAllPools();

            TryOpenWithOrdinarySqlite(path).Should().BeNull(
                "clearing the pool disposes the physical handle and releases ownership");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PoolingFalseReleasesOwnershipOnDisposeSoSqliteCanReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path);

            using (var managed = OpenManaged(path, pooling: false))
            {
                managed.ExecuteNonQueryText("INSERT INTO t (v) VALUES ('managed');");
            }

            TryOpenWithOrdinarySqlite(path).Should().BeNull(
                "Pooling=False must release ownership when the connection is disposed");

            ReadCountWithOrdinarySqlite(path).Should().Be(2, "the managed write must be durable");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ReadOnlyManagedUsageAlsoTakesTheOwnershipLock()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path);

            using (var managed = OpenManaged(path, pooling: true))
            {
                managed.ExecuteScalarLong("SELECT COUNT(*) FROM t;").Should().Be(1);
            }

            // No write occurred, yet ownership is still held: every physical pager
            // takes the same lock regardless of whether it mutates anything.
            TryOpenWithOrdinarySqlite(path).Should().NotBeNull(
                "a read-only managed session takes the same ownership lock as a writer");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void OwnershipRoundTripsBetweenManagedAndOrdinarySqliteWhenPoolingIsDisabled()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path);

            using (var managed = OpenManaged(path, pooling: false))
                managed.ExecuteNonQueryText("INSERT INTO t (v) VALUES ('first');");

            ReadCountWithOrdinarySqlite(path).Should().Be(2);

            using (var managed = OpenManaged(path, pooling: false))
            {
                managed.ExecuteScalarLong("SELECT COUNT(*) FROM t;").Should().Be(2);
                managed.ExecuteNonQueryText("INSERT INTO t (v) VALUES ('second');");
            }

            ReadCountWithOrdinarySqlite(path).Should().Be(3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static SqliteConnection OpenManaged(string path, bool pooling)
    {
        var connection = new SqliteConnection(
            $"Data Source={path};Pooling={(pooling ? "True" : "False")};Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void SeedWithOrdinarySqlite(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t (v) VALUES ('seed');";
        command.ExecuteNonQuery();
        connection.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Returns the failure message when ordinary SQLite cannot open the database,
    /// or <see langword="null"/> when the open succeeds.
    /// </summary>
    private static string? TryOpenWithOrdinarySqlite(string path)
    {
        try
        {
            ReadCountWithOrdinarySqlite(path);
            return null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            return exception.Message;
        }
    }

    private static long ReadCountWithOrdinarySqlite(string path)
    {
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM t;";
            return (long)command.ExecuteScalar()!;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-ownership-handoff-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"handoff-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // A retained handle is not a test failure during cleanup.
            }
        }
    }
}

internal static class ManagedOwnershipHandoffCommandExtensions
{
    internal static long ExecuteScalarLong(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    internal static void ExecuteNonQueryText(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
