using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

public sealed class ManagedForeignKeyFileCatalogDurabilityTests
{
    [Test]
    public void SupportedForeignKeysRoundTripAndEnforceChildAndParentMutationsAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-file-catalog-roundtrip.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT);");
            Execute(connection, "CREATE UNIQUE INDEX parent_code ON parent(code);");
            Execute(
                connection,
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id), parent_code TEXT, FOREIGN KEY(parent_code) REFERENCES parent(code));");
            Execute(connection, "INSERT INTO parent VALUES (1, 'one'), (2, 'two');");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ScalarText(connection, "SELECT sql FROM sqlite_schema WHERE name = 'child';")
                .Should().Contain("REFERENCES \"parent\" (\"id\")")
                .And.Contain("REFERENCES \"parent\" (\"code\")");

            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "INSERT INTO child VALUES (10, 1, 'one');");
            Execute(connection, "UPDATE parent SET code = 'two-updated' WHERE id = 2;");
            Execute(connection, "UPDATE child SET parent_id = 2, parent_code = 'two-updated' WHERE id = 10;");

            Action invalidChildInsert = () => Execute(connection, "INSERT INTO child VALUES (11, 999, 'missing');");
            invalidChildInsert.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

            Action invalidChildUpdate = () => Execute(connection, "UPDATE child SET parent_id = 999 WHERE id = 10;");
            invalidChildUpdate.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

            Action invalidParentUpdate = () => Execute(connection, "UPDATE parent SET id = 3 WHERE id = 2;");
            invalidParentUpdate.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

            Action invalidParentDelete = () => Execute(connection, "DELETE FROM parent WHERE id = 2;");
            invalidParentDelete.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

            ScalarInteger(connection, "SELECT COUNT(*) FROM child;").Should().Be(1);
            ScalarInteger(connection, "SELECT parent_id FROM child WHERE id = 10;").Should().Be(2);
            ScalarInteger(connection, "SELECT COUNT(*) FROM parent;").Should().Be(2);
        }

        using var verifiedReopen = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var verifiedConnection = verifiedReopen.Connect();
        Execute(verifiedConnection, "PRAGMA foreign_keys = ON;");
        ScalarText(verifiedConnection, "SELECT code FROM parent WHERE id = 2;").Should().Be("two-updated");
        ScalarInteger(verifiedConnection, "SELECT parent_id FROM child WHERE id = 10;").Should().Be(2);
    }

    [Test]
    public void CorruptedPersistedForeignKeyCatalogFailsClosedDuringReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-file-catalog-corruption.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");
        }

        CorruptForeignKeyKeyword(fileSystem, path);

        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
    }

    [Test]
    public void FailedForeignKeyCatalogPublicationRecoversThePriorCommittedCatalog()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "foreign-key-file-catalog-recovery.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
            Execute(connection, "INSERT INTO parent VALUES (1);");

            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(() =>
                Execute(connection, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM parent;").Should().Be(1);
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'child';").Should().Be(0);
    }

    [Test]
    public void UnsupportedForeignKeyFormsAreRejectedWithoutPublishingAFileCatalogEntry()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-file-catalog-gating.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");

            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE TABLE composite(a INTEGER, b INTEGER, FOREIGN KEY(a, b) REFERENCES parent(id, id));"))!
                .Message.Should().Contain("Composite foreign key constraints are not supported");
            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE TABLE actions(parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE);"))!
                .Message.Should().Contain("Foreign key actions, MATCH, and deferral are not supported");
            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE TABLE qualified(parent_id INTEGER REFERENCES main.parent(id));"))!
                .Message.Should().Contain("Schema-qualified foreign keys are not supported");
            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE TABLE unnamed_parent_column(parent_id INTEGER REFERENCES parent);"))!
                .Message.Should().Contain("Foreign key references must name exactly one parent column");

            ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table';").Should().Be(1);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");
        Execute(reopenedConnection, "PRAGMA foreign_keys = ON;");
        Execute(reopenedConnection, "INSERT INTO parent VALUES (1);");
        Execute(reopenedConnection, "INSERT INTO child VALUES (1);");
        Assert.Throws<EmbeddedSqlException>(() => Execute(reopenedConnection, "INSERT INTO child VALUES (2);"))!
            .Message.Should().Be("FOREIGN KEY constraint failed");
    }

    private static void CorruptForeignKeyKeyword(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting);
        var headerBytes = new byte[SqliteDatabaseHeader.Size];
        file.Read(0, headerBytes).Should().Be(headerBytes.Length);
        var header = SqliteDatabaseHeader.Parse(headerBytes);
        var page = new byte[header.PageSize];
        file.Read(0, page).Should().Be(page.Length);

        var schema = SqliteTableLeafPageView.Parse(page, header.UsableSpace, isFirstPage: true);
        var childCell = schema.Cells.Single(cell =>
        {
            var values = SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding);
            return values[0].AsText() == "table" && values[1].AsText() == "child";
        });
        SqliteVarint.TryRead(page.AsSpan(childCell.Offset), out _, out var payloadLengthBytes).Should().BeTrue();
        SqliteVarint.TryRead(
            page.AsSpan(childCell.Offset + payloadLengthBytes),
            out _,
            out var rowIdBytes).Should().BeTrue();

        var payloadOffset = childCell.Offset + payloadLengthBytes + rowIdBytes;
        var payload = page.AsSpan(payloadOffset, childCell.Cell.LocalPayload.Length);
        var markerOffset = payload.IndexOf("REFERENCES"u8);
        markerOffset.Should().BeGreaterThanOrEqualTo(0);
        payload[markerOffset] = (byte)'X';

        file.Write(0, page);
        file.FlushToDisk();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static long ScalarInteger(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0).AsInteger();
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static string ScalarText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0).AsText();
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }
}
