using AwesomeAssertions;
using Turso.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;

namespace Turso.Tests;

/// <summary>
/// Two-connection coverage for <c>BEGIN DEFERRED</c>, <c>BEGIN IMMEDIATE</c> and
/// <c>BEGIN EXCLUSIVE</c>. The point of these tests is the *timing* of the busy
/// error: DEFERRED must stay lazy and fail at the first write, while IMMEDIATE
/// and EXCLUSIVE must take the write lock eagerly and fail at BEGIN itself.
/// </summary>
public class ManagedTransactionModeLockingTests
{
    private const int SqliteBusy = 5;

    [Test]
    public void DeferredTransactionReportsBusyAtFirstWriteNotAtBegin()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN DEFERRED;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        // DEFERRED takes no lock at BEGIN, so B gets in.
        var beginError = Capture(() => b.ExecuteNonQuery("BEGIN DEFERRED;"));
        beginError.Should().BeNull();

        // The conflict only shows up when B actually tries to write.
        var writeError = Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (2);"));
        writeError.Should().NotBeNull();
        writeError!.SqliteErrorCode.Should().Be(SqliteBusy);
        writeError.Message.Should().Contain("database is locked");
    }

    [Test]
    public void ImmediateTransactionReportsBusyAtBeginNotAtFirstWrite()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        // IMMEDIATE takes the write lock eagerly, so BEGIN itself is where the
        // caller learns it lost the race - before doing any work.
        var beginError = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        beginError.Should().NotBeNull();
        beginError!.SqliteErrorCode.Should().Be(SqliteBusy);
        beginError.Message.Should().Contain("database is locked");

        // The failed BEGIN left B in autocommit rather than in a half-open
        // transaction, and A is unaffected.
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        a.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void ImmediateTransactionIsBlockedByAnotherConnectionsDeferredWrite()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        // A's DEFERRED transaction escalated to a write lock at its first write,
        // so B's eager acquisition has to fail.
        var error = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void ExclusiveTransactionBlocksWritersButNotReadersUnderWal()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteScalar<string>("PRAGMA journal_mode;").Should().Be("wal");
        a.ExecuteNonQuery("BEGIN EXCLUSIVE;");

        // SQLite's EXCLUSIVE does not exclude readers in WAL mode; it behaves
        // like IMMEDIATE there. Verified against Microsoft.Data.Sqlite below.
        Capture(() => b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;")).Should().BeNull();

        var writerError = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        writerError.Should().NotBeNull();
        writerError!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void ExclusiveTransactionBlocksReadersUnderRollbackJournal()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("PRAGMA journal_mode=delete;");
        a.ExecuteScalar<string>("PRAGMA journal_mode;").Should().Be("delete");

        a.ExecuteNonQuery("BEGIN EXCLUSIVE;");

        // Under a rollback journal an EXCLUSIVE lock does exclude readers.
        var readError = Capture(() => b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;"));
        readError.Should().NotBeNull();
        readError!.SqliteErrorCode.Should().Be(SqliteBusy);

        a.ExecuteNonQuery("ROLLBACK;");
        b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(0);
    }

    [Test]
    public void ImmediateTransactionDoesNotBlockAnotherConnectionsRead()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        // B still sees the pre-transaction snapshot, and is not refused.
        b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(0);
    }

    [Test]
    public void AutocommitWriteIsBusyWhileAnotherConnectionHoldsWriteTransaction()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var writeError = Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (2);"));
        writeError.Should().NotBeNull();
        writeError!.SqliteErrorCode.Should().Be(SqliteBusy);

        // Autocommit reads stay allowed, as in SQLite's WAL mode.
        Capture(() => b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;")).Should().BeNull();
    }

    [Test]
    public void AutocommitWriteIsBusyWhileADeferredTransactionHasAlreadyWritten()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        // Before the first write a DEFERRED transaction holds nothing, so an
        // outside autocommit write still gets through.
        a.ExecuteNonQuery("BEGIN;");
        Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (1);")).Should().BeNull();

        // Once it escalates, it locks out other connections' autocommit writes.
        a.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        var blocked = Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (3);"));
        blocked.Should().NotBeNull();
        blocked!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void CreateTableAsSelectIsBusyWhileAnotherConnectionHoldsWriteTransaction()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var error = Capture(() => b.ExecuteNonQuery("CREATE TABLE copy AS SELECT * FROM t;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void VacuumIsBusyWhileAnotherConnectionHoldsWriteTransaction()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var error = Capture(() => b.ExecuteNonQuery("VACUUM;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void NativeSqliteAlsoRefusesAutocommitWriteAgainstAnOpenWriteTransaction()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN IMMEDIATE;");

        var writeError = NativeError(() => NativeExec(b, "INSERT INTO t VALUES (2);"));
        writeError.Should().NotBeNull();
        writeError!.SqliteErrorCode.Should().Be(SqliteBusy);

        NativeError(() => NativeExec(b, "SELECT COUNT(*) FROM t;")).Should().BeNull();
    }

    [Test]
    public void CommitReleasesTheEagerWriteLock()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");
        Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;")).Should().NotBeNull();
        a.ExecuteNonQuery("COMMIT;");

        // The reservation is gone once the transaction ends, so B may take it.
        Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;")).Should().BeNull();
        b.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void CommittingWriterReleasesTheLockForALaterConnection()
    {
        using var db = new ManagedFileDatabase();

        using (var a = db.Connect())
        {
            a.ExecuteNonQuery("BEGIN IMMEDIATE;");
            a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
            a.ExecuteNonQuery("COMMIT;");
        }

        using var c = db.Connect();
        c.ExecuteNonQuery("BEGIN IMMEDIATE;");
        c.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        c.ExecuteNonQuery("COMMIT;");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(2);
    }

    [Test]
    public void RollbackReleasesTheEagerWriteLock()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN EXCLUSIVE;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        a.ExecuteNonQuery("ROLLBACK;");

        b.ExecuteNonQuery("BEGIN IMMEDIATE;");
        b.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        b.ExecuteNonQuery("COMMIT;");

        b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(1);
    }

    [Test]
    public void ClosingAConnectionReleasesTheEagerWriteLock()
    {
        using var db = new ManagedFileDatabase();
        using var b = db.Connect();

        using (var a = db.Connect())
        {
            a.ExecuteNonQuery("BEGIN IMMEDIATE;");
            Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;")).Should().NotBeNull();
        }

        b.ExecuteNonQuery("BEGIN IMMEDIATE;");
        b.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void ReenteringImmediateOnTheSameConnectionIsNotSelfBlocking()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");
        a.ExecuteNonQuery("SAVEPOINT s1;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        a.ExecuteNonQuery("RELEASE s1;");
        a.ExecuteNonQuery("COMMIT;");

        a.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(1);
    }

    [Test]
    public void SerializableTransactionScopeTakesTheEagerWriteLock()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        // SqliteTransaction emits BEGIN IMMEDIATE for non-deferred Serializable,
        // which previously degraded to DEFERRED inside the engine.
        using var transaction = a.BeginTransaction();

        var error = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);

        transaction.Rollback();
        b.ExecuteNonQuery("BEGIN IMMEDIATE;");
        b.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void BeginAcceptsTransactionKeywordWithEveryMode()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();

        foreach (var sql in new[]
                 {
                     "BEGIN TRANSACTION;",
                     "BEGIN DEFERRED TRANSACTION;",
                     "BEGIN IMMEDIATE TRANSACTION;",
                     "BEGIN EXCLUSIVE TRANSACTION;",
                 })
        {
            a.ExecuteNonQuery(sql);
            a.ExecuteNonQuery("COMMIT;");
        }
    }

    [Test]
    public void RepeatedTransactionModeKeywordIsRejected()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var native = new NativeFileDatabase();

        // SQLite allows at most one mode keyword.
        NativeError(() => NativeExec(native.Connect(), "BEGIN DEFERRED IMMEDIATE;")).Should().NotBeNull();
        Capture(() => a.ExecuteNonQuery("BEGIN DEFERRED IMMEDIATE;")).Should().NotBeNull();
    }

    // The differential tests below pin the managed behavior to what native
    // SQLite actually does for the same statement sequence. They use their own
    // natively created file: a managed database file is owned exclusively by the
    // managed pager for its lifetime (the Stage 0 contract), so opening one with
    // Microsoft.Data.Sqlite at the same time is refused by design.

    [Test]
    public void NativeSqliteAlsoReportsDeferredBusyAtFirstWrite()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN DEFERRED;");
        NativeExec(a, "INSERT INTO t VALUES (1);");

        NativeError(() => NativeExec(b, "BEGIN DEFERRED;")).Should().BeNull();

        var writeError = NativeError(() => NativeExec(b, "INSERT INTO t VALUES (2);"));
        writeError.Should().NotBeNull();
        writeError!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void NativeSqliteAlsoReportsImmediateBusyAtBegin()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN IMMEDIATE;");

        var beginError = NativeError(() => NativeExec(b, "BEGIN IMMEDIATE;"));
        beginError.Should().NotBeNull();
        beginError!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void NativeSqliteExclusiveDoesNotBlockReadersUnderWal()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN EXCLUSIVE;");

        NativeError(() => NativeExec(b, "SELECT COUNT(*) FROM t;")).Should().BeNull();
        NativeError(() => NativeExec(b, "BEGIN IMMEDIATE;"))!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void NativeSqliteExclusiveBlocksReadersUnderRollbackJournal()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=delete;");
        NativeExec(a, "BEGIN EXCLUSIVE;");

        var readError = NativeError(() => NativeExec(b, "SELECT COUNT(*) FROM t;"));
        readError.Should().NotBeNull();
        readError!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    private static SqliteException? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (SqliteException exception)
        {
            return exception;
        }
    }

    private static MsData.SqliteException? NativeError(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (MsData.SqliteException exception)
        {
            return exception;
        }
    }

    private static void NativeExec(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A throwaway managed file database seeded with a single table, shared by
    /// the two connections each test opens against it.
    /// </summary>
    private sealed class ManagedFileDatabase : IDisposable
    {
        private readonly List<SqliteConnection> _connections = [];

        public ManagedFileDatabase()
        {
            Path = TempDatabasePath("managed");

            using var seed = new SqliteConnection($"Data Source={Path};Local Provider=Managed");
            seed.Open();
            seed.ExecuteNonQuery("CREATE TABLE t(v INTEGER);");
        }

        public string Path { get; }

        public SqliteConnection Connect()
        {
            var connection = new SqliteConnection($"Data Source={Path};Local Provider=Managed");
            connection.Open();
            _connections.Add(connection);
            return connection;
        }

        public void Dispose()
        {
            foreach (var connection in _connections)
                connection.Dispose();

            DeleteDatabaseFiles(Path);
        }
    }

    /// <summary>
    /// The same shape as <see cref="ManagedFileDatabase"/> but created and driven
    /// entirely by Microsoft.Data.Sqlite, for differential assertions.
    /// </summary>
    private sealed class NativeFileDatabase : IDisposable
    {
        private readonly List<MsData.SqliteConnection> _connections = [];

        public NativeFileDatabase()
        {
            Path = TempDatabasePath("native");
            NativeExec(Connect(), "CREATE TABLE t(v INTEGER);");
        }

        public string Path { get; }

        public MsData.SqliteConnection Connect()
        {
            var connection = new MsData.SqliteConnection($"Data Source={Path}");
            connection.Open();

            // Fail fast instead of spinning on the default busy handler.
            NativeExec(connection, "PRAGMA busy_timeout=0;");
            _connections.Add(connection);
            return connection;
        }

        public void Dispose()
        {
            foreach (var connection in _connections)
                connection.Dispose();

            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(Path);
        }
    }

    private static string TempDatabasePath(string kind) => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"turso-txn-mode-{kind}-{Guid.NewGuid():N}.db");

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (!File.Exists(candidate))
                continue;

            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp file must not fail the test.
            }
        }
    }
}
