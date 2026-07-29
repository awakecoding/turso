using System.Data;
using System.Reflection;
using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

/// <summary>
/// Guards the "Managed engine scope" boundaries published in
/// <c>bindings/dotnet/Readme.md</c>. If one of these surfaces becomes supported,
/// this suite fails so the documentation is corrected in the same change rather
/// than drifting into an overstated compatibility claim.
/// </summary>
public sealed class ManagedDocumentedBoundaryTests
{
    private static readonly string[] UnsupportedPragmas =
    [
        "PRAGMA cache_size = 100",
        "PRAGMA synchronous = FULL",
        "PRAGMA locking_mode = EXCLUSIVE",
        "PRAGMA busy_timeout = 100",
        "PRAGMA wal_checkpoint",
        "PRAGMA wal_autocheckpoint = 100",
        "PRAGMA auto_vacuum = 1",
        "PRAGMA max_page_count = 100",
        "PRAGMA temp_store = 2",
        "PRAGMA mmap_size = 0",
        "PRAGMA function_list",
        "PRAGMA module_list",
    ];

    private static readonly string[] UnsupportedStatements =
    [
        "BEGIN CONCURRENT",
        "ANALYZE",
        "SELECT * FROM fts5vocab('t', 'row')",
        "CREATE VIRTUAL TABLE vt USING fts5(x)",
    ];

    [Test]
    [TestCaseSource(nameof(UnsupportedPragmas))]
    public void ADocumentedUnsupportedPragmaIsRejected(string sql)
    {
        using var connection = Open();
        var error = Assert.Throws<SqliteException>(() => Execute(connection, sql));
        error!.Message.Should().Contain("Unsupported PRAGMA");
    }

    [Test]
    [TestCaseSource(nameof(UnsupportedStatements))]
    public void ADocumentedUnsupportedStatementIsRejected(string sql)
    {
        using var connection = Open();
        Assert.Throws<SqliteException>(() => Execute(connection, sql));
    }

    [Test]
    public void RawHandleInteropRemainsUnavailable()
    {
        using var connection = Open();
        object? handle = connection.Handle;
        handle.Should().BeNull();
        connection.ServerVersion.Should().Be("3.0.0");
    }

    [Test]
    [TestCase("CreateModule")]
    public void NoModuleSurfaceIsPublished(string fragment)
    {
        typeof(SqliteConnection)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name)
            .Should()
            .NotContain(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Update, commit and rollback hooks, the authorizer, tracing and the progress handler moved
    /// out of the "Not implemented" list in <c>bindings/dotnet/Readme.md</c>, so this asserts the
    /// documented direction of that scope change: the surface must stay published. Behavior lives
    /// in <c>ManagedHookAndAuthorizerTests</c> and <c>ManagedHookSqliteDifferentialTests</c>.
    /// </summary>
    [Test]
    [TestCase("SetUpdateHook")]
    [TestCase("SetCommitHook")]
    [TestCase("SetRollbackHook")]
    [TestCase("SetAuthorizer")]
    [TestCase("SetTraceHandler")]
    [TestCase("SetProgressHandler")]
    public void TheDocumentedHookSurfaceIsPublished(string member)
    {
        typeof(SqliteConnection)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(candidate => candidate.Name)
            .Should()
            .Contain(member);
    }

    [Test]
    public void OnlyTheDocumentedSchemaCollectionsAreDefined()
    {
        using var connection = Open();

        connection.GetSchema("MetaDataCollections").Rows.Cast<DataRow>()
            .Select(row => (string)row["CollectionName"])
            .Should().Equal(
                "MetaDataCollections",
                "ReservedWords",
                "Tables",
                "Columns",
                "Indexes",
                "IndexColumns");

        foreach (var undefined in new[] { "DataSourceInformation", "DataTypes", "Restrictions", "ForeignKeys", "Views" })
        {
            Assert.Throws<ArgumentException>(() => connection.GetSchema(undefined))!
                .Message.Should().Be($"Unknown collection: {undefined}.");
        }
    }

    [Test]
    public void TheCommandBuilderRefusesSelectsItCannotRoundTrip()
    {
        using var connection = Open();

        // Documented limit: single-table selects exposing a key column only. A join and a keyless
        // table are the two shapes callers hit first, so both have to fail loudly at command
        // generation rather than silently producing a statement that updates nothing.
        using var join = new TursoDataAdapter("SELECT t.a, s.b FROM t JOIN s ON t.a = s.a", connection);
        using var joinBuilder = new TursoCommandBuilder(join);
        Assert.Throws<InvalidOperationException>(() => joinBuilder.GetUpdateCommand());

        using var keyless = new TursoDataAdapter("SELECT a, b FROM t", connection);
        using var keylessBuilder = new TursoCommandBuilder(keyless);
        Assert.Throws<InvalidOperationException>(() => keylessBuilder.GetUpdateCommand());
    }

    [Test]
    public void TheAdapterDoesNotBatchRowUpdates()
    {
        using var adapter = new TursoDataAdapter();

        adapter.UpdateBatchSize.Should().Be(1);
        Assert.Throws<NotSupportedException>(() => adapter.UpdateBatchSize = 10);
    }

    // Readme.md:695 documents that file-backed trigger definitions containing function
    // calls are rejected because connection-local implementations cannot be reconstructed
    // on reopen. A builtin scalar function (UPPER) is the first shape a caller reaches for,
    // so it must fail loudly at CREATE TRIGGER time rather than silently persisting a body
    // that would misbehave (or throw) after reopen.
    [Test]
    public void FileBackedTriggerWithBuiltinFunctionIsRejected()
    {
        var path = CreateDatabasePath();
        try
        {
            using var connection = OpenFile(path);
            ExecuteNonQuery(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)");
            ExecuteNonQuery(connection, "CREATE TABLE audit (id INTEGER PRIMARY KEY, upper_name TEXT)");

            var error = Assert.Throws<SqliteException>(() =>
                ExecuteNonQuery(connection, "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW BEGIN INSERT INTO audit (upper_name) VALUES (UPPER(NEW.name)); END"));

            error!.Message.Should().Contain("cannot persist trigger");
            error.Message.Should().Contain("File-backed schema definitions cannot retain");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    // Readme.md "Known divergences from SQLite" documents that a double-quoted token in a
    // value context is resolved strictly as a column identifier: an unresolved name throws
    // `no such column` rather than falling back to a string literal the way stock SQLite
    // (SQLITE_DQS, the default, including e_sqlite3) does. Single-quoted literals are the
    // portable form. This pins the strict behavior so a future DQS-style fallback cannot
    // silently slip in without updating the documentation.
    [Test]
    public void DoubleQuotedTokenInValueContextIsResolvedAsColumnNotStringLiteral()
    {
        using var connection = Open();
        Execute(connection, "INSERT INTO t VALUES (1, 'characters')");

        // (1) A double-quoted real column name resolves to the column value (strict
        //     identifier), not to the literal token. A DQS string-literal fallback would
        //     return the text "a" here instead of the stored integer 1.
        ReadOne(connection, "SELECT \"a\" FROM t").Should().Be("1");

        // (2) A double-quoted token that is NOT a column throws `no such column`, matching
        //     strict identifier resolution. Stock SQLite with SQLITE_DQS (the default,
        //     including e_sqlite3) would fall back to the string literal 'characters' and
        //     return a row. This pins the documented divergence.
        var error = Assert.Throws<SqliteException>(() =>
            ReadOne(connection, "SELECT \"characters\" FROM t"));

        error!.Message.Should().Contain("no such column: characters");
    }

    private static string? ReadOne(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return "<NO ROWS>";
        return reader.IsDBNull(0) ? "<NULL>" : reader.GetValue(0)?.ToString();
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT)");
        Execute(connection, "CREATE TABLE s(a INTEGER, b TEXT)");
        return connection;
    }

    private static SqliteConnection OpenFile(string path)
    {
        // Pooling=False hands the file lock back on dispose so the test can delete it; the
        // explicit Local Provider=Managed pins the managed file store whose reopen
        // constraint is the boundary under test.
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "managed-boundary");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"file-trigger-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
        }
    }
}
