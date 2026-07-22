using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

// Proves that EmbeddedDatabase routes the byte-identical-safe RETURNING arithmetic subset of
// INSERT/UPDATE/DELETE through the real Arithmetic opcode (DmlStatementCompiler.EmitExpression +
// VdbeArithmetic.Evaluate) and that every other RETURNING expression stays on the tree-walking
// evaluator. A RETURNING item lowers to the compiled path only when it is an arithmetic operation
// (+, -, *, /, %) whose every operand recursively resolves to:
//
//   * a numeric-affinity (INTEGER/REAL/NUMERIC) column of the affected row,
//   * the affected row's rowid,
//   * an INTEGER/REAL/NULL literal,
//   * a parameter whose currently bound value classifies as INTEGER/REAL/NULL, or
//   * a nested arithmetic node over the same.
//
// That is exactly where VdbeArithmetic matches the evaluator's numeric operators for the values a
// numeric-affinity column can feed. A TEXT/BLOB-affinity column operand, a non-numeric constant or
// parameter, and every function / subquery / collation / cast / comparison / concatenation / complex
// operand fall back, because there the opcode would raise a type error where the evaluator applies
// numeric affinity. Bare projections ("*", a column, the rowid, a folded constant) are owned by the
// existing star/column/constant routes, so only genuine arithmetic enters the new path.
//
// As in the sibling routing suites, EXPLAIN is the ground truth for "was this lowered to bytecode?":
// a routed statement dumps its opcode stream (including Arithmetic), while every deliberate fallback
// shape throws because EXPLAIN only describes lowered programs. Because compilation happens per
// execution, a parameter operand is re-baked (and re-classified) on every Step, so a rebind to a
// text/blob value re-declines and the whole statement falls back.
public class DmlReturningArithmeticSqlRoutingTests
{
    // ---- routed opcode proofs ------------------------------------------------------------------

    [Test]
    public void InsertReturningColumnArithmeticRoutesToArithmeticOpcode()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Column", "LoadConstant", "Arithmetic",
            "ResultRow", "Next", "Commit", "CloseCursor", "Halt");

        // The column reads into a scratch register, the literal bakes to another, and the real
        // Arithmetic opcode folds the operand block into the output register the ResultRow emits.
        Comments(rows).Should().Contain("r[1]=c0.col[0]");
        Comments(rows).Should().Contain("r[2]=1");
        Comments(rows).Should().Contain("r[0]=r[1] + r[2]");
        Comments(rows).Should().Contain("output=r[0]");

        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING value + 1;")
            .Should().Be(SqlValue.Integer(11));
    }

    [Test]
    public void AllFiveArithmeticOperatorsRouteForInsertReturning()
    {
        foreach (var (op, expected) in new[]
                 {
                     ("+", SqlValue.Integer(13)),
                     ("-", SqlValue.Integer(7)),
                     ("*", SqlValue.Integer(30)),
                     // Integer division truncates toward zero, exactly as the evaluator does.
                     ("/", SqlValue.Integer(3)),
                     ("%", SqlValue.Integer(1)),
                 })
        {
            using var connection = Connect();
            Execute(connection, "CREATE TABLE t(value INTEGER);");

            Opcodes(ReadRows(connection, $"EXPLAIN INSERT INTO t VALUES (10) RETURNING value {op} 3;"))
                .Should().Contain("Arithmetic");
            RoutedValue(connection, $"INSERT INTO t VALUES (10) RETURNING value {op} 3;")
                .Should().Be(expected);
        }
    }

    [Test]
    public void InsertReturningRowidArithmeticRoutes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING rowid + 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "RowId", "LoadConstant", "Arithmetic",
            "ResultRow", "Next", "Commit", "CloseCursor", "Halt");

        // The rowid pseudo-column is always an integer, so it feeds arithmetic through the dedicated
        // RowId opcode regardless of any declared column affinity.
        Comments(rows).Should().Contain("r[1]=c0.rowid");

        // The first auto-assigned rowid is 1, so the routed fold returns 2.
        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING rowid + 1;")
            .Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void InsertReturningNestedArithmeticRoutes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A nested arithmetic operand recurses through the same lowering, emitting one Arithmetic
        // opcode per operation over its own scratch operand block.
        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING (value + 1) * 2;");
        Opcodes(rows).Count(opcode => opcode == "Arithmetic").Should().Be(2);
        Opcodes(rows).Should().Contain("Arithmetic");

        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING (value + 1) * 2;")
            .Should().Be(SqlValue.Integer(22));
    }

    [Test]
    public void InsertReturningParameterOperandRoutesWhenBoundNumeric()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A parameter bound to an integer bakes to a LoadConstant, so the fold routes.
        Opcodes(ExplainBound(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + ?;", SqlValue.Integer(5)))
            .Should().Contain("Arithmetic");

        using var statement = connection.Prepare("INSERT INTO t VALUES (10) RETURNING value + ?;");
        statement.Bind(1, SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(15));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Each Step recompiles and re-bakes the parameter, so a reset with a fresh binding routes
        // the new value.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(100));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(110));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void RealColumnArithmeticRoutes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value REAL);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (2.5) RETURNING value * 2;"))
            .Should().Contain("Arithmetic");
        RoutedValue(connection, "INSERT INTO t VALUES (2.5) RETURNING value * 2;")
            .Should().Be(SqlValue.Real(5.0));
    }

    [Test]
    public void NumericAffinityColumnArithmeticRoutes()
    {
        using var connection = Connect();

        // NUMERIC affinity is a numeric (non-text, non-blob) affinity, so it is part of the routable
        // subset alongside INTEGER and REAL.
        Execute(connection, "CREATE TABLE t(value NUMERIC);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + 1;"))
            .Should().Contain("Arithmetic");
        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING value + 1;")
            .Should().Be(SqlValue.Integer(11));
    }

    [Test]
    public void UpdateReturningColumnArithmeticRoutesAndObservesPostWriteRow()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10);");

        var rows = ReadRows(connection, "EXPLAIN UPDATE t SET value = 20 RETURNING value + 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Update", "Column", "LoadConstant", "Arithmetic",
            "ResultRow", "Next", "Commit", "CloseCursor", "Halt");

        // UPDATE RETURNING projects the post-write row, so value + 1 folds over the new 20.
        RoutedValue(connection, "UPDATE t SET value = 20 RETURNING value + 1;")
            .Should().Be(SqlValue.Integer(21));
    }

    [Test]
    public void DeleteReturningColumnArithmeticRoutesAndObservesPreDeleteRow()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10);");

        var rows = ReadRows(connection, "EXPLAIN DELETE FROM t RETURNING value + 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Delete", "Column", "LoadConstant", "Arithmetic",
            "ResultRow", "Next", "Commit", "CloseCursor", "Halt");

        // DELETE RETURNING projects the pre-delete row, so value + 1 folds over the removed 10.
        RoutedValue(connection, "DELETE FROM t RETURNING value + 1;")
            .Should().Be(SqlValue.Integer(11));
        ReadRows(connection, "SELECT value FROM t;").Should().BeEmpty();
    }

    [Test]
    public void PureConstantArithmeticFoldsWithoutArithmeticOpcode()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // Arithmetic over only constants is constant-folded by the constant-projection route ahead of
        // this one, so it bakes a single LoadConstant and emits no Arithmetic opcode.
        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING 1 + 2;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "LoadConstant", "ResultRow", "Next", "Commit",
            "CloseCursor", "Halt");
        Opcodes(rows).Should().NotContain("Arithmetic");
        Comments(rows).Should().Contain("r[0]=3");

        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING 1 + 2;")
            .Should().Be(SqlValue.Integer(3));
    }

    // ---- evaluator fallbacks -------------------------------------------------------------------

    [Test]
    public void TextAffinityColumnOperandFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(label TEXT);");

        // A TEXT-affinity column can hold arbitrary text, so the Arithmetic opcode (which raises on
        // text) cannot claim byte-identity; the statement stays on the affinity-applying evaluator.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES ('10') RETURNING label + 1;");

        RoutedValue(connection, "INSERT INTO t VALUES ('10') RETURNING label + 1;")
            .Should().Be(SqlValue.Integer(11));
    }

    [Test]
    public void BlobAffinityColumnOperandFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(data BLOB);");

        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (x'0102') RETURNING data + 1;");
    }

    [Test]
    public void FunctionOperandFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A function call is not a leaf the Arithmetic operand lowering accepts, so it declines.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (-4) RETURNING abs(value) + 1;");

        RoutedValue(connection, "INSERT INTO t VALUES (-4) RETURNING abs(value) + 1;")
            .Should().Be(SqlValue.Integer(5));
    }

    [Test]
    public void SubqueryOperandFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A scalar subquery operand is outside the leaf subset, so the whole projection declines.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + (SELECT 1);");
    }

    [Test]
    public void CollationOperandFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // COLLATE binds tighter than the arithmetic operator, so the left operand is a collation
        // node, which carries collation semantics the Arithmetic opcode does not model.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value COLLATE NOCASE + 1;");
    }

    [Test]
    public void CastOperandFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A CAST operand is not a leaf the operand lowering accepts, so it declines.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING CAST(value AS INTEGER) + 1;");
    }

    [Test]
    public void ComparisonProjectionFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A comparison is a BinaryExpression but not an arithmetic operator, so it never enters the
        // arithmetic route.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value < 5;");

        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING value < 5;")
            .Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void ConcatenationProjectionFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // Concatenation carries text semantics the numeric Arithmetic opcode does not model.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value || 'x';");
    }

    [Test]
    public void BareParameterProjectionFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A bare parameter is not an arithmetic operation, so the new route leaves it alone (and it is
        // not a constant scalar either), keeping the evaluator authoritative.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING ?;", SqlValue.Integer(7));
    }

    [Test]
    public void ParameterOperandRebindToTextFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // The same statement routes with a numeric parameter but declines once the parameter is
        // rebound to text, because each Step re-bakes and re-classifies the operand.
        Opcodes(ExplainBound(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + ?;", SqlValue.Integer(5)))
            .Should().Contain("Arithmetic");
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + ?;", SqlValue.Text("x"));

        // Executing with the text binding still succeeds through the evaluator, which applies numeric
        // affinity to the unparseable text operand (treated as 0).
        using var statement = connection.Prepare("INSERT INTO t VALUES (10) RETURNING value + ?;");
        statement.Bind(1, SqlValue.Text("x"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(10));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void RoutedArithmeticErrorPropagatesBeforeCommit()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A blob stored in a numeric-affinity column is the accepted affinity-routing limitation: the
        // column routes by affinity, but the runtime value makes the Arithmetic opcode raise. The
        // projection runs before Commit, so the error aborts the UPDATE and nothing is persisted.
        Execute(connection, "INSERT INTO t VALUES (x'0102');");

        using (var statement = connection.Prepare("UPDATE t SET value = x'0304' RETURNING value + 1;"))
        {
            Assert.Catch(() => statement.Step());
        }

        // The buffered update was discarded, so the original blob row survives unchanged.
        var rows = ReadRows(connection, "SELECT value FROM t;");
        rows.Should().ContainSingle();
        rows[0][0].Kind.Should().Be(SqlValueKind.Blob);
        rows[0][0].AsBlob().ToArray().Should().Equal(new byte[] { 0x01, 0x02 });
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static EmbeddedConnection Connect() => new EmbeddedDatabase().Connect();

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue RoutedValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static void ExplainRefused(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    private static List<SqlValue[]> ExplainBound(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

        return DrainRows(statement);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(EmbeddedStatement statement)
    {
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

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());
}
