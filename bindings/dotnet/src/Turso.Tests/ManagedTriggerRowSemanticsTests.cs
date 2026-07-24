using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedTriggerRowSemanticsTests
{
    [Test]
    public void TimingWhenUpdateOfAndRowImagesMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(phase TEXT, old_id, old_value, new_id, new_value)",
                "CREATE TRIGGER data_before_insert BEFORE INSERT ON data "
                    + "WHEN NEW.id > 0 BEGIN "
                    + "INSERT INTO trace VALUES ('BI', NULL, NULL, NEW.id, NEW.value); END",
                "CREATE TRIGGER data_after_insert AFTER INSERT ON data FOR EACH ROW "
                    + "BEGIN INSERT INTO trace VALUES ('AI', NULL, NULL, NEW.id, NEW.value); END",
                "CREATE TRIGGER data_before_update UPDATE OF value ON data "
                    + "BEGIN INSERT INTO trace VALUES ('BU', OLD.id, OLD.value, NEW.id, NEW.value); END",
                "CREATE TRIGGER data_after_update AFTER UPDATE OF value, ghost ON data "
                    + "WHEN NEW.value <> OLD.value BEGIN "
                    + "INSERT INTO trace VALUES ('AU', OLD.id, OLD.value, NEW.id, NEW.value); END",
                "CREATE TRIGGER data_after_delete AFTER DELETE ON data "
                    + "BEGIN INSERT INTO trace VALUES ('AD', OLD.id, OLD.value, NULL, NULL); END",
                "INSERT INTO data VALUES (1, 'one'), (2, 'two')",
                "UPDATE data SET id = id",
                "UPDATE data SET value = upper(value) WHERE id = 2",
                "DELETE FROM data WHERE id = 1",
            ],
            "SELECT phase, old_id, old_value, new_id, new_value FROM trace ORDER BY rowid");
    }

    [Test]
    public void InsteadOfViewTriggersExposeViewRowsLikeSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE base(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(kind TEXT, old_id, old_value, new_id, new_value)",
                "CREATE VIEW projected AS SELECT id, value || '!' AS decorated FROM base",
                "CREATE TRIGGER projected_insert INSTEAD OF INSERT ON projected BEGIN "
                    + "INSERT INTO base VALUES (NEW.id, NEW.decorated); END",
                "CREATE TRIGGER projected_update INSTEAD OF UPDATE OF decorated ON projected BEGIN "
                    + "INSERT INTO trace VALUES ('U', OLD.id, OLD.decorated, NEW.id, NEW.decorated); "
                    + "UPDATE base SET value = NEW.decorated WHERE id = OLD.id; END",
                "CREATE TRIGGER projected_delete INSTEAD OF DELETE ON projected BEGIN "
                    + "INSERT INTO trace VALUES ('D', OLD.id, OLD.decorated, NULL, NULL); "
                    + "DELETE FROM base WHERE id = OLD.id; END",
                "INSERT INTO projected(id, decorated) VALUES (1, 'one')",
                "UPDATE projected SET decorated = 'two' WHERE id = 1",
                "DELETE FROM projected WHERE id = 1",
            ],
            "SELECT kind, old_id, old_value, new_id, new_value FROM trace ORDER BY rowid");
    }

    [Test]
    public void RaiseIgnoreAndFailPreserveTheSamePrefixesAsSqlite()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('pre' || NEW.id); "
                    + "SELECT CASE WHEN NEW.id = 2 THEN RAISE(FAIL, 'boom') END; "
                    + "INSERT INTO trace VALUES ('post' || NEW.id); END",
            ],
            "INSERT INTO data VALUES (1), (2), (3)",
            "SELECT 'data', id FROM data UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");

        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('pre' || NEW.id); "
                    + "SELECT CASE WHEN NEW.id = 2 THEN RAISE(IGNORE) END; "
                    + "INSERT INTO trace VALUES ('post' || NEW.id); END",
                "INSERT INTO data VALUES (1), (2), (3)",
            ],
            "SELECT 'data', id FROM data UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");
    }

    [Test]
    public void ForeignKeyActionsRunChildRowTriggersBeforeParentAfter()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                    + "REFERENCES parent(id) ON DELETE CASCADE)",
                "CREATE TRIGGER parent_before BEFORE DELETE ON parent "
                    + "BEGIN INSERT INTO trace VALUES ('PB:' || OLD.id); END",
                "CREATE TRIGGER parent_after AFTER DELETE ON parent "
                    + "BEGIN INSERT INTO trace VALUES ('PA:' || OLD.id); END",
                "CREATE TRIGGER child_before BEFORE DELETE ON child "
                    + "BEGIN INSERT INTO trace VALUES ('CB:' || OLD.id); END",
                "CREATE TRIGGER child_after AFTER DELETE ON child "
                    + "BEGIN INSERT INTO trace VALUES ('CA:' || OLD.id); END",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (10, 1)",
                "DELETE FROM parent",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void UpsertGeneratedAndWithoutRowidImagesMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE items(key TEXT PRIMARY KEY, seed INTEGER, "
                    + "doubled INTEGER AS (seed * 2) STORED) WITHOUT ROWID",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER items_before_insert BEFORE INSERT ON items BEGIN "
                    + "INSERT INTO trace VALUES ('BI:' || NEW.key || ':' || NEW.doubled); END",
                "CREATE TRIGGER items_after_insert AFTER INSERT ON items BEGIN "
                    + "INSERT INTO trace VALUES ('AI:' || NEW.key || ':' || NEW.doubled); END",
                "CREATE TRIGGER items_before_update BEFORE UPDATE OF seed ON items BEGIN "
                    + "INSERT INTO trace VALUES ('BU:' || OLD.doubled || ':' || NEW.doubled); END",
                "CREATE TRIGGER items_after_update AFTER UPDATE OF seed ON items BEGIN "
                    + "INSERT INTO trace VALUES ('AU:' || OLD.doubled || ':' || NEW.doubled); END",
                "INSERT INTO items(key, seed) VALUES ('a', 2)",
                "INSERT INTO items(key, seed) VALUES ('a', 3) "
                    + "ON CONFLICT(key) DO UPDATE SET seed = excluded.seed",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void AutomaticRowidIsFinalizedAfterBeforeTriggerWork()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(phase TEXT, value INTEGER)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data WHEN NEW.value = 'outer' BEGIN "
                    + "INSERT INTO trace VALUES ('before-rowid', NEW.rowid); "
                    + "INSERT INTO data(value) VALUES ('inner'); END",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('after-rowid', NEW.rowid); END",
                "INSERT INTO data(value) VALUES ('outer')",
            ],
            "SELECT phase, value FROM trace ORDER BY rowid");
    }

    [Test]
    public void AutomaticRowidTracksIgnoredAndAfterInsertedRows()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data WHEN NEW.value = 'skip' "
                    + "BEGIN SELECT RAISE(IGNORE); END",
                "CREATE TRIGGER data_after AFTER INSERT ON data WHEN NEW.value = 'first' "
                    + "BEGIN INSERT INTO data(value) VALUES ('nested'); END",
                "INSERT INTO data(value) VALUES ('skip'), ('first'), ('second')",
            ],
            "SELECT id, value FROM data ORDER BY id");
    }

    [Test]
    public void ReturningCapturesDirectRowBeforeAfterTriggerChanges()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "INSERT INTO data VALUES (1, 10)",
            "CREATE TRIGGER data_after AFTER UPDATE ON data WHEN NEW.value < 100 BEGIN "
                + "UPDATE data SET value = NEW.value + 100 WHERE id = NEW.id; END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(
            managed,
            sqlite,
            "UPDATE data SET value = 20 WHERE id = 1 RETURNING id, value");
        AssertQueriesMatch(managed, sqlite, "SELECT id, value FROM data");
    }

    [Test]
    public void ReplaceDeleteTriggersAndOuterConflictOverrideMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT UNIQUE)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_deleted AFTER DELETE ON data BEGIN "
                    + "INSERT INTO trace VALUES ('D:' || OLD.id); END",
                "CREATE TRIGGER data_inserted AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('I:' || NEW.id); END",
                "INSERT INTO data VALUES (1, 'same')",
                "DELETE FROM trace",
                "INSERT OR REPLACE INTO data VALUES (2, 'same')",
            ],
            "SELECT value FROM trace ORDER BY rowid");

        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER PRIMARY KEY)",
                "CREATE TABLE sink(id INTEGER PRIMARY KEY)",
                "INSERT INTO sink VALUES (1)",
                "CREATE TRIGGER source_inserted AFTER INSERT ON source BEGIN "
                    + "INSERT OR IGNORE INTO sink VALUES (1); END",
            ],
            "INSERT OR ABORT INTO source VALUES (1)",
            "SELECT (SELECT COUNT(*) FROM source), (SELECT COUNT(*) FROM sink)");
    }

    [Test]
    public void NotNullReplaceDefaultsAreAppliedBetweenBeforeAndAfter()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT NOT NULL DEFAULT 'default')",
                "CREATE TABLE trace(phase TEXT, value TEXT)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('before', NEW.value); END",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES ('after', NEW.value); END",
                "INSERT OR REPLACE INTO data VALUES (1, NULL)",
            ],
            "SELECT phase, value FROM trace ORDER BY rowid");
    }

    [Test]
    public void ScalarCallbackOrderMatchesSqlite()
    {
        var managedCallbacks = new List<string>();
        var sqliteCallbacks = new List<string>();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "tap",
            2,
            values =>
            {
                managedCallbacks.Add(values[0].AsText());
                return values[1];
            });
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        sqlite.CreateFunction<string, long, long>(
            "tap",
            (phase, value) =>
            {
                sqliteCallbacks.Add(phase);
                return value;
            });
        var setup = new[]
        {
            "CREATE TABLE data(value INTEGER CHECK(value IS NOT NULL))",
            "CREATE TRIGGER data_before BEFORE INSERT ON data "
                + "WHEN tap('when', 1) BEGIN SELECT tap('before', 1); END",
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "BEGIN SELECT tap('after', 1); END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(
            managed,
            sqlite,
            "INSERT INTO data VALUES (tap('assign', 7)) RETURNING tap('returning', value)");
        managedCallbacks.Should().Equal(sqliteCallbacks);
    }

    [Test]
    public void CancellationInsideTriggerRollsBackTheWriteTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "cancel_write",
            1,
            values =>
            {
                cancellation.Cancel();
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "WHEN NEW.id = 2 BEGIN SELECT cancel_write(NEW.id); END");
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO data VALUES (99)");

        using (var statement = connection.Prepare("INSERT INTO data VALUES (1), (2), (3)"))
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));

        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ROLLBACK"))!
            .Message.Should().Be("cannot rollback - no transaction is active");
    }

    [Test]
    public void FileTriggersPreserveRowSemanticsAndOrderAcrossReopenAndPageMigration()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("row-triggers.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)");
            Execute(connection, "CREATE TABLE trace(value TEXT)");
            Execute(
                connection,
                "CREATE TRIGGER first AFTER INSERT ON data "
                    + "BEGIN INSERT INTO trace VALUES ('first:' || NEW.id); END");
            Execute(
                connection,
                "CREATE TRIGGER second AFTER INSERT ON data WHEN NEW.value IS NOT NULL "
                    + "BEGIN INSERT INTO trace VALUES ('second:' || NEW.value); END");
            Execute(connection, "INSERT INTO data VALUES (1, 'one')");
            ReadRows(connection, "SELECT value FROM trace ORDER BY rowid")
                .Select(row => row[0].AsText())
                .Should().Equal("second:one", "first:1");
        }

        using (var database = EmbeddedDatabase.OpenFile("row-triggers.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "DELETE FROM trace");
            Execute(connection, "INSERT INTO data VALUES (2, 'two')");
            Execute(connection, "PRAGMA page_size = 8192");
            Execute(connection, "VACUUM");
            Execute(connection, "INSERT INTO data VALUES (3, 'three')");
            ReadRows(connection, "SELECT value FROM trace ORDER BY rowid")
                .Select(row => row[0].AsText())
                .Should().Equal(
                    "second:two",
                    "first:2",
                    "second:three",
                    "first:3");
            ReadRows(
                    connection,
                    "SELECT sql FROM sqlite_schema WHERE type = 'trigger' AND name = 'second'")
                .Should().ContainSingle()
                .Which[0].AsText().Should().Contain("WHEN NEW.value IS NOT NULL");
        }
    }

    [Test]
    public void AttachedPersistentTriggersStayWithinTheirDatabase()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("trigger-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE main.data(id INTEGER PRIMARY KEY)");
        Execute(connection, "ATTACH DATABASE 'trigger-aux.db' AS aux");
        Execute(connection, "CREATE TABLE aux.data(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE aux.trace(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER aux.data_after AFTER INSERT ON aux.data "
                + "BEGIN INSERT INTO trace VALUES (NEW.id); END");
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO aux.data VALUES (7)");
        Execute(connection, "COMMIT");

        ReadRows(connection, "SELECT id FROM aux.trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(7));
        var crossDatabase = () => Execute(
            connection,
            "CREATE TRIGGER main.cross_database AFTER INSERT ON main.data "
                + "BEGIN INSERT INTO aux.trace VALUES (NEW.id); END");
        crossDatabase.Should().Throw<EmbeddedSqlException>();
        Execute(connection, "INSERT INTO main.data VALUES (8)");
        ReadRows(connection, "SELECT id FROM aux.trace").Should().ContainSingle();
    }

    [Test]
    public void RaiseAbortAndRollbackUseSqliteTransactionScopes()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE prior(value INTEGER)",
                "CREATE TABLE data(id INTEGER)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_abort AFTER INSERT ON data WHEN NEW.id = 2 BEGIN "
                    + "INSERT INTO trace VALUES ('seen'); SELECT RAISE(ABORT, 'abort-trigger'); END",
                "BEGIN",
                "INSERT INTO prior VALUES (1)",
            ],
            "INSERT INTO data VALUES (1), (2), (3)",
            "SELECT 'prior', value FROM prior "
                + "UNION ALL SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE prior(value INTEGER)");
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_rollback AFTER INSERT ON data WHEN NEW.id = 2 "
                + "BEGIN SELECT RAISE(ROLLBACK, 'rollback-trigger'); END");
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO prior VALUES (1)");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO data VALUES (1), (2), (3)"))!
            .Message.Should().Be("rollback-trigger");
        ReadRows(connection, "SELECT value FROM prior").Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "COMMIT"))!
            .Message.Should().Be("cannot commit - no transaction is active");
    }

    [Test]
    public void TriggerLocalLastInsertRowidIsRestoredAfterFail()
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY)",
            "CREATE TABLE side(id INTEGER PRIMARY KEY, value TEXT)",
            "CREATE TABLE trace(value INTEGER)",
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "INSERT INTO side(value) VALUES ('nested'); "
                + "INSERT INTO trace VALUES (last_insert_rowid()); "
                + "SELECT RAISE(FAIL, 'failed-after'); END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        Assert.Throws<EmbeddedSqlException>(() => Execute(managed, "INSERT INTO data VALUES (10)"));
        Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, "INSERT INTO data VALUES (10)"));
        AssertQueriesMatch(
            managed,
            sqlite,
            "SELECT (SELECT id FROM data), (SELECT id FROM side), "
                + "(SELECT value FROM trace), last_insert_rowid()");
    }

    [Test]
    public void NestedRaiseIgnoreReturnsToTheParentTrigger()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE parent(id INTEGER)",
                "CREATE TABLE child(id INTEGER)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER child_before BEFORE INSERT ON child BEGIN "
                    + "INSERT INTO trace VALUES ('child-before'); SELECT RAISE(IGNORE); END",
                "CREATE TRIGGER parent_after AFTER INSERT ON parent BEGIN "
                    + "INSERT INTO child VALUES (NEW.id); "
                    + "INSERT INTO trace VALUES ('parent-resumed'); END",
                "INSERT INTO parent VALUES (1)",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void WithoutRowidCandidateIdentitySurvivesTriggerResorting()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(key TEXT PRIMARY KEY, value INTEGER) WITHOUT ROWID",
                "CREATE TABLE trace(value TEXT)",
                "INSERT INTO data VALUES ('a', 0), ('b', 0)",
                "CREATE TRIGGER data_after AFTER UPDATE ON data BEGIN "
                    + "INSERT OR IGNORE INTO data VALUES (NEW.key || 'x', 0); "
                    + "INSERT INTO trace VALUES (NEW.key); END",
                "UPDATE data SET value = value + 1 WHERE key IN ('a', 'b')",
            ],
            "SELECT key, value FROM data ORDER BY key");
    }

    [Test]
    public void RecursiveCyclesAreRejectedBeforeCallbacksOrMutation()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                + "SELECT mark(NEW.id); INSERT INTO data VALUES (NEW.id + 1); END");
        Execute(connection, "PRAGMA recursive_triggers = ON");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO data VALUES (1)"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();
    }

    [Test]
    public void UnsafeBeforeUpdateSelfMutationIsRejectedBeforeCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)");
        Execute(connection, "INSERT INTO data VALUES (1, 10)");
        Execute(
            connection,
            "CREATE TRIGGER data_before BEFORE UPDATE ON data BEGIN "
                + "SELECT mark(OLD.id); DELETE FROM data WHERE id = OLD.id; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "UPDATE data SET value = 11 WHERE id = 1"))!
            .Message.Should().Contain("unsafe BEFORE trigger");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id, value FROM data").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
    }

    [Test]
    public void UnsafeForeignKeyActionTriggerIsRejectedBeforeParentMutation()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                + "REFERENCES parent(id) ON UPDATE CASCADE, note TEXT)");
        Execute(connection, "INSERT INTO parent VALUES (1)");
        Execute(connection, "INSERT INTO child VALUES (10, 1, 'old')");
        Execute(
            connection,
            "CREATE TRIGGER child_before BEFORE UPDATE ON child BEGIN "
                + "SELECT mark(OLD.id); UPDATE child SET note = 'trigger' WHERE id = OLD.id; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "UPDATE parent SET id = 2 WHERE id = 1"))!
            .Message.Should().Contain("unsafe BEFORE trigger");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM parent").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
        ReadRows(connection, "SELECT parent_id, note FROM child").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Text("old"));
    }

    [Test]
    public void TriggerBodyRestrictionsFailBeforeCatalogOrRowMutation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "CREATE TABLE trace(id INTEGER)");
        var rejected = new[]
        {
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO trace DEFAULT VALUES; END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO main.trace VALUES (1); END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO trace VALUES (1) RETURNING id; END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN UPDATE trace SET id = 1 LIMIT 1; END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO trace VALUES (?); END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN CREATE TABLE nested(id); END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN PRAGMA foreign_keys; END",
            "CREATE TEMP TRIGGER bad AFTER INSERT ON data BEGIN INSERT INTO trace VALUES (1); END",
            "CREATE TRIGGER bad AFTER INSERT ON data BEGIN "
                + "SELECT RAISE(FAIL, 'dynamic-' || NEW.id); END",
        };
        foreach (var sql in rejected)
        {
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, sql));
            ReadRows(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger'")
                .Should().ContainSingle().Which[0].Should().Be(SqlValue.Integer(0));
        }

        Execute(connection, "INSERT INTO data VALUES (1)");
        ReadRows(connection, "SELECT id FROM data").Should().ContainSingle();
        ReadRows(connection, "SELECT id FROM trace").Should().BeEmpty();
    }

    [Test]
    public void LazyTriggerProgramErrorsPreflightBeforeSourceCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER missing_body AFTER INSERT ON data "
                + "BEGIN INSERT INTO missing VALUES (NEW.id); END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO data VALUES (mark(1))"))!
            .Message.Should().Contain("no such table: missing");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER missing_body");
        Execute(
            connection,
            "CREATE TRIGGER illegal_old AFTER INSERT ON data "
                + "BEGIN SELECT OLD.id; END");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO data VALUES (mark(2))"))!
            .Message.Should().Contain("no such column: OLD.id");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM data").Should().BeEmpty();
    }

    [Test]
    public void TriggerChangesDoNotChangeTheOuterCandidateSet()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
                "INSERT INTO data VALUES (1, 1), (2, 1)",
                "CREATE TRIGGER first_after AFTER UPDATE ON data WHEN NEW.id = 1 BEGIN "
                    + "UPDATE data SET value = 0 WHERE id = 2; END",
                "UPDATE data SET value = 2 WHERE value = 1",
            ],
            "SELECT id, value FROM data ORDER BY id");
    }

    [Test]
    public void UpsertUpdateOfAndReplaceDeleteOnlyTriggersMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(key INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_updated AFTER UPDATE OF value ON data BEGIN "
                    + "INSERT INTO trace VALUES ('U:' || NEW.value); END",
                "INSERT INTO data VALUES (1, 'old')",
                "INSERT INTO data VALUES (1, 'new') "
                    + "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ],
            "SELECT value FROM trace ORDER BY rowid");

        AssertMatchesSqlite(
            [
                "PRAGMA recursive_triggers = ON",
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT UNIQUE)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_deleted AFTER DELETE ON data BEGIN "
                    + "INSERT INTO trace VALUES ('D:' || OLD.id); END",
                "INSERT INTO data VALUES (1, 'same')",
                "INSERT OR REPLACE INTO data VALUES (2, 'same')",
            ],
            "SELECT value FROM trace ORDER BY rowid");
    }

    [Test]
    public void CheckConflictsAndWithDmlFailKeepSqlitePrefixes()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(value INTEGER CHECK(value > 0))",
                "CREATE TABLE trace(value INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES (NEW.value); END",
                "INSERT OR IGNORE INTO data VALUES (1), (-1), (2)",
            ],
            "SELECT 'data', value FROM data "
                + "UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");

        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(value INTEGER)",
                "CREATE TABLE trace(value INTEGER)",
                "CREATE TRIGGER data_before BEFORE INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES (NEW.value); "
                    + "SELECT CASE WHEN NEW.value = 2 THEN RAISE(FAIL, 'cte-fail') END; END",
            ],
            "WITH input(value) AS (VALUES (1), (2), (3)) "
                + "INSERT INTO data SELECT value FROM input",
            "SELECT 'data', value FROM data "
                + "UNION ALL SELECT 'trace', value FROM trace ORDER BY 1, 2");
    }

    [Test]
    public void OuterConflictPolicyDoesNotEraseBodyUpsert()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER PRIMARY KEY)",
                "CREATE TABLE sink(id INTEGER PRIMARY KEY)",
                "INSERT INTO sink VALUES (1)",
                "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                    + "INSERT INTO sink VALUES (1) ON CONFLICT(id) DO NOTHING; END",
                "INSERT OR ABORT INTO source VALUES (1)",
            ],
            "SELECT (SELECT COUNT(*) FROM source), (SELECT COUNT(*) FROM sink)");
    }

    [Test]
    public void MultirowUpsertEvaluatesAllValuesBeforeRowTriggers()
    {
        var managedCallbacks = new List<string>();
        var sqliteCallbacks = new List<string>();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "tap",
            2,
            values =>
            {
                managedCallbacks.Add(values[0].AsText());
                return values[1];
            });
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        sqlite.CreateFunction<string, long, long>(
            "tap",
            (phase, value) =>
            {
                sqliteCallbacks.Add(phase);
                return value;
            });
        var setup = new[]
        {
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value INTEGER)",
            "CREATE TRIGGER data_before BEFORE INSERT ON data "
                + "BEGIN SELECT tap('trigger-' || NEW.id, NEW.id); END",
        };
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        var statement = "INSERT INTO data VALUES "
            + "(1, tap('assign-1', 1)), (2, tap('assign-2', 2)), (3, tap('assign-3', 3)) "
            + "ON CONFLICT(id) DO UPDATE SET value = excluded.value";
        Execute(managed, statement);
        Execute(sqlite, statement);
        managedCallbacks.Should().Equal(sqliteCallbacks);
    }

    [Test]
    public void DuplicateViewInsertColumnsUseTheFirstValue()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE trace(value INTEGER)",
                "CREATE VIEW projected AS SELECT value FROM trace",
                "CREATE TRIGGER projected_insert INSTEAD OF INSERT ON projected BEGIN "
                    + "INSERT INTO trace VALUES (NEW.value); END",
                "INSERT INTO projected(value, value) VALUES (1, 2)",
            ],
            "SELECT value FROM trace");
    }

    [Test]
    public void TriggerDependentRenamesAreRejectedWithoutCatalogDamage()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "CREATE TABLE trace(id INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "BEGIN INSERT INTO trace VALUES (NEW.id); END");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ALTER TABLE data RENAME TO renamed"));
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ALTER TABLE data RENAME COLUMN id TO value"));
        Execute(connection, "INSERT INTO data VALUES (1)");
        ReadRows(connection, "SELECT id FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void FileTriggerDependencyScanCoversInsertSourcesAndCurrentTime()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("trigger-dependencies.db", fileSystem))
        {
            database.RegisterScalarFunction("custom_value", 1, values => values[0]);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE data(id INTEGER)");
            Execute(connection, "CREATE TABLE trace(id INTEGER)");
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "CREATE TRIGGER callback_after AFTER INSERT ON data BEGIN "
                        + "INSERT INTO trace SELECT custom_value(NEW.id); END"));
            Execute(
                connection,
                "CREATE TRIGGER time_after AFTER INSERT ON data "
                    + "WHEN CURRENT_TIMESTAMP IS NOT NULL "
                    + "BEGIN INSERT INTO trace VALUES (NEW.id); END");
        }

        using var reopened = EmbeddedDatabase.OpenFile("trigger-dependencies.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data VALUES (1)");
        ReadRows(reopenedConnection, "SELECT id FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void NestedQueryAndReplaceDeleteProgramsPreflightBeforeMutation()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source(id INTEGER)");
        Execute(connection, "CREATE TABLE empty_target(value INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                + "UPDATE empty_target SET value = (SELECT value FROM missing); END");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO source VALUES (mark(1))"))!
            .Message.Should().Contain("no such table: missing");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM source").Should().BeEmpty();

        Execute(connection, "DROP TRIGGER source_after");
        Execute(connection, "CREATE TABLE replacement(id INTEGER PRIMARY KEY, value TEXT UNIQUE)");
        Execute(connection, "INSERT INTO replacement VALUES (1, 'same')");
        Execute(
            connection,
            "CREATE TRIGGER replacement_delete BEFORE DELETE ON replacement "
                + "BEGIN SELECT OLD.missing; END");
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT OR REPLACE INTO replacement VALUES (2, 'same')"))!
            .Message.Should().Contain("no such column: OLD.missing");
        ReadRows(connection, "SELECT id, value FROM replacement").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Text("same"));
    }

    [Test]
    public void ForeignKeyActionEdgesParticipateInUnsafeMutationPreflight()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                + "REFERENCES parent(id) ON UPDATE CASCADE ON DELETE CASCADE)");
        Execute(connection, "INSERT INTO parent VALUES (1)");
        Execute(connection, "INSERT INTO child VALUES (10, 1)");
        Execute(
            connection,
            "CREATE TRIGGER child_before BEFORE UPDATE ON child BEGIN "
                + "DELETE FROM parent WHERE id = NEW.parent_id; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "UPDATE parent SET id = 2 WHERE id = 1"))!
            .Message.Should().Contain("unsafe BEFORE trigger");
        ReadRows(connection, "SELECT id FROM parent").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(1));
        ReadRows(connection, "SELECT id, parent_id FROM child").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(10), SqlValue.Integer(1));
    }

    [Test]
    public void AttachedSchemaNamedNewPreservesPseudoRowReferencesAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("named-new-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'named-new-aux.db' AS new");
            Execute(connection, "CREATE TABLE new.data(id INTEGER)");
            Execute(connection, "CREATE TABLE new.trace(id INTEGER)");
            Execute(
                connection,
                "CREATE TRIGGER new.data_after AFTER INSERT ON new.data "
                    + "BEGIN INSERT INTO trace VALUES (NEW.id); END");
            Execute(connection, "DETACH new");
        }

        using var reopened = EmbeddedDatabase.OpenFile("named-new-aux.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data VALUES (7)");
        ReadRows(reopenedConnection, "SELECT id FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void TriggerIndependentColumnRenameRemainsSupported()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "CREATE TABLE trace(value TEXT)");
        Execute(
            connection,
            "CREATE TRIGGER data_after AFTER INSERT ON data "
                + "BEGIN INSERT INTO trace VALUES ('inserted'); END");

        Execute(connection, "ALTER TABLE data RENAME COLUMN id TO value");
        Execute(connection, "INSERT INTO data VALUES (1)");
        ReadRows(connection, "SELECT value FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Text("inserted"));
    }

    [Test]
    public void OuterIgnoreAppliesToTriggerUpdateConflicts()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE source(id INTEGER PRIMARY KEY)",
                "CREATE TABLE sink(id INTEGER PRIMARY KEY, value INTEGER UNIQUE)",
                "INSERT INTO sink VALUES (1, 1), (2, 2)",
                "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                    + "UPDATE sink SET value = 2 WHERE id = 1; END",
                "INSERT OR IGNORE INTO source VALUES (1)",
            ],
            "SELECT (SELECT COUNT(*) FROM source), "
                + "(SELECT value FROM sink WHERE id = 1), (SELECT value FROM sink WHERE id = 2)");
    }

    [Test]
    public void InsertOrPolicyCanAccompanyTriggeredUpsert()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_update AFTER UPDATE OF value ON data BEGIN "
                    + "INSERT INTO trace VALUES (NEW.value); END",
                "INSERT INTO data VALUES (1, 'old')",
                "INSERT OR IGNORE INTO data VALUES (1, 'new') "
                    + "ON CONFLICT(id) DO UPDATE SET value = excluded.value",
            ],
            "SELECT value FROM trace");
    }

    [Test]
    public void RecreatedTriggerOrderPersistsWhenSchemaTextIsOtherwiseIdentical()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("trigger-order.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER)");
            Execute(connection, "CREATE TABLE trace(value TEXT)");
            Execute(
                connection,
                "CREATE TRIGGER first AFTER INSERT ON data "
                    + "BEGIN INSERT INTO trace VALUES ('first'); END");
            Execute(
                connection,
                "CREATE TRIGGER second AFTER INSERT ON data "
                    + "BEGIN INSERT INTO trace VALUES ('second'); END");
            Execute(connection, "DROP TRIGGER first");
            Execute(
                connection,
                "CREATE TRIGGER first AFTER INSERT ON data "
                    + "BEGIN INSERT INTO trace VALUES ('first'); END");
        }

        using var reopened = EmbeddedDatabase.OpenFile("trigger-order.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data VALUES (1)");
        ReadRows(reopenedConnection, "SELECT value FROM trace ORDER BY rowid")
            .Select(row => row[0].AsText())
            .Should().Equal("first", "second");
    }

    [Test]
    public void FileTriggersRejectImplicitCustomCollationDependencies()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("trigger-collation.db", fileSystem);
        database.RegisterCollation("CUSTOM", string.CompareOrdinal);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(value TEXT COLLATE CUSTOM)");
        Execute(connection, "CREATE TABLE trace(value TEXT)");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(
                connection,
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace SELECT DISTINCT value FROM data; END"))!
            .Message.Should().Contain("custom collation 'CUSTOM'");
    }

    [Test]
    public void QualifiedDropTriggerAndQuotedRaiseFunctionAreSupported()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("drop-trigger-main.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'drop-trigger-aux.db' AS aux");
            Execute(connection, "CREATE TABLE aux.data(id INTEGER)");
            Execute(
                connection,
                "CREATE TRIGGER aux.data_after AFTER INSERT ON aux.data BEGIN SELECT NEW.id; END");
            Execute(connection, "DROP TRIGGER aux.data_after");
            ReadRows(
                    connection,
                    "SELECT COUNT(*) FROM aux.sqlite_schema WHERE type = 'trigger'")
                .Should().ContainSingle().Which[0].Should().Be(SqlValue.Integer(0));
        }

        var calls = 0;
        using var memory = new EmbeddedDatabase();
        memory.RegisterScalarFunction(
            "RAISE",
            1,
            values =>
            {
                calls++;
                return values[0];
            });
        using var memoryConnection = memory.Connect();
        Execute(memoryConnection, "CREATE TABLE data(id INTEGER)");
        Execute(
            memoryConnection,
            "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN SELECT \"RAISE\"(NEW.id); END");
        Execute(memoryConnection, "INSERT INTO data VALUES (1)");
        calls.Should().Be(1);
    }

    [Test]
    public void UpsertNonTargetConflictsAndDoUpdateTriggersUseSqlitePolicies()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT UNIQUE)",
                "CREATE TABLE trace(id INTEGER)",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id); END",
                "INSERT INTO data VALUES (1, 'existing')",
            ],
            "INSERT OR FAIL INTO data VALUES (2, 'new'), (3, 'existing') "
                + "ON CONFLICT(id) DO NOTHING",
            "SELECT 'data', id FROM data "
                + "UNION ALL SELECT 'trace', id FROM trace ORDER BY 1, 2");

        AssertErrorAndStateMatchesSqlite(
            [
                "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)",
                "CREATE TABLE audit(id INTEGER PRIMARY KEY)",
                "INSERT INTO data VALUES (1, 'old')",
                "INSERT INTO audit VALUES (1)",
                "CREATE TRIGGER data_updated AFTER UPDATE ON data BEGIN "
                    + "INSERT INTO audit VALUES (1); END",
            ],
            "INSERT OR IGNORE INTO data VALUES (1, 'new') "
                + "ON CONFLICT(id) DO UPDATE SET value = excluded.value",
            "SELECT id, value FROM data");
    }

    [Test]
    public void OuterIgnoreDoesNotSuppressForeignKeyRestrict()
    {
        AssertErrorAndStateMatchesSqlite(
            [
                "PRAGMA foreign_keys = ON",
                "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
                "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) ON UPDATE RESTRICT)",
                "CREATE TABLE source(id INTEGER)",
                "INSERT INTO parent VALUES (1)",
                "INSERT INTO child VALUES (1)",
                "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                    + "UPDATE parent SET id = 2 WHERE id = 1; "
                    + "UPDATE child SET parent_id = 2; END",
            ],
            "INSERT OR IGNORE INTO source VALUES (1)",
            "SELECT (SELECT id FROM parent), (SELECT parent_id FROM child), "
                + "(SELECT COUNT(*) FROM source)");
    }

    [Test]
    public void ColumnRenameDetectsDependenciesFromOtherTriggerTargets()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(value INTEGER)");
        Execute(connection, "CREATE TABLE source(id INTEGER)");
        Execute(connection, "CREATE TABLE trace(value INTEGER)");
        Execute(
            connection,
            "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                + "INSERT INTO trace SELECT value FROM target; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "ALTER TABLE target RENAME COLUMN value TO renamed"))!
            .Message.Should().Contain("trigger source_after depends on it");
        Execute(connection, "INSERT INTO target VALUES (7)");
        Execute(connection, "INSERT INTO source VALUES (1)");
        ReadRows(connection, "SELECT value FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void TransitiveForeignKeyCyclesPreflightBeforeParentCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER "
                + "REFERENCES parent(id) ON DELETE CASCADE)");
        Execute(
            connection,
            "CREATE TABLE grandchild(id INTEGER PRIMARY KEY, child_id INTEGER "
                + "REFERENCES child(id) ON DELETE CASCADE)");
        Execute(connection, "CREATE TRIGGER parent_before BEFORE DELETE ON parent BEGIN SELECT mark(OLD.id); END");
        Execute(
            connection,
            "CREATE TRIGGER grandchild_after AFTER DELETE ON grandchild BEGIN "
                + "DELETE FROM grandchild WHERE id = OLD.id; END");
        Execute(connection, "INSERT INTO parent VALUES (1)");
        Execute(connection, "INSERT INTO child VALUES (10, 1)");
        Execute(connection, "INSERT INTO grandchild VALUES (100, 10)");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "DELETE FROM parent"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
        ReadRows(connection, "SELECT id FROM parent").Should().ContainSingle();
    }

    [Test]
    public void InsteadOfCyclePreflightRunsBeforeViewProjectionCallbacks()
    {
        var callbacks = 0;
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            1,
            values =>
            {
                callbacks++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "PRAGMA recursive_triggers = ON");
        Execute(connection, "CREATE TABLE data(id INTEGER)");
        Execute(connection, "INSERT INTO data VALUES (1)");
        Execute(connection, "CREATE VIEW projected AS SELECT mark(id) AS id FROM data");
        Execute(
            connection,
            "CREATE TRIGGER projected_update INSTEAD OF UPDATE ON projected BEGIN "
                + "UPDATE projected SET id = NEW.id; END");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "UPDATE projected SET id = 2"))!
            .Message.Should().Be("too many levels of trigger recursion");
        callbacks.Should().Be(0);
    }

    [Test]
    public void QuotedCurrentDateColumnRemainsAColumnInTriggerBodies()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE data(\"CURRENT_DATE\" TEXT)",
                "CREATE TABLE trace(value TEXT)",
                "CREATE TRIGGER data_after AFTER INSERT ON data BEGIN "
                    + "INSERT INTO trace SELECT \"CURRENT_DATE\" FROM data; END",
                "INSERT INTO data VALUES ('sentinel')",
            ],
            "SELECT value FROM trace");
    }

    [Test]
    public void FileTriggerCollationScanIsNestedAndAllowsBuiltins()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("nested-collation.db", fileSystem))
        {
            database.RegisterCollation("CUSTOM", string.CompareOrdinal);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE source(id INTEGER)");
            Execute(connection, "CREATE TABLE custom_values(value TEXT COLLATE CUSTOM)");
            Execute(connection, "CREATE TABLE trace(value TEXT)");
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "CREATE TRIGGER source_after AFTER INSERT ON source BEGIN "
                        + "UPDATE trace SET value = (SELECT DISTINCT value FROM custom_values); END"))!
                .Message.Should().Contain("custom collation 'CUSTOM'");
            Execute(
                connection,
                "CREATE TRIGGER builtin_after AFTER INSERT ON source BEGIN "
                    + "INSERT INTO trace VALUES (NEW.id COLLATE NOCASE); END");
        }

        using var reopened = EmbeddedDatabase.OpenFile("nested-collation.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO source VALUES (1)");
        ReadRows(reopenedConnection, "SELECT value FROM trace").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Text("1"));
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        AssertQueriesMatch(managed, sqlite, query);
    }

    private static void AssertErrorAndStateMatchesSqlite(
        IReadOnlyList<string> setup,
        string failingSql,
        string query)
    {
        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var sqlite = OpenSqlite();
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(sqlite, sql);
        }

        var managedError = Assert.Throws<EmbeddedSqlException>(() => Execute(managed, failingSql));
        var sqliteError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, failingSql));
        sqliteError!.Message.Should().Contain(managedError!.Message);
        AssertQueriesMatch(managed, sqlite, query);
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
        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            managedRows[row].Should().HaveCount(sqliteRows[row].Length);
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column]);
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

    private static void CellShouldMatch(SqlValue managed, object? sqlite)
    {
        switch (sqlite)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.AsInteger().Should().Be(integer);
                break;
            case double real:
                managed.AsReal().Should().BeApproximately(real, 1e-9);
                break;
            case string text:
                managed.AsText().Should().Be(text);
                break;
            case byte[] blob:
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new AssertionException($"Unsupported SQLite value type {sqlite.GetType().Name}.");
        }
    }
}
