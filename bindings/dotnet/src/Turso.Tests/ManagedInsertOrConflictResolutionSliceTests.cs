using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

[NonParallelizable]
public sealed class ManagedInsertOrConflictResolutionSliceTests
{
    [Test]
    public void IgnoreSkipsUniqueAndPrimaryKeyConflictsAcrossValuesAndSelectSources()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(connection, "CREATE TABLE source_items(id INTEGER, code TEXT);");
        Execute(connection, "INSERT INTO items VALUES (1, 'one');");

        using (var statement = connection.Prepare(
                   """
                   INSERT OR IGNORE INTO items VALUES
                       (1, 'duplicate-primary-key'),
                       (2, 'two'),
                       (3, 'one')
                   RETURNING id, code;
                   """))
        {
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(2));
            statement.GetValue(1).Should().Be(SqlValue.Text("two"));
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(1);
        }

        connection.LastInsertRowId.Should().Be(2);
        Execute(connection, "INSERT INTO source_items VALUES (2, 'duplicate-primary-key'), (3, 'three'), (4, 'one');");
        Execute(connection, "INSERT OR IGNORE INTO items SELECT id, code FROM source_items;");

        connection.LastInsertRowId.Should().Be(3);
        AssertRows(
            ReadRows(connection, "SELECT id, code FROM items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("one")],
            [SqlValue.Integer(2), SqlValue.Text("two")],
            [SqlValue.Integer(3), SqlValue.Text("three")]);
    }

    [Test]
    public void ReplaceDeletesEveryUniqueOrPrimaryKeyConflictAndReturnsInsertedRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(connection, "INSERT INTO items VALUES (1, 'one'), (2, 'two');");

        using (var statement = connection.Prepare(
                   """
                   INSERT OR REPLACE INTO items VALUES
                       (1, 'two')
                   RETURNING id, code;
                   """))
        {
            var rows = ReadRows(statement);
            AssertRows(
                rows,
                [SqlValue.Integer(1), SqlValue.Text("two")]);
            statement.RowsAffected.Should().Be(1);
        }

        connection.LastInsertRowId.Should().Be(1);
        AssertRows(
            ReadRows(connection, "SELECT id, code FROM items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("two")]);
    }

    [Test]
    public void ConflictAlgorithmsHonorExplicitUniqueIndexesAndGeneratedIndexValues()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value INTEGER, doubled AS (value * 2));");
        Execute(connection, "CREATE UNIQUE INDEX items_doubled ON items(doubled);");
        Execute(connection, "INSERT INTO items VALUES (1, 3);");

        Execute(connection, "INSERT OR IGNORE INTO items VALUES (2, 3);");
        connection.LastInsertRowId.Should().Be(1);
        AssertRows(
            ReadRows(connection, "SELECT id, value, doubled FROM items;"),
            [SqlValue.Integer(1), SqlValue.Integer(3), SqlValue.Integer(6)]);

        using (var statement = connection.Prepare("INSERT OR REPLACE INTO items VALUES (2, 3) RETURNING id, value, doubled;"))
        {
            AssertRows(
                ReadRows(statement),
                [SqlValue.Integer(2), SqlValue.Integer(3), SqlValue.Integer(6)]);
            statement.RowsAffected.Should().Be(1);
        }

        connection.LastInsertRowId.Should().Be(2);
        AssertRows(
            ReadRows(connection, "SELECT id, value, doubled FROM items;"),
            [SqlValue.Integer(2), SqlValue.Integer(3), SqlValue.Integer(6)]);
    }

    [Test]
    public void AbortRollsBackOnlyTheCurrentStatementAndKeepsTheTransactionActive()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO items VALUES (1);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO items VALUES (2);");

        Action conflict = () => Execute(connection, "INSERT OR ABORT INTO items VALUES (3), (1);");
        conflict.Should().Throw<EmbeddedSqlException>()
            .WithMessage("UNIQUE constraint failed: items.id");

        AssertRows(
            ReadRows(connection, "SELECT id FROM items ORDER BY id;"),
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)]);
        Execute(connection, "COMMIT;");
        AssertRows(
            ReadRows(connection, "SELECT id FROM items ORDER BY id;"),
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)]);
    }

    [Test]
    public void FailPreservesPriorRowsButDoesNotAdvanceLastInsertRowId()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO items VALUES (1);");
        connection.LastInsertRowId.Should().Be(1);

        Action conflict = () => Execute(connection, "INSERT OR FAIL INTO items VALUES (2), (1), (3);");
        conflict.Should().Throw<EmbeddedSqlException>()
            .WithMessage("UNIQUE constraint failed: items.id");

        connection.LastInsertRowId.Should().Be(1);
        AssertRows(
            ReadRows(connection, "SELECT id FROM items ORDER BY id;"),
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)]);
    }

    [Test]
    public void RollbackDropsTheEntireTransactionAndItsSavepoints()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO items VALUES (1);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO items VALUES (2);");
        Execute(connection, "SAVEPOINT pending;");
        Execute(connection, "INSERT INTO items VALUES (3);");

        Action conflict = () => Execute(connection, "INSERT OR ROLLBACK INTO items VALUES (1);");
        conflict.Should().Throw<EmbeddedSqlException>()
            .WithMessage("UNIQUE constraint failed: items.id");

        connection.LastInsertRowId.Should().Be(3);
        AssertRows(ReadRows(connection, "SELECT id FROM items ORDER BY id;"), [SqlValue.Integer(1)]);
        Action commit = () => Execute(connection, "COMMIT;");
        commit.Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot commit - no transaction is active");
        Action rollbackTo = () => Execute(connection, "ROLLBACK TO pending;");
        rollbackTo.Should().Throw<EmbeddedSqlException>()
            .WithMessage("no such savepoint: pending");
    }

    [Test]
    public void UnsupportedConstraintAndTriggerFormsAreRejectedBeforeWriting()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE parent_items(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE child_items(parent_id INTEGER REFERENCES parent_items(id));");
        Execute(connection, "PRAGMA foreign_keys = ON;");

        Action foreignKey = () => Execute(connection, "INSERT OR IGNORE INTO child_items VALUES (1);");
        foreignKey.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*does not support tables with FOREIGN KEY constraints*");
        ReadRows(connection, "SELECT parent_id FROM child_items;").Should().BeEmpty();

        Execute(connection, "CREATE TABLE checked_items(id INTEGER UNIQUE, value INTEGER CHECK (value > 0));");
        Action check = () => Execute(connection, "INSERT OR IGNORE INTO checked_items VALUES (1, 1);");
        check.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*does not support CHECK constraints*");
        ReadRows(connection, "SELECT id FROM checked_items;").Should().BeEmpty();

        Execute(connection, "CREATE TABLE triggered_items(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE audit_items(value INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER triggered_items_insert AFTER INSERT ON triggered_items BEGIN INSERT INTO audit_items VALUES (1); END;");
        Action trigger = () => Execute(connection, "INSERT OR ABORT INTO triggered_items VALUES (1);");
        trigger.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*does not support target tables with INSERT triggers*");
        ReadRows(connection, "SELECT id FROM triggered_items;").Should().BeEmpty();
        ReadRows(connection, "SELECT value FROM audit_items;").Should().BeEmpty();

        Execute(connection, "CREATE TABLE required_items(id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
        Action replaceNotNull = () => Execute(connection, "INSERT OR REPLACE INTO required_items VALUES (1, 'value');");
        replaceNotNull.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*does not support NOT NULL constraints*");
        ReadRows(connection, "SELECT id FROM required_items;").Should().BeEmpty();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return ReadRows(statement);
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedStatement statement)
    {
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
        actual.Count.Should().Be(expected.Length);
        for (var rowIndex = 0; rowIndex < expected.Length; rowIndex++)
        {
            actual[rowIndex].Length.Should().Be(expected[rowIndex].Length);
            for (var columnIndex = 0; columnIndex < expected[rowIndex].Length; columnIndex++)
                actual[rowIndex][columnIndex].Should().Be(expected[rowIndex][columnIndex]);
        }
    }
}
