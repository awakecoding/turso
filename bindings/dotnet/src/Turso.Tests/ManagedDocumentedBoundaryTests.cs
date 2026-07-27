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
        "CREATE TEMP VIEW v AS SELECT 1",
        "CREATE TEMP TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END",
        "BEGIN CONCURRENT",
        "ANALYZE",
        "SELECT * FROM pragma_table_info('t')",
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

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT)");
        Execute(connection, "CREATE TABLE s(a INTEGER, b TEXT)");
        return connection;
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
