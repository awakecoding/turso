using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

public sealed class ManagedAttachDetachRuntimeSliceTests
{
    [Test]
    public void DirectManagedAttachRoutesSchemaQualifiedDdlDmlAndQueriesAcrossDetach()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var main = EmbeddedDatabase.OpenFile("attach-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'attach-secondary.db' AS aux;");
            var databases = ReadRows(connection, "PRAGMA database_list;");
            databases.Should().HaveCount(2);
            databases[0].Should().Equal(
                SqlValue.Integer(0),
                SqlValue.Text("main"),
                SqlValue.Text("attach-main.db"));
            databases[1].Should().Equal(
                SqlValue.Integer(2),
                SqlValue.Text("aux"),
                SqlValue.Text("attach-secondary.db"));

            Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO aux.items VALUES (1, 'persisted');");
            ReadRows(connection, "SELECT value FROM aux.items WHERE id = 1;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("persisted"));

            fileSystem.FileExists("attach-secondary.db").Should().BeTrue();
            fileSystem.FileExists("attach-secondary.db-wal").Should().BeTrue();

            Execute(connection, "DETACH DATABASE aux;");
            ReadRows(connection, "PRAGMA database_list;").Should().ContainSingle()
                .Which.Should().Equal(
                    SqlValue.Integer(0),
                    SqlValue.Text("main"),
                    SqlValue.Text("attach-main.db"));
            var detached = () => ReadRows(connection, "SELECT value FROM aux.items;");
            detached.Should().Throw<EmbeddedSqlException>().WithMessage("no such database: aux");

            Execute(connection, "ATTACH DATABASE 'attach-secondary.db' AS aux;");
            ReadRows(connection, "SELECT value FROM aux.items WHERE id = 1;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("persisted"));
            Execute(connection, "DETACH aux;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile("attach-secondary.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadRows(connection, "SELECT value FROM items WHERE id = 1;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("persisted"));
        }

        fileSystem.DeleteFile("attach-secondary.db");
        fileSystem.DeleteFile("attach-secondary.db-wal");
        fileSystem.FileExists("attach-secondary.db").Should().BeFalse();
        fileSystem.FileExists("attach-secondary.db-wal").Should().BeFalse();
    }

    [Test]
    public void DirectManagedAttachRejectsUnsafeAliasesUnknownSchemasAndCrossDatabaseQueries()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-errors-main.db", fileSystem);
        using var connection = main.Connect();

        var memory = () => Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        memory.Should().Throw<EmbeddedSqlException>().WithMessage("*memory databases*not supported*");

        var key = () => connection.Prepare("ATTACH DATABASE 'attach-errors-secondary.db' AS aux KEY 'key';");
        key.Should().Throw<EmbeddedSqlException>().WithMessage("*does not support KEY*");

        Execute(connection, "ATTACH DATABASE 'attach-errors-secondary.db' AS aux;");
        var duplicateAlias = () => Execute(connection, "ATTACH DATABASE 'unused-secondary.db' AS aux;");
        duplicateAlias.Should().Throw<EmbeddedSqlException>().WithMessage("database aux is already in use");
        fileSystem.FileExists("unused-secondary.db").Should().BeFalse();

        var duplicateFile = () => Execute(connection, "ATTACH DATABASE 'attach-errors-secondary.db' AS other;");
        duplicateFile.Should().Throw<EmbeddedSqlException>().WithMessage("database file is already attached");

        var missingDetach = () => Execute(connection, "DETACH DATABASE absent;");
        missingDetach.Should().Throw<EmbeddedSqlException>().WithMessage("no such database: absent");

        var missingSchema = () => ReadRows(connection, "SELECT * FROM absent.items;");
        missingSchema.Should().Throw<EmbeddedSqlException>().WithMessage("no such database: absent");

        Execute(connection, "CREATE TABLE main_items(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY);");
        var crossDatabase = () => ReadRows(
            connection,
            "SELECT * FROM main.main_items JOIN aux.items ON 1 = 1;");
        crossDatabase.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Cross-database queries are not supported by managed ATTACH.");
    }

    [Test]
    public void DirectManagedAttachRejectsTransactionsAndKeepsReadOnlyAttachmentsReadOnly()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var seed = EmbeddedDatabase.OpenFile("attach-readonly-main.db", fileSystem))
        using (var connection = seed.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'attach-readonly-secondary.db' AS aux;");
            Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY);");
            Execute(connection, "INSERT INTO aux.items VALUES (1);");

            var begin = () => Execute(connection, "BEGIN;");
            begin.Should().Throw<EmbeddedSqlException>().WithMessage("*transactions are not supported*attached*");

            Execute(connection, "DETACH aux;");
            Execute(connection, "BEGIN;");
            var attachInTransaction = () => Execute(
                connection,
                "ATTACH DATABASE 'attach-readonly-secondary.db' AS aux;");
            attachInTransaction.Should().Throw<EmbeddedSqlException>().WithMessage("*not supported inside a transaction*");
            Execute(connection, "ROLLBACK;");
        }

        using var readOnlyMain = EmbeddedDatabase.OpenFile("attach-readonly-main.db", fileSystem, readOnly: true);
        using var readOnlyConnection = readOnlyMain.Connect();
        Execute(readOnlyConnection, "ATTACH DATABASE 'attach-readonly-secondary.db' AS aux;");
        ReadRows(readOnlyConnection, "SELECT id FROM aux.items;").Should()
            .ContainSingle().Which.Should().Equal(SqlValue.Integer(1));

        var writeAttached = () => Execute(readOnlyConnection, "INSERT INTO aux.items VALUES (2);");
        writeAttached.Should().Throw<EmbeddedSqlException>().WithMessage("attempt to write a readonly database");
    }

    [Test]
    public void DirectManagedAttachKeepsQueryOnlyDynamicAndSharesConnectionRegistries()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("attach-runtime-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            connection.RegisterScalarFunction(
                "managed_double",
                1,
                values => SqlValue.Integer(values[0].AsInteger() * 2));
            connection.RegisterAggregateFunction(
                "managed_product",
                1,
                SqlValue.Integer(1),
                (aggregate, values) => SqlValue.Integer(aggregate.AsInteger() * values[0].AsInteger()),
                aggregate => aggregate);
            connection.RegisterCollation(
                "managed_reverse",
                (left, right) => string.CompareOrdinal(right, left));

            Execute(connection, "PRAGMA query_only = ON;");
            Execute(connection, "ATTACH DATABASE 'attach-runtime-existing.db' AS existing;");
            var blocked = () => Execute(connection, "CREATE TABLE existing.items(value INTEGER);");
            blocked.Should().Throw<EmbeddedSqlException>().WithMessage("attempt to write a readonly database");

            Execute(connection, "PRAGMA query_only = OFF;");
            Execute(connection, "CREATE TABLE existing.items(value INTEGER);");
            Execute(connection, "INSERT INTO existing.items VALUES (1), (2), (3);");
            Execute(connection, "CREATE TABLE existing.names(value TEXT);");
            Execute(connection, "INSERT INTO existing.names VALUES ('a'), ('b'), ('c');");
            AssertRows(
                ReadRows(connection, "SELECT managed_double(value) FROM existing.items ORDER BY value;"),
                [SqlValue.Integer(2)],
                [SqlValue.Integer(4)],
                [SqlValue.Integer(6)]);
            ReadRows(connection, "SELECT managed_product(value) FROM existing.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(6));
            AssertRows(
                ReadRows(connection, "SELECT value FROM existing.names ORDER BY value COLLATE managed_reverse;"),
                [SqlValue.Text("c")],
                [SqlValue.Text("b")],
                [SqlValue.Text("a")]);

            connection.RegisterScalarFunction(
                "managed_triple",
                1,
                values => SqlValue.Integer(values[0].AsInteger() * 3));
            connection.RegisterCollation(
                "managed_nocase_reverse",
                (left, right) => string.CompareOrdinal(right, left));
            AssertRows(
                ReadRows(connection, "SELECT managed_triple(value) FROM existing.items ORDER BY value;"),
                [SqlValue.Integer(3)],
                [SqlValue.Integer(6)],
                [SqlValue.Integer(9)]);
            AssertRows(
                ReadRows(connection, "SELECT value FROM existing.names ORDER BY value COLLATE managed_nocase_reverse;"),
                [SqlValue.Text("c")],
                [SqlValue.Text("b")],
                [SqlValue.Text("a")]);

            Execute(connection, "ATTACH DATABASE 'attach-runtime-future.db' AS future;");
            Execute(connection, "CREATE TABLE future.items(value INTEGER);");
            Execute(connection, "INSERT INTO future.items VALUES (1), (2);");
            Execute(connection, "CREATE TABLE future.names(value TEXT);");
            Execute(connection, "INSERT INTO future.names VALUES ('a'), ('b');");
            AssertRows(
                ReadRows(connection, "SELECT managed_double(value), managed_triple(value) FROM future.items ORDER BY value;"),
                [SqlValue.Integer(2), SqlValue.Integer(3)],
                [SqlValue.Integer(4), SqlValue.Integer(6)]);
            ReadRows(connection, "SELECT managed_product(value) FROM future.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2));
            AssertRows(
                ReadRows(connection, "SELECT value FROM future.names ORDER BY value COLLATE managed_nocase_reverse;"),
                [SqlValue.Text("b")],
                [SqlValue.Text("a")]);

            Execute(connection, "DETACH existing;");
            Execute(connection, "DETACH future;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("attach-runtime-existing.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        AssertRows(
            ReadRows(reopenedConnection, "SELECT value FROM items ORDER BY value;"),
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)],
            [SqlValue.Integer(3)]);
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
            var row = new SqlValue[statement.GetColumnCount()];
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
