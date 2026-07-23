using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Turso.Core;

namespace Turso.Tests;

public class DistinctScanSqlRoutingTests
{
    [Test]
    public void DirectColumnDistinctMatchesSqliteValuesAndTypes()
    {
        string[] setup =
        [
            "CREATE TABLE t(i, r, text_value, blob_value)",
            "INSERT INTO t VALUES (1, 1.5, 'one', x'01'), (1, 1.5, 'one', x'01'), (NULL, NULL, NULL, NULL), (2, 2.5, 'two', x'02')",
        ];

        AssertMatchesSqlite(setup, "SELECT DISTINCT i, r, text_value, blob_value FROM t");
    }

    [Test]
    public void DirectColumnDistinctRoutesThroughDistinctResultRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a, b)");
        Execute(connection, "INSERT INTO t VALUES (1, 'x'), (1, 'x'), (NULL, 'x'), (NULL, 'x'), (2, 'y')");

        var rows = ReadRows(connection, "SELECT DISTINCT a, b FROM t");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("x"));
        rows[1].Should().Equal(SqlValue.Null, SqlValue.Text("x"));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Text("y"));

        ReadRows(connection, "EXPLAIN SELECT DISTINCT a, b FROM t")
            .Select(row => row[1].AsText())
            .Should().Equal(
                "OpenReadCursor", "Rewind", "Column", "Column", "DistinctResultRow", "Next", "CloseCursor", "Halt");
    }

    [Test]
    public void ResetClearsDistinctSetAndReadsAppendedRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a)");
        Execute(connection, "INSERT INTO t VALUES (1), (1), (2)");

        using var statement = connection.Prepare("SELECT DISTINCT a FROM t");
        Drain(statement).Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        Execute(connection, "INSERT INTO t VALUES (2), (3)");
        statement.Reset();

        Drain(statement).Select(row => row[0]).Should().Equal(
            SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void DistinctUsesDeclaredCollationsForDirectAndStarProjections()
    {
        string[] setup =
        [
            "CREATE TABLE t(a TEXT COLLATE NOCASE, b)",
            "INSERT INTO t VALUES ('x', 1), ('X', 1), ('x', 2)",
        ];

        AssertMatchesSqlite(setup, "SELECT DISTINCT a FROM t");
        AssertMatchesSqlite(setup, "SELECT DISTINCT * FROM t");
        AssertMatchesSqlite(setup, "SELECT DISTINCT t.* FROM t");

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

        ReadRows(connection, "EXPLAIN SELECT DISTINCT a FROM t")
            .Select(row => row[1].AsText())
            .Should().Contain("DistinctResultRow");
    }

    [Test]
    public void ComputedCollatedStarRowidAndFilteredDistinctFallBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a TEXT, b)");
        Execute(connection, "INSERT INTO t VALUES ('x', 1), ('X', 1), ('x', 2)");

        ReadRows(connection, "SELECT DISTINCT a COLLATE NOCASE FROM t")[0]
            .Should().Equal(SqlValue.Text("x"));
        ReadRows(connection, "SELECT DISTINCT a + 1 FROM t")[0][0]
            .AsInteger().Should().Be(1);
        ReadRows(connection, "SELECT DISTINCT * FROM t").Should().HaveCount(3);
        ReadRows(connection, "SELECT DISTINCT rowid FROM t").Should().HaveCount(3);
        ReadRows(connection, "SELECT DISTINCT a FROM t WHERE b = 1")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Text("x"), SqlValue.Text("X"));

        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT a COLLATE NOCASE FROM t");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT a + 1 FROM t");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT * FROM t");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT rowid FROM t");
        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT a FROM t WHERE b = 1");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);

        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().HaveCount(sqlite.Rows.Count);
        for (var row = 0; row < sqlite.Rows.Count; row++)
        {
            managed.Rows[row].Should().HaveCount(sqlite.Rows[row].Length);
            for (var column = 0; column < sqlite.Rows[row].Length; column++)
                CellShouldMatch(managed.Rows[row][column], sqlite.Rows[row][column]);
        }
    }

    private static (string[] Columns, List<SqlValue[]> Rows) RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

        using var statement = connection.Prepare(query);
        var columns = Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
        return (columns, Drain(statement));
    }

    private static (string[] Columns, List<object?[]> Rows) RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = sql;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var column = 0; column < row.Length; column++)
                row[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(row);
        }

        return (columns, rows);
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite)
    {
        switch (sqlite)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer));
                break;
            case double real:
                managed.Should().Be(SqlValue.Real(real));
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text));
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob);
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                Assert.Fail($"Unexpected SQLite value type {sqlite.GetType().Name}.");
                break;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return Drain(statement);
    }

    private static void ExplainRefused(EmbeddedConnection connection, string sql)
    {
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, sql))!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    private static List<SqlValue[]> Drain(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var column = 0; column < row.Length; column++)
                row[column] = statement.GetValue(column);

            rows.Add(row);
        }

        return rows;
    }
}
