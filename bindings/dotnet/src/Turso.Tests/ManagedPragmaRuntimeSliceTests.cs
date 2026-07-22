using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

public sealed class ManagedPragmaRuntimeSliceTests
{
    [Test]
    public void DirectManagedRuntimePragmasExposeSqliteMetadataShapesAndTypes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ColumnNames(connection, "PRAGMA database_list;").Should().Equal("seq", "name", "file");
        var databases = ReadRows(connection, "PRAGMA database_list;");
        databases.Should().HaveCount(1);
        databases[0].Should().Equal(SqlValue.Integer(0), SqlValue.Text("main"), SqlValue.Text(string.Empty));
        databases[0].Select(value => value.Kind).Should().Equal(
            SqlValueKind.Integer,
            SqlValueKind.Text,
            SqlValueKind.Text);

        ColumnNames(connection, "PRAGMA encoding;").Should().Equal("encoding");
        ReadRows(connection, "PRAGMA encoding;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("UTF-8"));

        ColumnNames(connection, "PRAGMA query_only;").Should().Equal("query_only");
        ReadRows(connection, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));
    }

    [Test]
    public void CatalogPragmasExposeGeneratedColumnsDefaultsAndPersistedCatalog()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "CREATE TABLE widgets(id INTEGER PRIMARY KEY, label TEXT NOT NULL DEFAULT 'ready', generated_label TEXT GENERATED ALWAYS AS (label || '!') VIRTUAL);");
        Execute(connection, "CREATE VIEW widget_labels AS SELECT label FROM widgets;");

        ColumnNames(connection, "PRAGMA table_info(widgets);").Should()
            .Equal("cid", "name", "type", "notnull", "dflt_value", "pk");
        var tableInfo = ReadRows(connection, "PRAGMA table_info(widgets);");
        tableInfo.Should().HaveCount(2);
        tableInfo[1].Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Text("label"),
            SqlValue.Text("TEXT"),
            SqlValue.Integer(1),
            SqlValue.Text("'ready'"),
            SqlValue.Integer(0));

        ColumnNames(connection, "PRAGMA table_xinfo(widgets);").Should()
            .Equal("cid", "name", "type", "notnull", "dflt_value", "pk", "hidden");
        var tableXInfo = ReadRows(connection, "PRAGMA table_xinfo(widgets);");
        tableXInfo.Should().HaveCount(3);
        tableXInfo[2].Should().Equal(
            SqlValue.Integer(2),
            SqlValue.Text("generated_label"),
            SqlValue.Text("TEXT"),
            SqlValue.Integer(0),
            SqlValue.Null,
            SqlValue.Integer(0),
            SqlValue.Integer(2));

        ColumnNames(connection, "PRAGMA table_list;").Should()
            .Equal("schema", "name", "type", "ncol", "wr", "strict");
        var tableList = ReadRows(connection, "PRAGMA table_list;");
        FindCatalogEntry(tableList, "sqlite_schema").Should().Equal(
            SqlValue.Text("main"),
            SqlValue.Text("sqlite_schema"),
            SqlValue.Text("table"),
            SqlValue.Integer(5),
            SqlValue.Integer(0),
            SqlValue.Integer(0));
        FindCatalogEntry(tableList, "widgets").Should().Equal(
            SqlValue.Text("main"),
            SqlValue.Text("widgets"),
            SqlValue.Text("table"),
            SqlValue.Integer(3),
            SqlValue.Integer(0),
            SqlValue.Integer(0));
        FindCatalogEntry(tableList, "widget_labels").Should().Equal(
            SqlValue.Text("main"),
            SqlValue.Text("widget_labels"),
            SqlValue.Text("view"),
            SqlValue.Integer(1),
            SqlValue.Integer(0),
            SqlValue.Integer(0));

        var fileSystem = new InMemoryFileSystem();
        using (var fileDatabase = EmbeddedDatabase.OpenFile("pragma-catalog.db", fileSystem))
        using (var fileConnection = fileDatabase.Connect())
        {
            Execute(fileConnection, "CREATE TABLE persisted(id INTEGER PRIMARY KEY, value TEXT);");
        }

        using var reopenedDatabase = EmbeddedDatabase.OpenFile("pragma-catalog.db", fileSystem);
        using var reopenedConnection = reopenedDatabase.Connect();
        FindCatalogEntry(ReadRows(reopenedConnection, "PRAGMA table_list;"), "persisted")[3]
            .Should().Be(SqlValue.Integer(2));
        ReadRows(reopenedConnection, "PRAGMA database_list;")[0][2].Should()
            .Be(SqlValue.Text("pragma-catalog.db"));
    }

    [Test]
    public void QueryOnlyIsConnectionLocalBlocksWritesAndIsNotTransactionState()
    {
        using var database = new EmbeddedDatabase();
        using var primary = database.Connect();
        using var sibling = database.Connect();

        using (var setter = primary.Prepare("PRAGMA query_only = ON;"))
        {
            setter.GetColumnCount().Should().Be(0);
            setter.Step().Should().Be(StatementStepResult.Done);
        }

        ReadRows(primary, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
        ReadRows(sibling, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));

        var write = () => Execute(primary, "CREATE TABLE rejected(value INTEGER);");
        write.Should().Throw<EmbeddedSqlException>().WithMessage("attempt to write a readonly database");
        ReadRows(primary, "PRAGMA table_list;").Should()
            .NotContain(row => row[1].AsText() == "rejected");

        Execute(primary, "BEGIN;");
        Execute(primary, "PRAGMA query_only = OFF;");
        Execute(primary, "ROLLBACK;");
        ReadRows(primary, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));

        Execute(primary, "SAVEPOINT pragma_state;");
        Execute(primary, "PRAGMA query_only = ON;");
        Execute(primary, "ROLLBACK TO pragma_state;");
        ReadRows(primary, "PRAGMA query_only;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
        Execute(primary, "RELEASE pragma_state;");
        Execute(primary, "PRAGMA query_only = OFF;");
        Execute(primary, "CREATE TABLE accepted(value INTEGER);");
        FindCatalogEntry(ReadRows(primary, "PRAGMA table_list;"), "accepted")[2]
            .Should().Be(SqlValue.Text("table"));
    }

    [Test]
    public void UnsupportedPragmasAreRejectedByTheManagedParser()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var unsupported = () => connection.Prepare("PRAGMA journal_mode = WAL;");
        unsupported.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Unsupported PRAGMA journal_mode. At SQL offset *");

        var unsupportedSchema = () => connection.Prepare("PRAGMA temp.table_list;");
        unsupportedSchema.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Unsupported PRAGMA database temp. At SQL offset *");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var columns = new string[statement.GetColumnCount()];
        for (var index = 0; index < columns.Length; index++)
            columns[index] = statement.GetColumnName(index);

        return columns;
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

    private static SqlValue[] FindCatalogEntry(IEnumerable<SqlValue[]> rows, string name)
        => rows.Single(row => row[1].AsText() == name);
}
