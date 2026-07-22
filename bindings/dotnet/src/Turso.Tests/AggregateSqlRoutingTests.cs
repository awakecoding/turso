using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

// Proves that EmbeddedDatabase routes the supported aggregate SQL subset through the real
// AggReset/AggStep/AggFinalize opcode family (plus Goto/SameGroup for GROUP BY) and that the
// routed results stay byte-identical to the tree-walking evaluator. EXPLAIN is used as the
// ground truth for "was this lowered to bytecode?": a routed statement dumps the accumulator
// opcodes, while every deliberate fallback shape throws because EXPLAIN only describes lowered
// programs. Fallback tests also assert the evaluator still produces the correct value or error.
public class AggregateSqlRoutingTests
{
    [Test]
    public void ScalarNumericAggregatesMatchEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (NULL), (30);");

        var rows = ReadRows(
            connection,
            "SELECT count(*), count(value), sum(value), avg(value), min(value), max(value), total(value) FROM t;");

        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(4));
        rows[0][1].Should().Be(SqlValue.Integer(3));
        rows[0][2].Should().Be(SqlValue.Integer(60));
        rows[0][3].Should().Be(SqlValue.Real(20));
        rows[0][4].Should().Be(SqlValue.Integer(10));
        rows[0][5].Should().Be(SqlValue.Integer(30));
        rows[0][6].Should().Be(SqlValue.Real(60));
    }

    [Test]
    public void ScalarAggregatesOverEmptyTableYieldEvaluatorIdentities()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(
            connection,
            "SELECT count(*), count(value), sum(value), avg(value), min(value), max(value), total(value), group_concat(value) FROM t;");

        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(0));
        rows[0][1].Should().Be(SqlValue.Integer(0));
        rows[0][2].Should().Be(SqlValue.Null);
        rows[0][3].Should().Be(SqlValue.Null);
        rows[0][4].Should().Be(SqlValue.Null);
        rows[0][5].Should().Be(SqlValue.Null);
        rows[0][6].Should().Be(SqlValue.Real(0));
        rows[0][7].Should().Be(SqlValue.Null);
    }

    [Test]
    public void GroupConcatConcatenatesInScanOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(name TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a'), ('b'), (NULL), ('c');");

        ReadRows(connection, "SELECT group_concat(name) FROM t;")[0][0]
            .Should().Be(SqlValue.Text("a,b,c"));
        ReadRows(connection, "SELECT group_concat(name, '-') FROM t;")[0][0]
            .Should().Be(SqlValue.Text("a-b-c"));
    }

    [Test]
    public void ScalarAggregateColumnLabelsUseAliasOrFunctionName()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        ColumnNames(connection, "SELECT count(*) AS n, sum(value) FROM t;")
            .Should().Equal("n", "SUM");
    }

    [Test]
    public void GroupByProducesFirstSeenGroupsWithMultipleAggregates()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2, 20), (1, 10), (2, 5), (1, 7);");

        var rows = ReadRows(connection, "SELECT k, count(*), sum(v) FROM t GROUP BY k;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(25));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(17));
    }

    [Test]
    public void GroupByMultipleKeysGroupsOnTheKeyTuple()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x', 10), (1, 'y', 20), (1, 'x', 5);");

        var rows = ReadRows(connection, "SELECT a, b, sum(v) FROM t GROUP BY a, b;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("x"), SqlValue.Integer(15));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Text("y"), SqlValue.Integer(20));
    }

    [Test]
    public void GroupByNullKeysGroupTogether()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (NULL), (1), (NULL), (2);");

        var rows = ReadRows(connection, "SELECT k, count(*) FROM t GROUP BY k;");

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        rows[1].Should().Equal(SqlValue.Null, SqlValue.Integer(2));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void GroupByTreatsEqualNumericKeysAsOneGroup()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k);");
        Execute(connection, "INSERT INTO t VALUES (1), (1.0), (1);");

        var rows = ReadRows(connection, "SELECT count(*) FROM t GROUP BY k;");

        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(3));
    }

    [Test]
    public void WhereFiltersRowsBeforeAggregation()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (1, 1), (2, 30), (2, 2);");

        ReadRows(connection, "SELECT count(*), sum(v) FROM t WHERE v > 5;")[0]
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(40));

        var grouped = ReadRows(connection, "SELECT k, count(*) FROM t WHERE v > 5 GROUP BY k;");
        grouped.Should().HaveCount(2);
        grouped[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        grouped[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void ConstantProjectionRoutesAlongsideAggregate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        var rows = ReadRows(connection, "SELECT count(*), 42 FROM t;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(42));

        // Proves the whole statement (including the folded constant) went through the accumulator.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT count(*), 42 FROM t;"))
            .Should().Contain("AggReset").And.Contain("AggStep").And.Contain("AggFinalize");
    }

    [Test]
    public void ScalarAggregateExplainEmitsTheAccumulatorProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN SELECT sum(value) FROM t;");

        Opcodes(rows).Should().Equal(
            "OpenReadCursor",
            "AggReset",
            "Rewind",
            "Column",
            "AggStep",
            "Next",
            "CloseCursor",
            "AggFinalize",
            "Copy",
            "ResultRow",
            "Halt");

        Comments(rows).Should().Contain("reset accumulator 0")
            .And.Contain("accumulator 0=sum step r[0]")
            .And.Contain("r[1]=sum finalize accumulator 0");
    }

    [Test]
    public void NullaryCountExplainDescribesAnEmptyArgumentRange()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Comments(ReadRows(connection, "EXPLAIN SELECT count(*) FROM t;"))
            .Should().Contain("accumulator 0=count step r[]");
    }

    [Test]
    public void GroupedAggregateExplainEmitsGotoAndSameGroup()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT k, sum(v) FROM t GROUP BY k;")).ToList();

        opcodes.Should().Contain("AggReset")
            .And.Contain("AggStep")
            .And.Contain("AggFinalize")
            .And.Contain("Goto")
            .And.Contain("SameGroup");
    }

    [Test]
    public void WhereFilteredScalarAggregateExplainEmitsFilterAndAccumulator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT count(*) FROM t WHERE value > 1;")).ToList();

        opcodes.Should().Contain("Filter")
            .And.Contain("AggReset")
            .And.Contain("AggStep")
            .And.Contain("AggFinalize");
    }

    [Test]
    public void GroupedAggregateResetReplayReflectsAppendedRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (1, 30);");

        using var statement = connection.Prepare("SELECT k, count(*), sum(v) FROM t GROUP BY k;");

        DrainGrouped(statement).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(40)),
            (SqlValue.Integer(2), SqlValue.Integer(1), SqlValue.Integer(20)));

        Execute(connection, "INSERT INTO t VALUES (2, 5), (3, 7);");

        statement.Reset();
        DrainGrouped(statement).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(40)),
            (SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(25)),
            (SqlValue.Integer(3), SqlValue.Integer(1), SqlValue.Integer(7)));
    }

    [Test]
    public void HavingFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (1), (2);");

        var rows = ReadRows(connection, "SELECT k, count(*) FROM t GROUP BY k HAVING count(*) >= 2;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        // HAVING is a deliberate fallback boundary: EXPLAIN cannot describe an un-lowered plan.
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT k, count(*) FROM t GROUP BY k HAVING count(*) >= 2;"));
    }

    [Test]
    public void OrderByFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (2), (3), (3), (3);");

        var rows = ReadRows(connection, "SELECT k, count(*) FROM t GROUP BY k ORDER BY count(*) DESC;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Integer(3), SqlValue.Integer(2), SqlValue.Integer(1));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT k, count(*) FROM t GROUP BY k ORDER BY count(*) DESC;"));
    }

    [Test]
    public void DistinctAggregateFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (1), (2), (3), (3);");

        ReadRows(connection, "SELECT count(DISTINCT v) FROM t;")[0][0].Should().Be(SqlValue.Integer(3));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT count(DISTINCT v) FROM t;"));
    }

    [Test]
    public void CompositeAggregateExpressionFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20);");

        ReadRows(connection, "SELECT sum(v) + 1 FROM t;")[0][0].Should().Be(SqlValue.Integer(31));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT sum(v) + 1 FROM t;"));
    }

    [Test]
    public void GroupKeyOnlyProjectionFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2), (1), (2), (1);");

        // No aggregate in the projection, so the builder's "at least one aggregate" rule declines.
        ReadRows(connection, "SELECT k FROM t GROUP BY k;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT k FROM t GROUP BY k;"));
    }

    [Test]
    public void MixingAggregateAndBareColumnStillRaisesEvaluatorError()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT count(*), value FROM t;"))!;
        error.Message.Should().Be("Mixing aggregate and non-aggregate expressions is not supported.");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT count(*), value FROM t;"));
    }

    [Test]
    public void NonGroupedColumnStillRaisesEvaluatorError()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10);");

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT k, v FROM t GROUP BY k;"))!;
        error.Message.Should().Be("Non-aggregate projections must appear in GROUP BY.");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT k, v FROM t GROUP BY k;"));
    }

    private static List<(SqlValue, SqlValue, SqlValue)> DrainGrouped(EmbeddedStatement statement)
    {
        var rows = new List<(SqlValue, SqlValue, SqlValue)>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add((statement.GetValue(0), statement.GetValue(1), statement.GetValue(2)));

        return rows;
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);

            rows.Add(values);
        }

        return rows;
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var names = new string[statement.GetColumnCount()];
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
            names[ordinal] = statement.GetColumnName(ordinal);

        return names;
    }
}
