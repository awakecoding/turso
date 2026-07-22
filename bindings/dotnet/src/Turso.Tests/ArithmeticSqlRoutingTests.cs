using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

// Proves that EmbeddedDatabase routes the byte-identical-safe SQL arithmetic subset through the real
// Arithmetic opcode (ArithmeticProgramBuilder.BuildOverValues + VdbeArithmetic.Evaluate) and that every
// other shape stays on the tree-walking evaluator. Only one grammar lowers:
//
//     SELECT <op1> {+,-,*,/,%} <op2>        (source-less, single projection, no other clauses)
//
// where each operand is a literal or a bind parameter whose value classifies as INTEGER, REAL, or NULL.
// That is exactly where VdbeArithmetic matches the evaluator's numeric operators: integer/real typing,
// integer-overflow-to-real, divide/modulo-by-zero-to-NULL, and NULL short-circuiting all agree for
// numeric/NULL operands. Text and blob operands are excluded because the opcode raises a type error where
// the evaluator applies numeric affinity, so they -- along with column/nested/complex operands, the
// concatenation/comparison/logical operators, and any FROM/WHERE/DISTINCT/... clause -- must fall back.
//
// As in the sibling routing suites, EXPLAIN is the ground truth for "was this lowered to bytecode?": a
// routed statement dumps its opcode stream (including Arithmetic), while every deliberate fallback shape
// throws because EXPLAIN only describes lowered programs. Because pure-literal arithmetic is constant-folded
// by the constant-projection route ahead of this one, this route fires only when at least one operand is a
// parameter; each Step recompiles, so a rebind re-bakes (and re-classifies) the fresh operand value.
public class ArithmeticSqlRoutingTests
{
    // ---- routed values + opcode proof -----------------------------------------------------------------

    [Test]
    public void AdditionOverParameterRoutesToArithmeticOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("SELECT ? + 2");
        statement.Bind(1, SqlValue.Integer(3));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Done);

        // The parameter and the literal each bake to a LoadConstant, then the real Arithmetic opcode folds
        // the operand register block into the result; cursor-less, one ResultRow, a terminating Halt.
        var rows = ExplainBound(connection, "EXPLAIN SELECT ? + 2", SqlValue.Integer(3));
        Opcodes(rows).Should().Equal("LoadConstant", "LoadConstant", "Arithmetic", "ResultRow", "Halt");
        Comments(rows).Should().Contain("r[2]=r[0] + r[1]");
    }

    [Test]
    public void AllFiveArithmeticOperatorsRouteAndMatchEvaluatorSemantics()
    {
        using var connection = new EmbeddedDatabase().Connect();

        RoutedValue(connection, "SELECT ? + ?", SqlValue.Integer(10), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(13));
        RoutedValue(connection, "SELECT ? - ?", SqlValue.Integer(10), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(7));
        RoutedValue(connection, "SELECT ? * ?", SqlValue.Integer(10), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(30));
        // Integer division truncates toward zero, exactly as the evaluator's ApplyDivision does.
        RoutedValue(connection, "SELECT ? / ?", SqlValue.Integer(10), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(3));
        RoutedValue(connection, "SELECT ? % ?", SqlValue.Integer(10), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(1));

        foreach (var sql in new[]
                 {
                     "EXPLAIN SELECT ? + ?", "EXPLAIN SELECT ? - ?", "EXPLAIN SELECT ? * ?",
                     "EXPLAIN SELECT ? / ?", "EXPLAIN SELECT ? % ?",
                 })
        {
            Opcodes(ExplainBound(connection, sql, SqlValue.Integer(10), SqlValue.Integer(3)))
                .Should().Contain("Arithmetic");
        }
    }

    [Test]
    public void UnaryMinusLowersToSubtractAndRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The parser rewrites unary "-x" as "0 - x", so a negated parameter is a binary Subtract over a
        // literal 0 and the parameter -- squarely in the routable subset.
        using var statement = connection.Prepare("SELECT -?");
        statement.Bind(1, SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(-5));

        var rows = ExplainBound(connection, "EXPLAIN SELECT -?", SqlValue.Integer(5));
        Opcodes(rows).Should().Equal("LoadConstant", "LoadConstant", "Arithmetic", "ResultRow", "Halt");
        Comments(rows).Should().Contain("r[2]=r[0] - r[1]");
        // p4 carries the operator symbol and the leading literal 0 that the rewrite introduced.
        Dump(rows).Should().Contain(entry => entry.StartsWith("LoadConstant") && entry.Contains("|r[0]=0"));
    }

    [Test]
    public void RealOperandProducesRealResultThroughOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A real operand promotes the whole operation to double, matching the evaluator's AsReal path.
        RoutedValue(connection, "SELECT ? / ?", SqlValue.Real(7.0), SqlValue.Integer(2))
            .Should().Be(SqlValue.Real(3.5));
        RoutedValue(connection, "SELECT ? + ?", SqlValue.Integer(1), SqlValue.Real(0.5))
            .Should().Be(SqlValue.Real(1.5));

        Opcodes(ExplainBound(connection, "EXPLAIN SELECT ? / ?", SqlValue.Real(7.0), SqlValue.Integer(2)))
            .Should().Contain("Arithmetic");
    }

    // ---- NULL / zero / overflow boundaries ------------------------------------------------------------

    [Test]
    public void NullOperandPropagatesToNullThroughOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A NULL literal operand makes the whole expression NULL; the opcode short-circuits before typing
        // the surviving operand, exactly as the evaluator does.
        using (var statement = connection.Prepare("SELECT ? + NULL"))
        {
            statement.Bind(1, SqlValue.Integer(5));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Kind.Should().Be(SqlValueKind.Null);
        }

        // A NULL-valued parameter propagates identically.
        RoutedValue(connection, "SELECT NULL * ?", SqlValue.Integer(5)).Kind.Should().Be(SqlValueKind.Null);
        RoutedValue(connection, "SELECT ? % ?", SqlValue.Null, SqlValue.Integer(3)).Kind
            .Should().Be(SqlValueKind.Null);

        // Still lowered: the NULL is a baked LoadConstant feeding the Arithmetic opcode.
        Opcodes(ExplainBound(connection, "EXPLAIN SELECT ? + NULL", SqlValue.Integer(5)))
            .Should().Contain("Arithmetic");
    }

    [Test]
    public void DivideAndModuloByZeroYieldNullThroughOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A zero divisor yields NULL (never a raised divide-by-zero), matching ApplyDivision/ApplyModulo.
        RoutedValue(connection, "SELECT ? / ?", SqlValue.Integer(5), SqlValue.Integer(0)).Kind
            .Should().Be(SqlValueKind.Null);
        RoutedValue(connection, "SELECT ? % ?", SqlValue.Integer(5), SqlValue.Integer(0)).Kind
            .Should().Be(SqlValueKind.Null);
        RoutedValue(connection, "SELECT ? / 0", SqlValue.Integer(5)).Kind.Should().Be(SqlValueKind.Null);

        Opcodes(ExplainBound(connection, "EXPLAIN SELECT ? / 0", SqlValue.Integer(5)))
            .Should().Contain("Arithmetic");
    }

    [Test]
    public void IntegerOverflowPromotesToRealThroughOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // An integer add/multiply that overflows long falls back to the real result rather than wrapping or
        // raising, exactly as the evaluator's checked-then-real fallback does.
        RoutedValue(connection, "SELECT ? + ?", SqlValue.Integer(long.MaxValue), SqlValue.Integer(1))
            .Should().Be(SqlValue.Real((double)long.MaxValue + 1.0));

        var product = RoutedValue(connection, "SELECT ? * ?", SqlValue.Integer(long.MaxValue), SqlValue.Integer(2));
        product.Kind.Should().Be(SqlValueKind.Real);
        product.AsReal().Should().Be((double)long.MaxValue * 2.0);

        Opcodes(ExplainBound(
                connection, "EXPLAIN SELECT ? + ?", SqlValue.Integer(long.MaxValue), SqlValue.Integer(1)))
            .Should().Contain("Arithmetic");
    }

    // ---- parameter baking: rebind + baked-constant visibility -----------------------------------------

    [Test]
    public void BakedOperandIsVisibleInExplainConstant()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The parameter operand is baked, so its EXPLAIN p4 carries the concrete value; a different binding
        // produces a different baked constant.
        var withSeven = Dump(ExplainBound(connection, "EXPLAIN SELECT ? + 2", SqlValue.Integer(7)));
        var withNine = Dump(ExplainBound(connection, "EXPLAIN SELECT ? + 2", SqlValue.Integer(9)));

        withSeven.Should().Contain(entry => entry.StartsWith("LoadConstant") && entry.Contains("7"));
        withNine.Should().Contain(entry => entry.StartsWith("LoadConstant") && entry.Contains("9"));
        withSeven.Should().NotEqual(withNine);
    }

    [Test]
    public void RebindAcrossResetReflectsFreshlyBakedOperand()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("SELECT ? + 1");

        statement.Bind(1, SqlValue.Integer(3));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(4));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Reset then rebind: the routed program recompiles per execution, re-baking the fresh operand.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(9));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(10));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void RebindFromNumericToTextFallsBackOnNextExecution()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("SELECT ? + 1");

        // First execution: numeric operand routes to the Arithmetic opcode.
        statement.Bind(1, SqlValue.Integer(3));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(4));

        // Rebind the same slot to text and re-run: classification declines, so the evaluator takes over and
        // applies numeric affinity ('10' -> 10). The result is still correct, proving the fallback boundary.
        statement.Reset();
        statement.Bind(1, SqlValue.Text("10"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(11));

        // EXPLAIN confirms the routing flip: lowered for the integer binding, refused for the text binding.
        Opcodes(ExplainBound(connection, "EXPLAIN SELECT ? + 1", SqlValue.Integer(3))).Should().Contain("Arithmetic");
        ExplainRefused(connection, "EXPLAIN SELECT ? + 1", SqlValue.Text("10"));
    }

    // ---- fallback boundaries: evaluator keeps ownership, EXPLAIN refuses to describe -------------------

    [Test]
    public void TextOperandFallsBackToEvaluatorButStaysNumericallyExact()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A numeric parameter routes; a text parameter that coerces to the same number falls back to the
        // evaluator's numeric affinity -- both produce the identical value, which is the whole point of the
        // exclusion (the opcode would instead raise a type error).
        RoutedValue(connection, "SELECT ? + 1", SqlValue.Integer(5)).Should().Be(SqlValue.Integer(6));

        using (var statement = connection.Prepare("SELECT ? + 1"))
        {
            statement.Bind(1, SqlValue.Text("5"));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(6));
        }

        Opcodes(ExplainBound(connection, "EXPLAIN SELECT ? + 1", SqlValue.Integer(5))).Should().Contain("Arithmetic");
        ExplainRefused(connection, "EXPLAIN SELECT ? + 1", SqlValue.Text("5"));
    }

    [Test]
    public void BlobOperandFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A blob operand coerces to integer 0 under the evaluator's affinity; the opcode would raise, so the
        // route declines and the evaluator owns it.
        using (var statement = connection.Prepare("SELECT ? + 1"))
        {
            statement.Bind(1, SqlValue.Blob(new byte[] { 0x01, 0x02 }));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        }

        ExplainRefused(connection, "EXPLAIN SELECT ? + 1", SqlValue.Blob(new byte[] { 0x01, 0x02 }));
    }

    [Test]
    public void PureConstantArithmeticFoldsWithoutArithmeticOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // Wholly constant arithmetic is folded by the constant-projection route ahead of this one, so no
        // Arithmetic opcode is emitted -- the value is baked directly.
        ReadRows(connection, "SELECT 1 + 2;")[0][0].Should().Be(SqlValue.Integer(3));
        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT 1 + 2;")).ToList();
        opcodes.Should().Contain("LoadConstant").And.NotContain("Arithmetic");

        // The rewritten unary "-5" (i.e. 0 - 5) is likewise fully constant and folds.
        ReadRows(connection, "SELECT -5;")[0][0].Should().Be(SqlValue.Integer(-5));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT -5;")).Should().NotContain("Arithmetic");
    }

    [Test]
    public void ConcatenationOperatorFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // "||" is not one of the numeric operators VdbeArithmetic models, so it declines.
        using (var statement = connection.Prepare("SELECT ? || 'x'"))
        {
            statement.Bind(1, SqlValue.Text("a"));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Text("ax"));
        }

        ExplainRefused(connection, "EXPLAIN SELECT ? || 'x'", SqlValue.Text("a"));
    }

    [Test]
    public void ComparisonOperatorFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A comparison is a BinaryExpression but not an arithmetic operator, so it stays on the evaluator.
        using (var statement = connection.Prepare("SELECT ? < 5"))
        {
            statement.Bind(1, SqlValue.Integer(3));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        }

        ExplainRefused(connection, "EXPLAIN SELECT ? < 5", SqlValue.Integer(3));
    }

    [Test]
    public void NestedArithmeticOperandFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The right operand is itself a binary expression, not a bare literal/parameter, so the route
        // declines; the evaluator still resolves the whole nested expression (4 + 3 = 7).
        using (var statement = connection.Prepare("SELECT ? + (1 * 3)"))
        {
            statement.Bind(1, SqlValue.Integer(4));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(7));
        }

        ExplainRefused(connection, "EXPLAIN SELECT ? + (1 * 3)", SqlValue.Integer(4));
    }

    [Test]
    public void ColumnOperandOverScanFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(x INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20);");

        // The over-values route is source-less; a column operand means a FROM clause, which declines. (A
        // dynamically-typed column can hold text/blob, so column affinity cannot be proven exact anyway.)
        ReadRows(connection, "SELECT x + 1 FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(11), SqlValue.Integer(21));

        Assert.Throws<EmbeddedSqlException>(
                () => ReadRows(connection, "EXPLAIN SELECT x + 1 FROM t;"))!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    [Test]
    public void MultipleProjectionsFallBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // BuildOverValues emits exactly one arithmetic result column, so more than one projection declines.
        using (var statement = connection.Prepare("SELECT ? + 1, ? + 2"))
        {
            statement.Bind(1, SqlValue.Integer(10));
            statement.Bind(2, SqlValue.Integer(20));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(11));
            statement.GetValue(1).Should().Be(SqlValue.Integer(22));
        }

        ExplainRefused(connection, "EXPLAIN SELECT ? + 1, ? + 2", SqlValue.Integer(10), SqlValue.Integer(20));
    }

    [Test]
    public void DistinctFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using (var statement = connection.Prepare("SELECT DISTINCT ? + 1"))
        {
            statement.Bind(1, SqlValue.Integer(5));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(6));
        }

        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT ? + 1", SqlValue.Integer(5));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static SqlValue RoutedValue(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

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

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());

    private static List<string> Dump(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => $"{row[1].AsText()}|{row[6].AsText()}").ToList();

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return DrainRows(statement);
    }

    // Prepares an EXPLAIN statement, binds the given values positionally (EXPLAIN still requires every
    // parameter bound before it can describe the program), and reads its opcode rows.
    private static List<SqlValue[]> ExplainBound(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

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
}
