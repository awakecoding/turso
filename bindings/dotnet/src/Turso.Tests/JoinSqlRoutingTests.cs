using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

// Proves that EmbeddedDatabase routes the supported two-table INNER/LEFT OUTER join subset
// through the real nested-loop opcode family
// (OpenReadCursor/Rewind/Column/FilterRegisters/JumpIf/ResultRow/Next/CloseCursor/Halt) and
// that the routed rows stay byte-identical to the tree-walking evaluator. As in the aggregate
// and sorted routing suites, EXPLAIN is the ground truth for "was this lowered to bytecode?":
// a routed statement dumps the join opcodes, while every deliberate fallback shape throws
// because EXPLAIN only describes lowered programs. Fallback tests also assert the evaluator
// still produces the correct rows.
public class JoinSqlRoutingTests
{
    [Test]
    public void InnerJoinOnEqualityMatchesEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.id, u.name, o.amount FROM users u INNER JOIN orders o ON u.id = o.user_id;");

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("ada"), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Text("ada"), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Text("bo"), SqlValue.Integer(30));
    }

    [Test]
    public void InnerJoinStarExpandsLeftThenRightColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT * FROM users u JOIN orders o ON u.id = o.user_id;");

        ColumnNames(connection, "SELECT * FROM users u JOIN orders o ON u.id = o.user_id;")
            .Should().Equal("id", "name", "id", "user_id", "amount");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(
            SqlValue.Integer(1), SqlValue.Text("ada"), SqlValue.Integer(100), SqlValue.Integer(1), SqlValue.Integer(10));
        rows[2].Should().Equal(
            SqlValue.Integer(2), SqlValue.Text("bo"), SqlValue.Integer(102), SqlValue.Integer(2), SqlValue.Integer(30));
    }

    [Test]
    public void QualifiedStarProjectsOnlyThatSourcesColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        ColumnNames(connection, "SELECT o.* FROM users u JOIN orders o ON u.id = o.user_id;")
            .Should().Equal("id", "user_id", "amount");

        var rows = ReadRows(connection, "SELECT o.* FROM users u JOIN orders o ON u.id = o.user_id;");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(100), SqlValue.Integer(1), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(101), SqlValue.Integer(1), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Integer(102), SqlValue.Integer(2), SqlValue.Integer(30));
    }

    [Test]
    public void AliasedAndConstantProjectionsRouteThroughTheJoin()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        ColumnNames(
            connection,
            "SELECT u.name AS who, o.amount AS cost FROM users u JOIN orders o ON u.id = o.user_id;")
            .Should().Equal("who", "cost");

        var rows = ReadRows(
            connection,
            "SELECT u.name AS who, o.amount AS cost, 7 FROM users u JOIN orders o ON u.id = o.user_id;");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(10), SqlValue.Integer(7));

        // The whole statement (including the folded constant) went through the join program.
        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.name AS who, o.amount AS cost, 7 FROM users u JOIN orders o ON u.id = o.user_id;"))
            .Should().Contain("FilterRegisters").And.Contain("LoadConstant");
    }

    [Test]
    public void InnerJoinWhereFoldsIntoThePerPairPredicate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount > 15;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(20));
        rows[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(30));

        // A single FilterRegisters gates both the ON condition and the folded WHERE.
        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT u.name, o.amount FROM users u JOIN orders o ON u.id = o.user_id WHERE o.amount > 15;"))
            .Count(opcode => opcode == "FilterRegisters").Should().Be(1);
    }

    [Test]
    public void CommaJoinProducesTheCrossProduct()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(y INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1), (2);");
        Execute(connection, "INSERT INTO b VALUES (10), (20);");

        var rows = ReadRows(connection, "SELECT a.x, b.y FROM a, b;");

        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(10));
        rows[3].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(20));
    }

    [Test]
    public void CrossJoinWithoutPredicateEmitsNoFilterRegisters()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(y INTEGER);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT a.x, b.y FROM a CROSS JOIN b;")).ToList();

        opcodes.Should().Contain("OpenReadCursor")
            .And.Contain("Rewind")
            .And.Contain("Column")
            .And.Contain("ResultRow")
            .And.Contain("Next")
            .And.Contain("CloseCursor")
            .And.Contain("Halt");
        opcodes.Should().NotContain("FilterRegisters");
        opcodes.Should().NotContain("JumpIf");
    }

    [Test]
    public void NullJoinKeysNeverMatchInInnerJoin()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(k INTEGER, tag TEXT);");
        Execute(connection, "CREATE TABLE r(k INTEGER, tag TEXT);");
        Execute(connection, "INSERT INTO l VALUES (1, 'l1'), (NULL, 'lnull');");
        Execute(connection, "INSERT INTO r VALUES (1, 'r1'), (NULL, 'rnull');");

        var rows = ReadRows(connection, "SELECT l.tag, r.tag FROM l JOIN r ON l.k = r.k;");

        // NULL = NULL is not true, so only the (1,1) pair survives.
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Text("l1"), SqlValue.Text("r1"));
    }

    [Test]
    public void NocaseCollationInOnConditionRoutesAndMatchesEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(name TEXT);");
        Execute(connection, "CREATE TABLE r(name TEXT);");
        Execute(connection, "INSERT INTO l VALUES ('Ada'), ('Bo');");
        Execute(connection, "INSERT INTO r VALUES ('ADA'), ('zoe');");

        var rows = ReadRows(
            connection,
            "SELECT l.name, r.name FROM l JOIN r ON l.name = r.name COLLATE NOCASE;");

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Text("Ada"), SqlValue.Text("ADA"));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT l.name, r.name FROM l JOIN r ON l.name = r.name COLLATE NOCASE;"))
            .Should().Contain("FilterRegisters");
    }

    [Test]
    public void LeftOuterJoinNullExtendsUnmatchedLeftRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        // A third user with no orders exercises the null-extension branch.
        Execute(connection, "INSERT INTO users VALUES (3, 'cy');");

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;");

        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(20));
        rows[2].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(30));
        rows[3].Should().Equal(SqlValue.Text("cy"), SqlValue.Null);
    }

    [Test]
    public void LeftOuterJoinNullExtendsEveryRowWhenRightIsEmpty()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE orders(id INTEGER, user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO users VALUES (1, 'ada'), (2, 'bo');");

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Null);
        rows[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Null);
    }

    [Test]
    public void LeftOuterJoinWithEmptyLeftYieldsNoRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE orders(id INTEGER, user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO orders VALUES (100, 1, 10);");

        ReadRows(connection, "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;")
            .Should().BeEmpty();
    }

    [Test]
    public void InnerJoinExplainEmitsTheNestedLoopProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        // Single-column tables keep the combined-column reads to one per side so the exact
        // opcode sequence stays readable.
        Execute(connection, "CREATE TABLE p(a INTEGER);");
        Execute(connection, "CREATE TABLE q(b INTEGER);");

        var opcodes = Opcodes(ReadRows(
            connection,
            "EXPLAIN SELECT p.a, q.b FROM p JOIN q ON p.a = q.b;")).ToList();

        opcodes.Should().Equal(
            "OpenReadCursor",
            "OpenReadCursor",
            "Rewind",
            "Rewind",
            "Column",
            "Column",
            "FilterRegisters",
            "Copy",
            "Copy",
            "ResultRow",
            "Next",
            "Next",
            "CloseCursor",
            "CloseCursor",
            "Halt");
    }

    [Test]
    public void LeftOuterJoinExplainEmitsMatchFlagAndJumpIf()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var opcodes = Opcodes(ReadRows(
            connection,
            "EXPLAIN SELECT u.id, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;")).ToList();

        opcodes.Should().Contain("FilterRegisters")
            .And.Contain("JumpIf")
            .And.Contain("LoadConstant")
            .And.Contain("ResultRow");

        // The LEFT OUTER shape projects two result rows: the matched pair and the null-extension.
        opcodes.Count(opcode => opcode == "ResultRow").Should().Be(2);

        Comments(ReadRows(
                connection,
                "EXPLAIN SELECT u.id, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id;"))
            .Should().Contain(comment => comment.StartsWith("goto ") && comment.Contains(" if r["));
    }

    [Test]
    public void JoinStatementResetReplayReflectsAppendedRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        using var statement = connection.Prepare(
            "SELECT u.name, o.amount FROM users u JOIN orders o ON u.id = o.user_id;");

        DrainPairs(statement).Should().Equal(
            (SqlValue.Text("ada"), SqlValue.Integer(10)),
            (SqlValue.Text("ada"), SqlValue.Integer(20)),
            (SqlValue.Text("bo"), SqlValue.Integer(30)));

        Execute(connection, "INSERT INTO orders VALUES (103, 2, 40);");

        statement.Reset();
        DrainPairs(statement).Should().Equal(
            (SqlValue.Text("ada"), SqlValue.Integer(10)),
            (SqlValue.Text("ada"), SqlValue.Integer(20)),
            (SqlValue.Text("bo"), SqlValue.Integer(30)),
            (SqlValue.Text("bo"), SqlValue.Integer(40)));
    }

    [Test]
    public void RightJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        Execute(connection, "INSERT INTO orders VALUES (200, 9, 99);");

        // RIGHT joins are evaluator-only; the orphan order surfaces with NULL user columns.
        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u RIGHT JOIN orders o ON u.id = o.user_id;");
        rows.Should().HaveCount(4);
        rows[3].Should().Equal(SqlValue.Null, SqlValue.Integer(99));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT u.name, o.amount FROM users u RIGHT JOIN orders o ON u.id = o.user_id;"));
    }

    [Test]
    public void FullJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u FULL JOIN orders o ON u.id = o.user_id;");
        rows.Should().HaveCount(3);

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT u.name, o.amount FROM users u FULL JOIN orders o ON u.id = o.user_id;"));
    }

    [Test]
    public void UsingJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER, tag TEXT);");
        Execute(connection, "CREATE TABLE r(id INTEGER, note TEXT);");
        Execute(connection, "INSERT INTO l VALUES (1, 'a'), (2, 'b');");
        Execute(connection, "INSERT INTO r VALUES (1, 'x'), (3, 'y');");

        // USING coalesces the join column into a single output, which the raw-concatenating
        // builder cannot reproduce, so the evaluator keeps ownership.
        var rows = ReadRows(connection, "SELECT id, tag, note FROM l JOIN r USING (id);");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"), SqlValue.Text("x"));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT id, tag, note FROM l JOIN r USING (id);"));
    }

    [Test]
    public void NaturalJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER, tag TEXT);");
        Execute(connection, "CREATE TABLE r(id INTEGER, note TEXT);");
        Execute(connection, "INSERT INTO l VALUES (1, 'a'), (2, 'b');");
        Execute(connection, "INSERT INTO r VALUES (2, 'x'), (3, 'y');");

        var rows = ReadRows(connection, "SELECT id, tag, note FROM l NATURAL JOIN r;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Text("b"), SqlValue.Text("x"));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT id, tag, note FROM l NATURAL JOIN r;"));
    }

    [Test]
    public void ThreeTableJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER, av TEXT);");
        Execute(connection, "CREATE TABLE b(id INTEGER, bv TEXT);");
        Execute(connection, "CREATE TABLE c(id INTEGER, cv TEXT);");
        Execute(connection, "INSERT INTO a VALUES (1, 'a1');");
        Execute(connection, "INSERT INTO b VALUES (1, 'b1');");
        Execute(connection, "INSERT INTO c VALUES (1, 'c1');");

        var rows = ReadRows(
            connection,
            "SELECT a.av, b.bv, c.cv FROM a JOIN b ON a.id = b.id JOIN c ON b.id = c.id;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Text("a1"), SqlValue.Text("b1"), SqlValue.Text("c1"));

        // The outer join's left side is itself a join, so the two-table route declines.
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT a.av, b.bv, c.cv FROM a JOIN b ON a.id = b.id JOIN c ON b.id = c.id;"));
    }

    [Test]
    public void GroupByOverJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT u.name, count(*) FROM users u JOIN orders o ON u.id = o.user_id GROUP BY u.name;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(2));
        rows[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(1));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT u.name, count(*) FROM users u JOIN orders o ON u.id = o.user_id GROUP BY u.name;"));
    }

    [Test]
    public void OrderByOverJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT o.amount FROM users u JOIN orders o ON u.id = o.user_id ORDER BY o.amount DESC;");
        rows.Select(row => row[0])
            .Should().Equal(SqlValue.Integer(30), SqlValue.Integer(20), SqlValue.Integer(10));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT o.amount FROM users u JOIN orders o ON u.id = o.user_id ORDER BY o.amount DESC;"));
    }

    [Test]
    public void DistinctOverJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT DISTINCT u.name FROM users u JOIN orders o ON u.id = o.user_id;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Text("ada"), SqlValue.Text("bo"));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT DISTINCT u.name FROM users u JOIN orders o ON u.id = o.user_id;"));
    }

    [Test]
    public void LimitOverJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        var rows = ReadRows(
            connection,
            "SELECT o.amount FROM users u JOIN orders o ON u.id = o.user_id LIMIT 2;");
        rows.Should().HaveCount(2);

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT o.amount FROM users u JOIN orders o ON u.id = o.user_id LIMIT 2;"));
    }

    [Test]
    public void LeftOuterJoinWithWhereFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);
        Execute(connection, "INSERT INTO users VALUES (3, 'cy');");

        // A LEFT join WHERE is a post-join filter over null-extended rows the nested loop cannot
        // express, so it stays on the evaluator (which drops the null-extended "cy" row here).
        var rows = ReadRows(
            connection,
            "SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount > 15;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(20));
        rows[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(30));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT u.name, o.amount FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE o.amount > 15;"));
    }

    [Test]
    public void ComputedExpressionProjectionFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        // A computed projection is neither a column read nor a folded constant, so the builder
        // declines and the evaluator computes it.
        var rows = ReadRows(
            connection,
            "SELECT o.amount + 1 FROM users u JOIN orders o ON u.id = o.user_id;");
        rows.Select(row => row[0])
            .Should().Equal(SqlValue.Integer(11), SqlValue.Integer(21), SqlValue.Integer(31));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT o.amount + 1 FROM users u JOIN orders o ON u.id = o.user_id;"));
    }

    [Test]
    public void MissingQualifiedStarTableStillRaisesEvaluatorError()
    {
        using var connection = new EmbeddedDatabase().Connect();
        SeedOrders(connection);

        // A qualifier that names neither side declines so the evaluator raises its exact error.
        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT z.* FROM users u JOIN orders o ON u.id = o.user_id;"))!;
        error.Message.Should().Be("no such table: z");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT z.* FROM users u JOIN orders o ON u.id = o.user_id;"));
    }

    private static void SeedOrders(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE orders(id INTEGER, user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO users VALUES (1, 'ada'), (2, 'bo');");
        Execute(connection, "INSERT INTO orders VALUES (100, 1, 10), (101, 1, 20), (102, 2, 30);");
    }

    private static List<(SqlValue, SqlValue)> DrainPairs(EmbeddedStatement statement)
    {
        var rows = new List<(SqlValue, SqlValue)>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add((statement.GetValue(0), statement.GetValue(1)));

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
