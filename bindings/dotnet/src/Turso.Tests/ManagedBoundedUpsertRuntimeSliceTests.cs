using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

public sealed class ManagedBoundedUpsertRuntimeSliceTests
{
    [Test]
    public void UpsertInsertsAndDoesNothingOnPrimaryKeyConflict()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");

        ReadRows(
                connection,
                "INSERT INTO items VALUES (1, 'first') ON CONFLICT(id) DO NOTHING RETURNING id, label;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(1), SqlValue.Text("first"));
        connection.LastInsertRowId.Should().Be(1);

        using (var noOp = connection.Prepare(
                   "INSERT INTO items VALUES (1, 'ignored') ON CONFLICT(id) DO NOTHING RETURNING id;"))
        {
            noOp.Step().Should().Be(StatementStepResult.Done);
            noOp.RowsAffected.Should().Be(0);
        }

        connection.LastInsertRowId.Should().Be(1);
        ReadRows(connection, "SELECT id, label FROM items;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Text("first"));
    }

    [Test]
    public void UpsertUpdateUsesTargetAndExcludedValues()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, quantity INTEGER, label TEXT);");
        Execute(connection, "INSERT INTO items VALUES (1, 3, 'old');");

        using var statement = connection.Prepare(
            """
            INSERT INTO items VALUES (1, 7, 'new')
            ON CONFLICT(id) DO UPDATE
            SET quantity = items.quantity + excluded.quantity, label = excluded.label
            RETURNING id, quantity, label;
            """);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Integer(10));
        statement.GetValue(2).Should().Be(SqlValue.Text("new"));
        statement.Step().Should().Be(StatementStepResult.Done);
        statement.RowsAffected.Should().Be(1);
        connection.LastInsertRowId.Should().Be(1);
    }

    [Test]
    public void UpsertUniqueConflictHonorsNoCaseAndNullsRemainDistinct()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE tokens(code TEXT COLLATE NOCASE, value TEXT);");
        Execute(connection, "CREATE UNIQUE INDEX tokens_code_unique ON tokens(code);");
        Execute(connection, "INSERT INTO tokens VALUES ('alpha', 'old');");

        ReadRows(
                connection,
                """
                INSERT INTO tokens VALUES ('ALPHA', 'new')
                ON CONFLICT(code) DO UPDATE SET value = excluded.value
                RETURNING code, value;
                """)
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Text("alpha"), SqlValue.Text("new"));

        Execute(connection, "INSERT INTO tokens VALUES (NULL, 'first-null') ON CONFLICT(code) DO NOTHING;");
        Execute(connection, "INSERT INTO tokens VALUES (NULL, 'second-null') ON CONFLICT(code) DO NOTHING;");
        AssertRows(
            ReadRows(connection, "SELECT value FROM tokens WHERE code IS NULL ORDER BY value;"),
            [SqlValue.Text("first-null")],
            [SqlValue.Text("second-null")]);
    }

    [Test]
    public void UpsertUpdateRecomputesGeneratedValuesFiresUpdateTriggerAndReturnsUpdatedRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER, doubled AS (value * 2));");
        Execute(connection, "CREATE TABLE audit(event TEXT);");
        Execute(
            connection,
            "CREATE TRIGGER item_update AFTER UPDATE ON items BEGIN INSERT INTO audit VALUES ('update'); END;");
        Execute(connection, "INSERT INTO items(id, value) VALUES (1, 3);");

        ReadRows(
                connection,
                """
                INSERT INTO items(id, value) VALUES (1, 10)
                ON CONFLICT(id) DO UPDATE SET value = excluded.value
                RETURNING value, doubled;
                """)
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(10), SqlValue.Integer(20));
        AssertRows(ReadRows(connection, "SELECT event FROM audit;"), [SqlValue.Text("update")]);
    }

    [Test]
    public void UpsertConstraintFailureRollsBackTheWholeStatement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT UNIQUE, payload TEXT);");
        Execute(connection, "INSERT INTO items VALUES (1, 'one', 'original'), (2, 'two', 'other');");

        Action conflict = () => Execute(
            connection,
            """
            INSERT INTO items VALUES (1, 'two', 'changed')
            ON CONFLICT(id) DO UPDATE SET code = excluded.code, payload = excluded.payload;
            """);
        conflict.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: code");

        AssertRows(
            ReadRows(connection, "SELECT id, code, payload FROM items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("one"), SqlValue.Text("original")],
            [SqlValue.Integer(2), SqlValue.Text("two"), SqlValue.Text("other")]);
    }

    [Test]
    public void UpsertRejectsUnboundedAndAmbiguousForms()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT UNIQUE, value INTEGER);");
        Execute(connection, "INSERT INTO items VALUES (1, 'one', 1);");

        Action targetless = () => Execute(connection, "INSERT INTO items VALUES (1, 'x', 2) ON CONFLICT DO NOTHING;");
        targetless.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*requires a parenthesized PRIMARY KEY or UNIQUE conflict target*");

        Action updateWhere = () => Execute(
            connection,
            "INSERT INTO items VALUES (1, 'one', 2) ON CONFLICT(id) DO UPDATE SET value = excluded.value WHERE value > 0;");
        updateWhere.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*DO UPDATE WHERE clauses are not supported*");

        Execute(connection, "CREATE UNIQUE INDEX duplicate_code ON items(code);");
        Action ambiguous = () => Execute(
            connection,
            "INSERT INTO items VALUES (2, 'one', 2) ON CONFLICT(code) DO NOTHING;");
        ambiguous.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*matches multiple PRIMARY KEY or UNIQUE constraints*");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.ColumnCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    private static void AssertRows(IReadOnlyList<SqlValue[]> actual, params SqlValue[][] expected)
    {
        actual.Should().HaveCount(expected.Length);
        for (var index = 0; index < expected.Length; index++)
            actual[index].Should().Equal(expected[index]);
    }
}
