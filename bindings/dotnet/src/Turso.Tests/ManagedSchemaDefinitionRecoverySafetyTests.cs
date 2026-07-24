using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

public sealed class ManagedSchemaDefinitionRecoverySafetyTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void StatementLevelAfterTriggerCommitsThroughWalFailureAndRunsAfterReopen()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "trigger-recovery.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE events(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE audit(note TEXT);");

            faults.FailNext(FileSystemOperation.SetLength);
            Assert.Throws<EmbeddedPostCommitMaintenanceException>(() => Execute(
                connection,
                "CREATE TRIGGER events_audit AFTER INSERT ON events BEGIN INSERT INTO audit VALUES ('created'); END;"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO events VALUES (1);");
        Scalar(reopenedConnection, "SELECT note FROM audit;").Should().Be("created");
    }

    [Test]
    public void RuntimeDependentViewIsRejectedBeforePublishingCatalogOrPages()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "unsupported-view-definition.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entries(value INTEGER);");
            Execute(connection, "CREATE TABLE audit(value INTEGER);");
            Execute(connection, "INSERT INTO entries VALUES (7);");

            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE VIEW function_entries AS SELECT abs(value) AS value FROM entries;"))!
                .Message.Should().Contain("function");
            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE TRIGGER function_trigger AFTER INSERT ON entries BEGIN INSERT INTO audit VALUES (abs(1)); END;"))!
                .Message.Should().Contain("function");

            ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';").Should().Be(0);
            ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';").Should().Be(0);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT value FROM entries;").Should().Be(7);
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';").Should().Be(0);
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';").Should().Be(0);
    }

    [Test]
    public void CorruptedPersistedTriggerDefinitionFailsClosedDuringReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "corrupted-trigger-definition.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE events(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE audit(note TEXT);");
            Execute(
                connection,
                "CREATE TRIGGER events_audit AFTER INSERT ON events BEGIN INSERT INTO audit VALUES ('created'); END;");
        }

        CorruptTriggerSql(fileSystem, path);

        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
    }

    [Test]
    public void EncryptedReadOnlyReopenExecutesPersistedTriggerAndRefusesMutation()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "encrypted-read-only-trigger.db";

        using (var encryption = TursoEncryptionOptions.FromHex(TursoEncryptionCipher.Aes256Gcm, Aes256Key))
        using (var database = EmbeddedDatabase.OpenFile(path, new TursoEncryptionFileSystem(fileSystem, encryption)))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE events(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE audit(note TEXT);");
            Execute(
                connection,
                "CREATE TRIGGER events_audit AFTER INSERT ON events BEGIN INSERT INTO audit VALUES ('created'); END;");
            Execute(connection, "INSERT INTO events VALUES (1);");
        }

        using var reopenEncryption = TursoEncryptionOptions.FromHex(TursoEncryptionCipher.Aes256Gcm, Aes256Key);
        using var reopened = EmbeddedDatabase.OpenFile(
            path,
            new TursoEncryptionFileSystem(fileSystem, reopenEncryption),
            readOnly: true);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT note FROM audit;").Should().Be("created");

        Assert.Throws<EmbeddedSqlException>(() => Execute(reopenedConnection, "INSERT INTO events VALUES (2);"))!
            .Message.Should().Be("attempt to write a readonly database");
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM events;").Should().Be(1);
    }

    [Test]
    public void OversizedEncryptedSchemaSqlIsRejectedWithoutPublishingCatalogOrPages()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "oversized-encrypted-schema.db";
        var oversizedDefault = new string('x', 5000);

        using (var encryption = TursoEncryptionOptions.FromHex(TursoEncryptionCipher.Aes256Gcm, Aes256Key))
        using (var encryptedFileSystem = new TursoEncryptionFileSystem(fileSystem, encryption))
        using (var database = EmbeddedDatabase.OpenFile(path, encryptedFileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE durable(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO durable VALUES (1, 'before');");

            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                $"CREATE TABLE oversized(value TEXT DEFAULT '{oversizedDefault}');"))!
                .Message.Should().Contain("schema overflow pages are not supported");

            ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'oversized';").Should().Be(0);
            Scalar(connection, "SELECT value FROM durable WHERE id = 1;").Should().Be("before");
        }

        using var reopenEncryption = TursoEncryptionOptions.FromHex(TursoEncryptionCipher.Aes256Gcm, Aes256Key);
        using var reopenedFileSystem = new TursoEncryptionFileSystem(fileSystem, reopenEncryption);
        using var reopened = EmbeddedDatabase.OpenFile(path, reopenedFileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'oversized';").Should().Be(0);
        Scalar(reopenedConnection, "SELECT value FROM durable WHERE id = 1;").Should().Be("before");
    }

    private static void CorruptTriggerSql(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting);
        var headerBytes = new byte[SqliteDatabaseHeader.Size];
        file.Read(0, headerBytes).Should().Be(headerBytes.Length);
        var header = SqliteDatabaseHeader.Parse(headerBytes);
        var page = new byte[header.PageSize];
        file.Read(0, page).Should().Be(page.Length);

        var schema = SqliteTableLeafPageView.Parse(page, header.UsableSpace, isFirstPage: true);
        var triggerCell = schema.Cells.Single(cell =>
        {
            var values = SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding);
            return values[0].AsText() == "trigger";
        });
        SqliteVarint.TryRead(page.AsSpan(triggerCell.Offset), out _, out var payloadLengthBytes).Should().BeTrue();
        SqliteVarint.TryRead(
            page.AsSpan(triggerCell.Offset + payloadLengthBytes),
            out _,
            out var rowIdBytes).Should().BeTrue();

        var payloadOffset = triggerCell.Offset + payloadLengthBytes + rowIdBytes;
        var payload = page.AsSpan(payloadOffset, triggerCell.Cell.LocalPayload.Length);
        var markerOffset = payload.IndexOf("CREATE TRIGGER"u8);
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

    private static string Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static long ScalarInteger(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }
}
