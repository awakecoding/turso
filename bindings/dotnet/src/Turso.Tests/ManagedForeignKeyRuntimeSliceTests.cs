using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

public sealed class ManagedForeignKeyRuntimeSliceTests
{
    [Test]
    public void ImmediateForeignKeysEnforceColumnAndTableReferencesWithNullAndStatementAtomicity()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id), parent_code TEXT, FOREIGN KEY(parent_code) REFERENCES parent(code));");
        Execute(connection, "INSERT INTO parent VALUES (1, 'one');");
        Execute(connection, "INSERT INTO child VALUES (1, 1, 'one'), (2, NULL, NULL), (3, 1, NULL);");

        Action invalidInsert = () => Execute(
            connection,
            "INSERT INTO child VALUES (4, 1, 'one'), (5, 999, 'one');");
        invalidInsert.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        Count(connection, "child").Should().Be(3);

        Action invalidChildUpdate = () => Execute(connection, "UPDATE child SET parent_id = 999 WHERE id = 1;");
        invalidChildUpdate.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        Value(connection, "SELECT parent_id FROM child WHERE id = 1;").Should().Be(SqlValue.Integer(1));

        Action invalidParentUpdate = () => Execute(connection, "UPDATE parent SET id = 2 WHERE id = 1;");
        invalidParentUpdate.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        Value(connection, "SELECT id FROM parent;").Should().Be(SqlValue.Integer(1));

        Action invalidParentDelete = () => Execute(connection, "DELETE FROM parent WHERE id = 1;");
        invalidParentDelete.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        Count(connection, "parent").Should().Be(1);
    }

    [Test]
    public void ForeignKeysUseSingleColumnUniqueIndexesAndParentAffinity()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(code TEXT);");
        Execute(connection, "CREATE UNIQUE INDEX parent_code ON parent(code);");
        Execute(connection, "CREATE TABLE child(code INTEGER REFERENCES parent(code));");
        Execute(connection, "INSERT INTO parent VALUES ('1');");

        Execute(connection, "INSERT INTO child VALUES (1);");
        Action invalidChild = () => Execute(connection, "INSERT INTO child VALUES (2);");
        invalidChild.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        Count(connection, "child").Should().Be(1);
    }

    [Test]
    public void ForeignKeyDefinitionsPersistInTheManagedFileCatalog()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("foreign-key-persistence.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");
            Execute(connection, "INSERT INTO parent VALUES (1);");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile("foreign-key-persistence.db", fileSystem);
        using var reopenedConnection = reopenedDatabase.Connect();
        Execute(reopenedConnection, "PRAGMA foreign_keys = ON;");
        Execute(reopenedConnection, "INSERT INTO child VALUES (1);");
        Action invalidChild = () => Execute(reopenedConnection, "INSERT INTO child VALUES (2);");
        invalidChild.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
    }

    [Test]
    public void ForeignKeysPragmaIsConnectionLocalAndCannotChangeWithinTransactionsOrSavepoints()
    {
        using var database = new EmbeddedDatabase();
        using var primary = database.Connect();
        using var sibling = database.Connect();
        Execute(primary, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(primary, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");

        PragmaBoolean(primary, "foreign_keys").Should().BeFalse();
        PragmaBoolean(sibling, "foreign_keys").Should().BeFalse();
        Execute(primary, "INSERT INTO child VALUES (99);");

        Execute(primary, "PRAGMA foreign_keys = ON;");
        PragmaBoolean(primary, "foreign_keys").Should().BeTrue();
        PragmaBoolean(sibling, "foreign_keys").Should().BeFalse();
        Action enforced = () => Execute(primary, "INSERT INTO child VALUES (100);");
        enforced.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

        Execute(primary, "BEGIN;");
        Execute(primary, "PRAGMA foreign_keys = OFF;");
        PragmaBoolean(primary, "foreign_keys").Should().BeTrue();
        Execute(primary, "SAVEPOINT foreign_key_setting;");
        Execute(primary, "PRAGMA foreign_keys = OFF;");
        PragmaBoolean(primary, "foreign_keys").Should().BeTrue();
        Execute(primary, "ROLLBACK TO foreign_key_setting;");
        Execute(primary, "RELEASE foreign_key_setting;");
        Execute(primary, "ROLLBACK;");

        Execute(primary, "PRAGMA foreign_keys = OFF;");
        PragmaBoolean(primary, "foreign_keys").Should().BeFalse();
        Execute(primary, "INSERT INTO child VALUES (102);");
        Execute(primary, "PRAGMA foreign_keys = ON;");
        PragmaBoolean(primary, "foreign_keys").Should().BeTrue();

        Execute(sibling, "INSERT INTO child VALUES (101);");
        Count(primary, "child").Should().Be(3);
    }

    [Test]
    public void ImmediateForeignKeyFailuresLeaveTransactionsAndSavepointsUsable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");
        Execute(connection, "INSERT INTO parent VALUES (1);");

        Execute(connection, "BEGIN;");
        Action invalidChild = () => Execute(connection, "INSERT INTO child VALUES (2);");
        invalidChild.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        Execute(connection, "INSERT INTO child VALUES (1);");
        Execute(connection, "SAVEPOINT undo_child;");
        Execute(connection, "INSERT INTO child VALUES (1);");
        Execute(connection, "ROLLBACK TO undo_child;");
        Execute(connection, "RELEASE undo_child;");
        Execute(connection, "COMMIT;");
        Count(connection, "child").Should().Be(1);

        Execute(connection, "BEGIN;");
        Action invalidDelete = () => Execute(connection, "DELETE FROM parent WHERE id = 1;");
        invalidDelete.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        Execute(connection, "ROLLBACK;");
        Count(connection, "parent").Should().Be(1);
    }

    [Test]
    public void UnsupportedForeignKeyDefinitionsAreRejectedBeforeCatalogMutation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Action missingParentColumn = () => connection.Prepare(
            "CREATE TABLE missing_parent_column(child INTEGER REFERENCES parent);");
        missingParentColumn.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Foreign key references must name exactly one parent column. At SQL offset *");
        Action composite = () => connection.Prepare(
            "CREATE TABLE composite(a INTEGER, b INTEGER, FOREIGN KEY(a, b) REFERENCES parent(id, code));");
        composite.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Composite foreign key constraints are not supported. At SQL offset *");
        Action actions = () => connection.Prepare(
            "CREATE TABLE actions(child INTEGER REFERENCES parent(id) ON DELETE CASCADE);");
        actions.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Foreign key actions, MATCH, and deferral are not supported. At SQL offset *");
        Action qualified = () => connection.Prepare(
            "CREATE TABLE qualified(child INTEGER REFERENCES main.parent(id));");
        qualified.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Schema-qualified foreign keys are not supported. At SQL offset *");

        Count(connection, "sqlite_schema WHERE name = 'missing_parent_column'").Should().Be(0);

        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE non_unique_parent(value INTEGER);");
        Execute(connection, "CREATE TABLE mismatched_child(value INTEGER REFERENCES non_unique_parent(value));");
        Action unsupportedParentKey = () => Execute(connection, "INSERT INTO mismatched_child VALUES (1);");
        unsupportedParentKey.Should().Throw<EmbeddedSqlException>()
            .WithMessage("foreign key mismatch - \"mismatched_child\" referencing \"non_unique_parent\"");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static bool PragmaBoolean(EmbeddedConnection connection, string pragma)
        => Value(connection, $"PRAGMA {pragma};") == SqlValue.Integer(1);

    private static int Count(EmbeddedConnection connection, string source)
        => checked((int)Value(connection, $"SELECT COUNT(*) FROM {source};").AsInteger());

    private static SqlValue Value(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }
}
