using AwesomeAssertions;
using Turso.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Turso.Tests;

public class CompiledCompoundRecursiveDifferentialTests
{
    [TestCase("SELECT 1 + 1 AS x UNION ALL VALUES (3), (4)", "Arithmetic")]
    [TestCase("SELECT 2 AS x INTERSECT VALUES (1 + 1)", "Arithmetic")]
    [TestCase("SELECT * FROM (SELECT 1 AS x UNION SELECT 2) INTERSECT VALUES (2)", "GuardedRow")]
    [TestCase("SELECT * FROM (SELECT 1 AS x INTERSECT SELECT 1) EXCEPT VALUES (2)", "GuardedRow")]
    [TestCase("SELECT * FROM (SELECT 1 AS x EXCEPT SELECT 2) UNION ALL VALUES (3)", "CompoundResultRow")]
    [TestCase("VALUES (1), (2), (2) EXCEPT SELECT 2", "RowSetRewind")]
    public void SafeCompoundShapesMatchSqliteAndRoute(string query, string routedOpcode)
    {
        using var connection = new EmbeddedDatabase().Connect();

        AssertMatchesSqlite([], query);
        ExplainOpcodes(connection, query).Should().Contain(routedOpcode);
        QueryPlanDetail(connection, query).Should().Be("MANAGED COMPILED VDBE");
    }

    [Test]
    public void CompoundCollationMetadataAndDeduplicationMatchSqlite()
    {
        const string query = "SELECT 'X' COLLATE NOCASE AS value INTERSECT VALUES ('x')";
        using var connection = new EmbeddedDatabase().Connect();

        var output = AssertMatchesSqlite([], query);

        output.Columns.Should().Equal("value");
        output.Rows.Should().ContainSingle();
        output.Rows[0][0].Should().Be(SqlValue.Text("X"));
        ExplainOpcodes(connection, query).Should().Contain("CompoundResultRow");
    }

    [Test]
    public void CompoundParametersResetAndRebindWithoutChangingRouting()
    {
        const string query =
            "SELECT * FROM (VALUES (?1) EXCEPT VALUES (?2)) UNION ALL VALUES (?3)";
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(query);

        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(2));
        statement.Bind(3, SqlValue.Integer(2));
        Drain(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        AssertMatchesSqlite([], query, SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(2));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(4));
        statement.Bind(2, SqlValue.Integer(4));
        statement.Bind(3, SqlValue.Integer(5));
        Drain(statement).Should().Equal(SqlValue.Integer(5));
        AssertMatchesSqlite([], query, SqlValue.Integer(4), SqlValue.Integer(4), SqlValue.Integer(5));

        using var explain = connection.Prepare("EXPLAIN " + query);
        explain.Bind(1, SqlValue.Null);
        explain.Bind(2, SqlValue.Null);
        explain.Bind(3, SqlValue.Null);
        ReadRows(explain).Select(row => row[1].AsText())
            .Should().Contain("LoadParameter").And.Contain("RowSetRewind");
    }

    [Test]
    public void ErrorCapableSetTermsKeepEvaluatorSourceOrder()
    {
        string[] setup =
        [
            "CREATE TABLE first_error(value)",
            "INSERT INTO first_error VALUES (-9223372036854775808)",
            "CREATE TABLE second_error(value)",
            "INSERT INTO second_error VALUES ('not-json')",
        ];
        const string query =
            "SELECT abs(value) FROM first_error INTERSECT "
            + "SELECT json_extract(value, '$') FROM second_error";

        var managed = CaptureManagedError(setup, query);
        var sqlite = CaptureSqliteError(setup, query);

        managed.RowsBeforeError.Should().Be(sqlite.RowsBeforeError).And.Be(0);
        managed.Message.Should().Contain("integer overflow");
        sqlite.Message.Should().Contain("integer overflow");

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        QueryPlanDetail(connection, query).Should().Be("MANAGED EVALUATOR FALLBACK");
        Assert.Throws<EmbeddedSqlException>(() => ExplainOpcodes(connection, query));
    }

    [Test]
    public void ExplicitNullOrderingMatchesSqliteAndReportsFallback()
    {
        const string query =
            "VALUES (NULL), (2), (1) UNION ALL SELECT NULL ORDER BY 1 NULLS LAST";
        using var connection = new EmbeddedDatabase().Connect();

        var output = AssertMatchesSqlite([], query);

        output.Rows.Select(row => row[0]).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Null,
            SqlValue.Null);
        QueryPlanDetail(connection, query).Should().Be("MANAGED EVALUATOR FALLBACK");
    }

    [Test]
    public void CancellableCompoundExecutionReportsEvaluatorFallback()
    {
        const string query = "SELECT 1 + 1 UNION ALL VALUES (3)";
        using var connection = new EmbeddedDatabase().Connect();
        using var cancellation = new CancellationTokenSource();

        QueryPlanDetail(connection, query).Should().Be("MANAGED COMPILED VDBE");
        QueryPlanDetail(connection, query, cancellation.Token)
            .Should().Be("MANAGED EVALUATOR FALLBACK");

        using var statement = connection.Prepare(query);
        ReadRows(statement, cancellation.Token).Should().HaveCount(2);
    }

    [Test]
    public void JoinedAndDistinctRecursiveTermsMatchSqliteAndRoute()
    {
        string[] setup =
        [
            "CREATE TABLE edges(src INTEGER, dst INTEGER)",
            "INSERT INTO edges VALUES (1, 2), (1, 3), (2, 4), (3, 4), (4, 1)",
        ];
        const string joined =
            "WITH RECURSIVE reach(n) AS ("
            + "VALUES (1) UNION SELECT DISTINCT dst FROM edges JOIN reach ON src = n"
            + ") SELECT * FROM reach";

        var output = AssertMatchesSqlite(setup, joined);
        output.Rows.Select(row => row[0]).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(3),
            SqlValue.Integer(4));

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        ExplainOpcodes(connection, joined).Should().Contain("WorkTableExpandGeneration");
        QueryPlanDetail(connection, joined).Should().Be("MANAGED COMPILED VDBE");
    }

    [Test]
    public void RecursiveParametersResetRebindAndTerminateLikeSqlite()
    {
        const string query =
            "WITH RECURSIVE c(x) AS ("
            + "VALUES (?1) UNION SELECT x + 1 FROM c WHERE x < ?2"
            + ") SELECT * FROM c";
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(query);

        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(3));
        Drain(statement).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(3));
        AssertMatchesSqlite([], query, SqlValue.Integer(1), SqlValue.Integer(3));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Integer(9));
        Drain(statement).Should().Equal(
            SqlValue.Integer(7),
            SqlValue.Integer(8),
            SqlValue.Integer(9));
        AssertMatchesSqlite([], query, SqlValue.Integer(7), SqlValue.Integer(9));
    }

    [Test]
    public void RecursiveCallbackAndCancellationShapesStayOnEvaluator()
    {
        const string query =
            "WITH RECURSIVE c(x) AS ("
            + "VALUES (1) UNION ALL SELECT cancel_next(x + 1) FROM c WHERE x < 4"
            + ") SELECT * FROM c";
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "cancel_next",
            1,
            values =>
            {
                calls++;
                cancellation.Cancel();
                return values[0];
            });
        using var connection = database.Connect();

        QueryPlanDetail(connection, query).Should().Be("MANAGED EVALUATOR FALLBACK");
        Assert.Throws<OperationCanceledException>(
            () => ReadRows(connection.Prepare(query), cancellation.Token));
        calls.Should().Be(1);
    }

    [Test]
    public void CancellableRecursivePlanTruthfullyReportsFallback()
    {
        const string query =
            "WITH RECURSIVE c(x) AS (VALUES (1) UNION SELECT x + 1 FROM c WHERE x < 3) "
            + "SELECT * FROM c";
        using var connection = new EmbeddedDatabase().Connect();
        using var cancellation = new CancellationTokenSource();

        QueryPlanDetail(connection, query).Should().Be("MANAGED COMPILED VDBE");
        QueryPlanDetail(connection, query, cancellation.Token)
            .Should().Be("MANAGED EVALUATOR FALLBACK");
    }

    private static QueryOutput AssertMatchesSqlite(
        IReadOnlyList<string> setup,
        string query,
        params SqlValue[] parameters)
    {
        var managed = RunManaged(setup, query, parameters);
        var sqlite = RunSqlite(setup, query, parameters);

        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().HaveCount(sqlite.Rows.Count);
        for (var index = 0; index < managed.Rows.Count; index++)
            managed.Rows[index].Should().Equal(sqlite.Rows[index]);
        return managed;
    }

    private static QueryOutput RunManaged(
        IReadOnlyList<string> setup,
        string query,
        IReadOnlyList<SqlValue> parameters)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        using var statement = connection.Prepare(query);
        for (var index = 0; index < parameters.Count; index++)
            statement.Bind(index + 1, parameters[index]);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        return new QueryOutput(columns, ReadRows(statement));
    }

    private static QueryOutput RunSqlite(
        IReadOnlyList<string> setup,
        string query,
        IReadOnlyList<SqlValue> parameters)
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
        for (var index = 0; index < parameters.Count; index++)
            command.Parameters.AddWithValue($"?{index + 1}", ToClrValue(parameters[index]));
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<SqlValue[]>();
        while (reader.Read())
        {
            var row = new SqlValue[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? SqlValue.Null : FromClrValue(reader.GetValue(index));
            rows.Add(row);
        }

        return new QueryOutput(columns, rows);
    }

    private static ErrorOutput CaptureManagedError(IReadOnlyList<string> setup, string query)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        using var statement = connection.Prepare(query);
        var rows = 0;
        var error = Assert.Throws<EmbeddedSqlException>(() =>
        {
            while (statement.Step() == StatementStepResult.Row)
                rows++;
        });
        return new ErrorOutput(rows, error!.Message);
    }

    private static ErrorOutput CaptureSqliteError(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = sql;
            setupCommand.ExecuteNonQuery();
        }

        var rows = 0;
        var error = Assert.Throws<MsData.SqliteException>(() =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows++;
        });
        return new ErrorOutput(rows, error!.Message);
    }

    private static IReadOnlyList<string> ExplainOpcodes(EmbeddedConnection connection, string query)
    {
        using var statement = connection.Prepare("EXPLAIN " + query);
        return ReadRows(statement).Select(row => row[1].AsText()).ToArray();
    }

    private static string QueryPlanDetail(
        EmbeddedConnection connection,
        string query,
        CancellationToken cancellationToken = default)
    {
        using var statement = connection.Prepare("EXPLAIN QUERY PLAN " + query);
        statement.Step(cancellationToken).Should().Be(StatementStepResult.Row);
        return statement.GetValue(3).AsText();
    }

    private static List<SqlValue> Drain(EmbeddedStatement statement)
    {
        var values = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0));
        return values;
    }

    private static List<SqlValue[]> ReadRows(
        EmbeddedStatement statement,
        CancellationToken cancellationToken = default)
    {
        using (statement)
        {
            var rows = new List<SqlValue[]>();
            while (statement.Step(cancellationToken) == StatementStepResult.Row)
            {
                var row = new SqlValue[statement.GetColumnCount()];
                for (var index = 0; index < row.Length; index++)
                    row[index] = statement.GetValue(index);
                rows.Add(row);
            }

            return rows;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static object ToClrValue(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => DBNull.Value,
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => value.AsBlob().ToArray(),
            _ => throw new InvalidOperationException($"Unsupported SQL value kind {value.Kind}."),
        };

    private static SqlValue FromClrValue(object value)
        => value switch
        {
            long integer => SqlValue.Integer(integer),
            double real => SqlValue.Real(real),
            string text => SqlValue.Text(text),
            byte[] blob => SqlValue.Blob(blob),
            _ => throw new InvalidOperationException(
                $"Unsupported Microsoft.Data.Sqlite value type {value.GetType().Name}."),
        };

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<SqlValue[]> Rows);

    private sealed record ErrorOutput(int RowsBeforeError, string Message);
}
