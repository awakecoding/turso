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
        "UPDATE t SET b = s.b FROM s WHERE t.a = s.a",
        "UPDATE OR REPLACE t SET a = 1",
        "UPDATE t AS x SET b = 'q'",
        "DELETE FROM t AS x WHERE x.a = 99",
        "BEGIN CONCURRENT",
        "ANALYZE",
        "SELECT * FROM pragma_table_info('t')",
        "CREATE VIRTUAL TABLE vt USING fts5(x)",
        "SELECT a, count(*), row_number() OVER () FROM t GROUP BY a",
        "SELECT count(*) OVER () FROM t GROUP BY a",
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
    [TestCase("Authorizer")]
    [TestCase("UpdateHook")]
    [TestCase("CommitHook")]
    [TestCase("RollbackHook")]
    [TestCase("Trace")]
    [TestCase("ProgressHandler")]
    [TestCase("CreateModule")]
    public void NoHookOrModuleSurfaceIsPublished(string fragment)
    {
        typeof(SqliteConnection)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name)
            .Should()
            .NotContain(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
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
