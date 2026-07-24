using AwesomeAssertions;
using ManagedSqlite = Turso.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;
using Turso.Core;

namespace Turso.Tests;

public sealed class ExplainQueryPlanTests
{
    [Test]
    public void ReportsStableCompiledAndFallbackRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var compiled = ReadPlan(connection, "EXPLAIN QUERY PLAN SELECT 1;");
        compiled.Columns.Should().Equal("id", "parent", "notused", "detail");
        compiled.Rows.Should().ContainSingle()
            .Which.Should().Equal(
                SqlValue.Integer(0),
                SqlValue.Integer(0),
                SqlValue.Integer(0),
                SqlValue.Text("MANAGED COMPILED VDBE"));

        var fallback = ReadPlan(connection, "EXPLAIN QUERY PLAN SELECT DISTINCT 1;");
        fallback.Rows.Should().ContainSingle()
            .Which[3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void RebindingParametersReportsTheRuntimeRouteWithoutRenderingValues()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("EXPLAIN QUERY PLAN SELECT ?1 + 1;");

        statement.ParameterCount.Should().Be(1);
        statement.GetParameterName(1).Should().Be("?1");
        statement.Bind(1, SqlValue.Integer(3));
        ReadDetail(statement).Should().Be("MANAGED COMPILED VDBE");

        statement.Reset();
        statement.Bind(1, SqlValue.Text("3"));
        ReadDetail(statement).Should().Be("MANAGED EVALUATOR FALLBACK");

        using var unbound = connection.Prepare("EXPLAIN QUERY PLAN SELECT ?1 + 1;");
        Assert.Throws<EmbeddedSqlException>(() => unbound.Step())!
            .Message.Should().Be("Missing value for parameter ?1.");
    }

    [Test]
    public void PlanningDmlDoesNotExecuteTheInnerStatement()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        ReadPlan(connection, "EXPLAIN QUERY PLAN INSERT INTO t VALUES (2);")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
        ReadScalar(connection, "SELECT count(*) FROM t;").Should().Be(SqlValue.Integer(1));

        ReadPlan(connection, "EXPLAIN QUERY PLAN INSERT OR IGNORE INTO t VALUES (3);")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        ReadScalar(connection, "SELECT count(*) FROM t;").Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void FallbackPlanningDoesNotInvokeUserFunctions()
    {
        using var connection = new EmbeddedDatabase().Connect();
        var calls = 0;
        connection.RegisterScalarFunction(
            "observe",
            1,
            values =>
            {
                calls++;
                return values[0];
            });

        using var statement = connection.Prepare("EXPLAIN QUERY PLAN SELECT observe(7);");
        ReadDetail(statement).Should().Be("MANAGED EVALUATOR FALLBACK");
        calls.Should().Be(0);

        using var explain = connection.Prepare("EXPLAIN SELECT observe(7);");
        Assert.Throws<EmbeddedSqlException>(() => explain.Step())!
            .Message.Should().Be(
                "EXPLAIN is only supported for statements lowered to the bytecode compiler.");
        calls.Should().Be(0);

        ReadScalar(connection, "SELECT observe(7);").Should().Be(SqlValue.Integer(7));
        calls.Should().Be(1);
    }

    [Test]
    public void RejectsStatementsWithoutAQueryPlanInsteadOfExecutingThem()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("EXPLAIN QUERY PLAN CREATE TABLE should_not_exist(value);");

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be(
                "EXPLAIN QUERY PLAN is only supported for queries and INSERT, UPDATE, or DELETE statements.");
        using var missingTable = connection.Prepare("SELECT * FROM should_not_exist;");
        Assert.Throws<EmbeddedSqlException>(() => missingTable.GetColumnCount())!
            .Message.Should().Be("no such table: should_not_exist");
    }

    private static string ReadDetail(EmbeddedStatement statement)
    {
        statement.Step().Should().Be(StatementStepResult.Row);
        var detail = statement.GetValue(3).AsText();
        statement.Step().Should().Be(StatementStepResult.Done);
        return detail;
    }

    private static (string[] Columns, List<SqlValue[]> Rows) ReadPlan(
        EmbeddedConnection connection,
        string sql)
    {
        using var statement = connection.Prepare(sql);
        var columns = Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(
                Enumerable.Range(0, statement.GetColumnCount())
                    .Select(statement.GetValue)
                    .ToArray());
        }

        return (columns, rows);
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}

public sealed class ExplainQueryPlanDifferentialTests
{
    [Test]
    public void ManagedFacadeMatchesSqlitePublicShapeAndParameterContract()
    {
        var managed = ReadManagedPlan();
        var sqlite = ReadSqlitePlan();

        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Columns.Should().Equal("id", "parent", "notused", "detail");
        managed.Rows.Should().ContainSingle();
        sqlite.Rows.Should().ContainSingle();
        AssertPublicRowShape(managed.Rows[0]);
        AssertPublicRowShape(sqlite.Rows[0]);
        managed.Rows[0][3].Should().Be("MANAGED COMPILED VDBE");
        ((string)sqlite.Rows[0][3]).Should().NotBeNullOrWhiteSpace();
    }

    private static (string[] Columns, List<object[]> Rows) ReadManagedPlan()
    {
        using var connection = new ManagedSqlite.SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT ?1 + 1;";
        command.Parameters.AddWithValue("?1", 4);
        using var reader = command.ExecuteReader();
        return ReadRows(reader);
    }

    private static (string[] Columns, List<object[]> Rows) ReadSqlitePlan()
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT ?1 + 1;";
        command.Parameters.AddWithValue("?1", 4);
        using var reader = command.ExecuteReader();
        return ReadRows(reader);
    }

    private static (string[] Columns, List<object[]> Rows) ReadRows(System.Data.Common.DbDataReader reader)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            rows.Add(
                Enumerable.Range(0, reader.FieldCount)
                    .Select(reader.GetValue)
                    .ToArray());
        }

        return (columns, rows);
    }

    private static void AssertPublicRowShape(object[] row)
    {
        row.Should().HaveCount(4);
        row[0].Should().BeOfType<long>();
        row[1].Should().BeOfType<long>();
        row[2].Should().BeOfType<long>();
        row[3].Should().BeOfType<string>();
    }
}
