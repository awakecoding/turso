using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

// Exercises the sorter-backed compiled SELECT route wired into EmbeddedDatabase: single
// base-table ORDER BY pipelines lower to a real VdbeProgram (OpenSorter/SorterInsert/
// SorterSort/SorterData/SorterNext/CloseSorter) instead of the tree-walking evaluator.
// Every ordering test asserts both the emitted bytecode (proving the route is genuinely
// used) and the produced rows (proving semantics are preserved), and the fallback tests
// prove excluded shapes stay on the evaluator with correct results.
public class CompiledSortedScanExecutionTests
{
    [Test]
    public void ExplainEmitsSorterProgramForOrderedScan()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var columns = ColumnNames(connection, "EXPLAIN SELECT value FROM t ORDER BY value;");
        columns.Should().Equal("addr", "opcode", "p1", "p2", "p3", "p4", "comment");

        var rows = ReadRows(connection, "EXPLAIN SELECT value FROM t ORDER BY value;");
        Opcodes(rows).Should().Equal(
            "OpenReadCursor",
            "OpenSorter",
            "Rewind",
            "Column",
            "SorterInsert",
            "Next",
            "CloseCursor",
            "SorterSort",
            "SorterData",
            "Copy",
            "ResultRow",
            "SorterNext",
            "CloseSorter",
            "Halt");

        // addr counts up from zero.
        for (var index = 0; index < rows.Count; index++)
            rows[index][0].Should().Be(SqlValue.Integer(index));

        // OpenSorter reports its materialized column width (the full 1-column row).
        rows[1][4].Should().Be(SqlValue.Integer(1));
        rows[1][6].Should().Be(SqlValue.Text("open sorter 0 (1 cols)"));

        // Rewind jumps to SorterSort (addr 7) when the table is empty.
        rows[2][3].Should().Be(SqlValue.Integer(7));

        // SorterInsert stages the full scanned row r[0].
        rows[4][6].Should().Be(SqlValue.Text("sorter 0 insert r[0]"));

        // SorterSort drains from addr 8, or jumps to CloseSorter (addr 12) when empty.
        rows[7][3].Should().Be(SqlValue.Integer(12));
        rows[7][6].Should().Be(SqlValue.Text("sort sorter 0, goto 12 if empty"));

        // SorterData copies the sorted record back into the staging register.
        rows[8][6].Should().Be(SqlValue.Text("r[0]=sorter 0 data"));

        // SorterNext loops back to the drain start (addr 8) while rows remain.
        rows[11][3].Should().Be(SqlValue.Integer(8));
        rows[11][6].Should().Be(SqlValue.Text("next sorter 0, goto 8 if more rows"));

        rows[12][6].Should().Be(SqlValue.Text("close sorter 0"));
    }

    [Test]
    public void ExplainEmitsFilterInsideSorterProgramWhenWherePresent()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN SELECT value FROM t WHERE value > 1 ORDER BY value;");
        Opcodes(rows).Should().Equal(
            "OpenReadCursor",
            "OpenSorter",
            "Rewind",
            "Filter",
            "Column",
            "SorterInsert",
            "Next",
            "CloseCursor",
            "SorterSort",
            "SorterData",
            "Copy",
            "ResultRow",
            "SorterNext",
            "CloseSorter",
            "Halt");
    }

    [Test]
    public void OrderedScanReturnsAscendingRowsThroughTheSorter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (3), (1), (2);");

        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY value;").Should().BeTrue();

        var rows = ReadRows(connection, "SELECT value FROM t ORDER BY value;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void OrderedScanReturnsDescendingRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (3), (1), (2);");

        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY value DESC;").Should().BeTrue();

        var rows = ReadRows(connection, "SELECT value FROM t ORDER BY value DESC;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Integer(3), SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void MultiKeyOrderMixesAscendingAndDescending()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 1), (2, 1), (1, 2);");

        RouteUsesSorter(connection, "SELECT a, b FROM t ORDER BY a ASC, b DESC;").Should().BeTrue();

        var rows = ReadRows(connection, "SELECT a, b FROM t ORDER BY a ASC, b DESC;");
        rows.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(2)),
            (SqlValue.Integer(1), SqlValue.Integer(1)),
            (SqlValue.Integer(2), SqlValue.Integer(1)));
    }

    [Test]
    public void TiedKeysPreserveScanOrderBecauseTheSorterIsStable()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(grp INTEGER, tag TEXT);");
        // Rows are interleaved so a naive unstable sort could reorder the grp=1 ties.
        Execute(connection, "INSERT INTO t VALUES (1, 'a'), (2, 'd'), (1, 'b'), (1, 'c');");

        RouteUsesSorter(connection, "SELECT tag FROM t ORDER BY grp;").Should().BeTrue();

        var rows = ReadRows(connection, "SELECT tag FROM t ORDER BY grp;");
        rows.Select(row => row[0]).Should().Equal(
            SqlValue.Text("a"),
            SqlValue.Text("b"),
            SqlValue.Text("c"),
            SqlValue.Text("d"));
    }

    [Test]
    public void NullKeysSortFirstMatchingSqliteOrdering()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2), (NULL), (1);");

        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY value;").Should().BeTrue();

        var rows = ReadRows(connection, "SELECT value FROM t ORDER BY value;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Null, SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void OrderByAppliesCollationInsideTheSorter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(name TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('Banana'), ('apple'), ('Cherry');");

        RouteUsesSorter(connection, "SELECT name FROM t ORDER BY name COLLATE NOCASE;").Should().BeTrue();

        var rows = ReadRows(connection, "SELECT name FROM t ORDER BY name COLLATE NOCASE;");
        rows.Select(row => row[0]).Should().Equal(
            SqlValue.Text("apple"),
            SqlValue.Text("Banana"),
            SqlValue.Text("Cherry"));
    }

    [Test]
    public void OrderByHonoursCustomCollationThroughTheSorter()
    {
        var database = new EmbeddedDatabase();
        database.RegisterCollation("reverse_text", (left, right) => string.CompareOrdinal(right, left));
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(name TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a'), ('b'), ('c');");

        RouteUsesSorter(connection, "SELECT name FROM t ORDER BY name COLLATE reverse_text;").Should().BeTrue();

        var rows = ReadRows(connection, "SELECT name FROM t ORDER BY name COLLATE reverse_text;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Text("c"), SqlValue.Text("b"), SqlValue.Text("a"));
    }

    [Test]
    public void OrderedScanProjectsConstantsColumnsAndStars()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");
        Execute(connection, "INSERT INTO t VALUES (2, 'b'), (1, 'a');");

        // Constant + column projection ordered by a column.
        RouteUsesSorter(connection, "SELECT 7, name FROM t ORDER BY id;").Should().BeTrue();
        var mixed = ReadRows(connection, "SELECT 7, name FROM t ORDER BY id;");
        mixed.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(7), SqlValue.Text("a")),
            (SqlValue.Integer(7), SqlValue.Text("b")));

        // Star expands to every declared column in order.
        RouteUsesSorter(connection, "SELECT * FROM t ORDER BY id;").Should().BeTrue();
        var star = ReadRows(connection, "SELECT * FROM t ORDER BY id;");
        star.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Text("a")),
            (SqlValue.Integer(2), SqlValue.Text("b")));
    }

    [Test]
    public void ParameterisedWhereFiltersBeforeOrderingThroughTheSorter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3), (4);");

        using var statement = connection.Prepare("SELECT value FROM t WHERE value >= ?1 ORDER BY value DESC;");
        statement.Bind(1, SqlValue.Integer(3));

        var rows = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(statement.GetValue(0));

        rows.Should().Equal(SqlValue.Integer(4), SqlValue.Integer(3));
    }

    [Test]
    public void OrderByOutputAliasAndOrdinalRouteThroughTheSorter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2), (1), (3);");

        RouteUsesSorter(connection, "SELECT value AS result FROM t ORDER BY result;").Should().BeTrue();
        ReadRows(connection, "SELECT value AS result FROM t ORDER BY result;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));

        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY 1 DESC;").Should().BeTrue();
        ReadRows(connection, "SELECT value FROM t ORDER BY 1 DESC;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(3), SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void OrderedScanOverEmptyTableDrainsNoRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY value;").Should().BeTrue();
        ReadRows(connection, "SELECT value FROM t ORDER BY value;").Should().BeEmpty();
    }

    [Test]
    public void OrderedScanSupportsResetAndReplaysLiveRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2), (1);");

        using var statement = connection.Prepare("SELECT value FROM t ORDER BY value;");
        var first = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            first.Add(statement.GetValue(0));
        first.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        // A row inserted after preparation is picked up on the next run: the sorter
        // re-materializes the live rows each execution.
        Execute(connection, "INSERT INTO t VALUES (0);");

        statement.Reset();
        var replayed = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            replayed.Add(statement.GetValue(0));

        replayed.Should().Equal(SqlValue.Integer(0), SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void LimitAndOffsetRouteThroughSorterAndGates()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (3), (1), (2), (4);");

        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY value LIMIT 2 OFFSET 1;").Should().BeTrue();
        var program = ReadRows(connection, "EXPLAIN SELECT value FROM t ORDER BY value LIMIT 2 OFFSET 1;");
        Opcodes(program).Should().Equal(
            "LoadConstant",
            "LoadConstant",
            "OpenReadCursor",
            "OpenSorter",
            "Rewind",
            "Column",
            "SorterInsert",
            "Next",
            "CloseCursor",
            "SorterSort",
            "SorterData",
            "Copy",
            "OffsetGate",
            "LimitGate",
            "ResultRow",
            "SorterNext",
            "CloseSorter",
            "Halt");
        program[12][6].Should().Be(SqlValue.Text("goto 15 and decrement r[2] while r[2]>0"));
        program[13][6].Should().Be(SqlValue.Text("goto 17 when r[3]<=0, else decrement r[3]"));

        ReadRows(connection, "SELECT value FROM t ORDER BY value LIMIT 2;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        ReadRows(connection, "SELECT value FROM t ORDER BY value LIMIT 2 OFFSET 1;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void BoundedSorterPreservesAffinityAliasesOrdinalsAndEvaluatorResults()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(rank NUMERIC, name TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('10', 'zulu'), ('2', 'bravo'), (NULL, 'alpha'), ('7', 'charlie');");

        const string routedSql =
            "SELECT name, rank AS score FROM t ORDER BY 2 DESC LIMIT 2 OFFSET 1;";
        RouteUsesSorter(connection, routedSql).Should().BeTrue();
        ColumnNames(connection, routedSql).Should().Equal("name", "score");

        var routed = ReadRows(connection, routedSql);
        routed.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Text("charlie"), SqlValue.Integer(7)),
            (SqlValue.Text("bravo"), SqlValue.Integer(2)));

        const string aliasSql =
            "SELECT name, rank AS score FROM t ORDER BY score DESC LIMIT 2 OFFSET 1;";
        RouteUsesSorter(connection, aliasSql).Should().BeTrue();
        ReadRows(connection, aliasSql).Select(row => (row[0], row[1]))
            .Should().Equal(routed.Select(row => (row[0], row[1])));

        // rank + 0 has the same numeric ordering for these NUMERIC-affinity values but is a
        // computed ORDER BY expression, so it deliberately stays on the evaluator.
        const string evaluatorSql =
            "SELECT name, rank AS score FROM t ORDER BY rank + 0 DESC LIMIT 2 OFFSET 1;";
        RouteUsesSorter(connection, evaluatorSql).Should().BeFalse();
        ReadRows(connection, evaluatorSql).Select(row => (row[0], row[1]))
            .Should().Equal(routed.Select(row => (row[0], row[1])));
    }

    [Test]
    public void BoundedSorterPreservesNoCaseNullAndDescendingOrdering()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(name TEXT);");
        Execute(connection, "INSERT INTO t VALUES (NULL), ('Banana'), ('apple'), ('Cherry');");

        const string sql =
            "SELECT name FROM t ORDER BY name COLLATE NOCASE DESC LIMIT 3 OFFSET 1;";
        RouteUsesSorter(connection, sql).Should().BeTrue();

        ReadRows(connection, sql).Select(row => row[0]).Should().Equal(
            SqlValue.Text("Banana"),
            SqlValue.Text("apple"),
            SqlValue.Null);
    }

    [Test]
    public void BoundedSorterRecompilesWithResetAndReboundParameters()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3), (4), (5);");

        using var statement = connection.Prepare(
            "SELECT value AS ranked FROM t WHERE value >= ?1 ORDER BY 1 DESC LIMIT ?2 OFFSET ?3;");
        statement.GetColumnName(0).Should().Be("ranked");
        statement.Bind(1, SqlValue.Integer(2));
        statement.Bind(2, SqlValue.Integer(2));
        statement.Bind(3, SqlValue.Integer(1));
        Drain(statement).Should().Equal(SqlValue.Integer(4), SqlValue.Integer(3));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(3));
        statement.Bind(2, SqlValue.Integer(3));
        statement.Bind(3, SqlValue.Integer(0));
        Drain(statement).Should().Equal(SqlValue.Integer(5), SqlValue.Integer(4), SqlValue.Integer(3));
    }

    [Test]
    public void DistinctBoundedOrderStaysOnTheEvaluator()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (1), (2);");

        RouteUsesSorter(connection, "SELECT DISTINCT value FROM t ORDER BY value LIMIT 2;").Should().BeFalse();
        ReadRows(connection, "SELECT DISTINCT value FROM t ORDER BY value LIMIT 2;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void BoundedOrderPreflightRoutesSafeJoinButLeavesOtherShapesOnEvaluator()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "CREATE TABLE u(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (3), (1), (2);");
        Execute(connection, "INSERT INTO u VALUES (2), (1), (3);");

        const string computedProjection = "SELECT value + 1 FROM t ORDER BY value LIMIT 2;";
        RouteUsesSorter(connection, computedProjection).Should().BeFalse();
        ReadRows(connection, computedProjection).Select(row => row[0])
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3));

        const string join = "SELECT t.value FROM t JOIN u ON t.value = u.value ORDER BY t.value LIMIT 2;";
        RouteUsesSorter(connection, join).Should().BeTrue();
        ReadRows(connection, join).Select(row => row[0])
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        const string compound = "SELECT value FROM t UNION ALL SELECT value FROM u ORDER BY 1 LIMIT 2;";
        RouteUsesSorter(connection, compound).Should().BeFalse();
        ReadRows(connection, compound).Select(row => row[0])
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
    }

    [Test]
    public void BoundedOrderFallsBackForLimitZeroAndEvaluatorErrors()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        // LIMIT 0 validates expressions without scanning, so a gated sorter cannot own it.
        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY value LIMIT 0;").Should().BeFalse();
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT missing FROM t ORDER BY value LIMIT 0;"));

        // Unsupported order keys and bad bounds remain evaluator errors rather than partially
        // compiled programs with different diagnostics.
        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY missing LIMIT 1;").Should().BeFalse();
        var orderError = Assert.Throws<InvalidOperationException>(
            () => ReadRows(connection, "SELECT value FROM t ORDER BY missing LIMIT 1;"))!;
        orderError.InnerException.Should().BeOfType<EmbeddedSqlException>()
            .Which.Message.Should().Be("no such column: missing");

        RouteUsesSorter(connection, "SELECT value FROM t ORDER BY value LIMIT 'x';").Should().BeFalse();
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT value FROM t ORDER BY value LIMIT 'x';"))!
            .Message.Should().Be("datatype mismatch");
    }

    [Test]
    public void OrderByUnbackedRowidStaysOnTheEvaluator()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (30), (10), (20);");

        // rowid is not a materialized declared column, so an ORDER BY over it must run on
        // the evaluator, which resolves the hidden rowid from the scanned row.
        RouteUsesSorter(connection, "SELECT a FROM t ORDER BY rowid DESC;").Should().BeFalse();
        ReadRows(connection, "SELECT a FROM t ORDER BY rowid DESC;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(20), SqlValue.Integer(10), SqlValue.Integer(30));
    }

    [Test]
    public void ComputedProjectionStaysOnTheEvaluator()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2), (1);");

        // value + 1 is neither a bare column nor a folded constant, so the projection is
        // not lowered and the whole statement falls back to the evaluator.
        RouteUsesSorter(connection, "SELECT value + 1 FROM t ORDER BY value;").Should().BeFalse();
        ReadRows(connection, "SELECT value + 1 FROM t ORDER BY value;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void SchemaTableOrderingStaysOnTheEvaluator()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE beta(x INTEGER);");
        Execute(connection, "CREATE TABLE alpha(y INTEGER);");

        // sqlite_master is not a base scan target, so ordered reads of it stay on the
        // evaluator rather than the sorter route.
        RouteUsesSorter(connection, "SELECT name FROM sqlite_master ORDER BY name;").Should().BeFalse();
        ReadRows(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Text("alpha"), SqlValue.Text("beta"));
    }

    // Returns true when EXPLAIN of the query lowers to a sorter-backed program, i.e. the
    // compiled ORDER BY route (not the evaluator) owns execution.
    private static bool RouteUsesSorter(EmbeddedConnection connection, string sql)
    {
        try
        {
            var rows = ReadRows(connection, "EXPLAIN " + sql);
            return Opcodes(rows).Contains("SorterSort");
        }
        catch (EmbeddedSqlException)
        {
            // EXPLAIN throws only when the statement was not lowered to bytecode at all,
            // which means the evaluator owns it.
            return false;
        }
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static List<SqlValue> Drain(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(statement.GetValue(0));

        return rows;
    }

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
