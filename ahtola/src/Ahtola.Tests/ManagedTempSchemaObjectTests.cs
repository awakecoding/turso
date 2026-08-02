using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// TEMP views and triggers live in a connection-private schema. These tests pin the boundary that
/// makes that safe: another connection must never observe them, and they must never reach the
/// persistent schema, because a leaked temp object would be a durable change to someone else's
/// database.
/// </summary>
public sealed class ManagedTempSchemaObjectTests
{
    [Test]
    public void TempViewAndTriggerAreInvisibleToAnotherConnection()
    {
        using var database = new EmbeddedDatabase();
        using var owner = database.Connect();
        using var observer = database.Connect();
        Execute(owner, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)");
        Execute(owner, "CREATE TABLE log(value TEXT)");
        Execute(owner, "CREATE TEMP VIEW private_view AS SELECT 1 AS x");
        Execute(
            owner,
            "CREATE TEMP TRIGGER private_trigger AFTER INSERT ON data "
                + "BEGIN INSERT INTO log VALUES ('T:' || NEW.id); END");

        ReadRows(owner, "SELECT x FROM private_view").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
        Assert.Throws<EmbeddedSqlException>(() => Execute(observer, "SELECT x FROM private_view"))!
            .Message.Should().Be("no such table: private_view");

        // The owning connection's trigger must not fire for the other connection's writes.
        Execute(observer, "INSERT INTO data VALUES (1, 'one')");
        ReadRows(owner, "SELECT value FROM log").Should().BeEmpty();

        Execute(owner, "INSERT INTO data VALUES (2, 'two')");
        ReadRows(owner, "SELECT value FROM log").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Text("T:2"));

        var mainObjects = ReadRows(observer, "SELECT type, name FROM sqlite_schema ORDER BY name")
            .Select(row => $"{row[0].AsText()}:{row[1].AsText()}");
        mainObjects.Should().Equal("table:data", "table:log");
    }

    [Test]
    public void TempObjectsAreNeverWrittenToThePersistentSchema()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("temp-objects.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
            Execute(connection, "CREATE TABLE log(value TEXT)");
            Execute(connection, "CREATE TEMP VIEW private_view AS SELECT 1 AS x");
            Execute(
                connection,
                "CREATE TEMP TRIGGER private_trigger AFTER INSERT ON data "
                    + "BEGIN INSERT INTO log VALUES ('T:' || NEW.id); END");
            Execute(connection, "INSERT INTO data VALUES (1)");
            ReadRows(connection, "SELECT value FROM log").Should().ContainSingle();
        }

        // A fresh open is the durable-corruption boundary: nothing temporary may survive it.
        using (var database = EmbeddedDatabase.OpenFile("temp-objects.db", fileSystem))
        using (var connection = database.Connect())
        {
            ReadRows(connection, "SELECT type, name FROM sqlite_schema ORDER BY name")
                .Select(row => $"{row[0].AsText()}:{row[1].AsText()}")
                .Should().Equal("table:data", "table:log");
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "SELECT x FROM private_view"))!
                .Message.Should().Be("no such table: private_view");

            // The trigger is gone with the connection that declared it, so the write is untraced.
            Execute(connection, "INSERT INTO data VALUES (2)");
            ReadRows(connection, "SELECT value FROM log").Should().ContainSingle();
        }
    }

    [Test]
    public void TempObjectsAreDroppedWhenTheOwningConnectionCloses()
    {
        using var database = new EmbeddedDatabase();
        using (var owner = database.Connect())
        {
            Execute(owner, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
            Execute(owner, "CREATE TEMP VIEW private_view AS SELECT 1 AS x");
            Execute(owner, "CREATE TEMP TRIGGER private_trigger AFTER INSERT ON data BEGIN SELECT 1; END");
            ReadRows(owner, "SELECT name FROM sqlite_temp_schema ORDER BY name").Should().HaveCount(2);
        }

        using var successor = database.Connect();
        ReadRows(successor, "SELECT name FROM sqlite_temp_schema").Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(() => Execute(successor, "SELECT x FROM private_view"));
    }

    [Test]
    public void TempTriggerOnAMainTableWritesBothSchemasLikeSqlite()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
            "CREATE TABLE main_log(value TEXT)",
            "CREATE TEMP TABLE temp_log(value TEXT)",
            "CREATE TEMP TRIGGER data_audit AFTER INSERT ON data BEGIN "
                + "INSERT INTO main_log VALUES ('main:' || NEW.id); "
                + "INSERT INTO temp_log VALUES ('temp:' || NEW.value); END",
            "INSERT INTO data VALUES (1, 'one'), (2, 'two')",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(managed, sqlite, "SELECT value FROM main_log ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM temp_log ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT type, name FROM sqlite_schema ORDER BY name");
    }

    [Test]
    public void FailedStatementDiscardsTheTempTriggersCrossSchemaWrites()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "CREATE TEMP TABLE source(id INTEGER PRIMARY KEY)",
            "CREATE TABLE main_log(id INTEGER)",
            "CREATE TEMP TRIGGER source_audit AFTER INSERT ON source BEGIN "
                + "INSERT INTO main_log VALUES (NEW.id); "
                + "SELECT CASE WHEN NEW.id = 2 THEN RAISE(ABORT, 'stop') END; END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(managed, "INSERT INTO source VALUES (1), (2)"));
        Assert.Throws<MsData.SqliteException>(
            () => Execute(sqlite, "INSERT INTO source VALUES (1), (2)"));
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM source ORDER BY id");
        AssertQueriesMatch(managed, sqlite, "SELECT id FROM main_log ORDER BY rowid");
    }

    [Test]
    public void TempObjectsShadowSameNamedMainObjectsLikeSqlite()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY)",
            "CREATE TABLE log(value TEXT)",
            "CREATE VIEW origin AS SELECT 'main' AS name",
            "CREATE TEMP VIEW origin AS SELECT 'temp' AS name",
            "CREATE TRIGGER data_audit AFTER INSERT ON data BEGIN INSERT INTO log VALUES ('main'); END",
            "CREATE TEMP TRIGGER data_audit AFTER INSERT ON data BEGIN INSERT INTO log VALUES ('temp'); END",
            "INSERT INTO data VALUES (1)",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(managed, sqlite, "SELECT name FROM origin");
        AssertQueriesMatch(managed, sqlite, "SELECT name FROM main.origin");
        AssertQueriesMatch(managed, sqlite, "SELECT name FROM temp.origin");

        // Both same-named triggers fire, and the temp one is observed first.
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM log ORDER BY rowid");
    }

    [Test]
    public void DropRemovesTheTempObjectBeforeTheMainObjectLikeSqlite()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY)",
            "CREATE TABLE log(value TEXT)",
            "CREATE VIEW origin AS SELECT 'main' AS name",
            "CREATE TEMP VIEW origin AS SELECT 'temp' AS name",
            "CREATE TRIGGER data_audit AFTER INSERT ON data BEGIN INSERT INTO log VALUES ('main'); END",
            "CREATE TEMP TRIGGER data_audit AFTER INSERT ON data BEGIN INSERT INTO log VALUES ('temp'); END",
            "DROP VIEW origin",
            "DROP TRIGGER data_audit",
            "INSERT INTO data VALUES (1)",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(managed, sqlite, "SELECT name FROM origin");
        AssertQueriesMatch(managed, sqlite, "SELECT value FROM log ORDER BY rowid");
        AssertQueriesMatch(managed, sqlite, "SELECT type, name FROM sqlite_schema ORDER BY name");
        AssertQueriesMatch(managed, sqlite, "SELECT type, name FROM sqlite_temp_schema ORDER BY name");
    }

    [Test]
    public void TempSchemaTableReportsDefinitionsWithoutTheTempKeywordLikeSqlite()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY)",
            "CREATE TEMP VIEW private_view AS SELECT 1 AS x",
            "CREATE TEMP TRIGGER private_trigger AFTER INSERT ON data BEGIN SELECT 1; END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_temp_master ORDER BY name");
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT type, name, tbl_name, sql FROM sqlite_temp_schema ORDER BY name");
    }

    [Test]
    public void DroppingATableOnlyRemovesTheTempTriggersThatWatchIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TEMP TABLE temp_data(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TEMP TRIGGER on_main AFTER INSERT ON data BEGIN SELECT 1; END");
        Execute(connection, "CREATE TEMP TRIGGER on_temp AFTER INSERT ON temp_data BEGIN SELECT 1; END");

        Execute(connection, "DROP TABLE data");

        ReadRows(connection, "SELECT name FROM sqlite_temp_schema WHERE type = 'trigger' ORDER BY name")
            .Select(row => row[0].AsText())
            .Should().Equal("on_temp");
    }

    [Test]
    public void TempViewBodyOutsideTheTempSchemaIsRejectedUpFront()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");

        // The managed engine evaluates a view inside the database that owns it, so a temp view
        // reaching main is refused instead of half-working.
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "CREATE TEMP VIEW over_main AS SELECT id FROM data"))!
            .Message.Should().Be("Managed temporary views can only reference objects in the temp schema.");
        ReadRows(connection, "SELECT name FROM sqlite_temp_schema").Should().BeEmpty();
    }

    [Test]
    public void TempTriggerNamesRejectQualifiersLikeSqlite()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        Execute(managed, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(sqlite, "CREATE TABLE data(id INTEGER PRIMARY KEY)");

        const string qualifiedTrigger =
            "CREATE TEMP TRIGGER temp.qualified AFTER INSERT ON data BEGIN SELECT 1; END";
        var managedError = Assert.Throws<EmbeddedSqlException>(() => Execute(managed, qualifiedTrigger));
        var sqliteError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, qualifiedTrigger));
        managedError!.Message.Should().StartWith("temporary trigger may not have qualified name");
        sqliteError!.Message.Should().Contain("temporary trigger may not have qualified name");

        const string qualifiedView = "CREATE TEMP VIEW main.qualified AS SELECT 1 AS x";
        var managedViewError = Assert.Throws<EmbeddedSqlException>(() => Execute(managed, qualifiedView));
        var sqliteViewError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, qualifiedView));
        managedViewError!.Message.Should().StartWith("temporary table name must be unqualified");
        sqliteViewError!.Message.Should().Contain("temporary table name must be unqualified");

        // temp is the one qualifier SQLite accepts on a temp view name, and it is stripped.
        Execute(managed, "CREATE TEMP VIEW temp.accepted AS SELECT 1 AS x");
        Execute(sqlite, "CREATE TEMP VIEW temp.accepted AS SELECT 1 AS x");
        AssertQueriesMatch(managed, sqlite, "SELECT name, sql FROM sqlite_temp_schema ORDER BY name");
    }

    private static MsData.SqliteConnection OpenSqlite()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssertQueriesMatch(
        EmbeddedConnection managed,
        MsData.SqliteConnection sqlite,
        string query)
    {
        var managedRows = ReadRows(managed, query);
        var sqliteRows = ReadRows(sqlite, query);
        managedRows.Should().HaveCount(sqliteRows.Count, "query '{0}' must return the same row count", query);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            managedRows[row].Should().HaveCount(sqliteRows[row].Length);
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column], query);
        }
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var column = 0; column < values.Length; column++)
                values[column] = statement.GetValue(column);
            rows.Add(values);
        }

        return rows;
    }

    private static IReadOnlyList<object?[]> ReadRows(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(values);
        }

        return rows;
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite, string query)
    {
        switch (sqlite)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null, "query '{0}'", query);
                break;
            case long integer:
                managed.AsInteger().Should().Be(integer, "query '{0}'", query);
                break;
            case double real:
                managed.AsReal().Should().Be(real, "query '{0}'", query);
                break;
            case string text:
                managed.AsText().Should().Be(text, "query '{0}'", query);
                break;
            default:
                managed.ToString().Should().Be(sqlite.ToString(), "query '{0}'", query);
                break;
        }
    }
}
