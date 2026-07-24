using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedBackupSnapshotTests
{
    [Test]
    public void ManagedBackupCopiesSnapshotSchemaValuesAndHiddenRowids()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE data(integer_value INTEGER, real_value REAL, text_value TEXT, blob_value BLOB, null_value TEXT);");
        source.ExecuteNonQuery("CREATE TABLE aliases(id INTEGER PRIMARY KEY, value TEXT);");
        source.ExecuteNonQuery("CREATE TABLE generated(base INTEGER, doubled AS (base * 2) STORED);");
        source.ExecuteNonQuery("CREATE VIEW data_view AS SELECT integer_value, text_value FROM data;");
        source.ExecuteNonQuery("PRAGMA user_version = 123; PRAGMA application_id = 456;");
        destination.ExecuteNonQuery("PRAGMA user_version = 9; PRAGMA application_id = 10;");
        var sourceSchemaVersion = source.ExecuteScalar<long>("PRAGMA schema_version;");

        InsertData(source, 41, -17, 1.25, "first", [0, 1, 2], null);
        InsertData(source, 97, 42, -3.5, "second", [255, 4], "present");
        source.ExecuteNonQuery("INSERT INTO aliases(id, value) VALUES (71, 'rowid alias');");
        source.ExecuteNonQuery("INSERT INTO generated(base) VALUES (21);");

        source.BackupDatabase(destination);

        using (var reader = destination.ExecuteReader(
                   "SELECT rowid, integer_value, real_value, text_value, blob_value, null_value FROM data ORDER BY rowid;"))
        {
            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(41);
            reader.GetInt64(1).Should().Be(-17);
            reader.GetDouble(2).Should().Be(1.25);
            reader.GetString(3).Should().Be("first");
            ((byte[])reader.GetValue(4)).Should().Equal(0, 1, 2);
            reader.IsDBNull(5).Should().BeTrue();

            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(97);
            reader.GetInt64(1).Should().Be(42);
            reader.GetDouble(2).Should().Be(-3.5);
            reader.GetString(3).Should().Be("second");
            ((byte[])reader.GetValue(4)).Should().Equal(255, 4);
            reader.GetString(5).Should().Be("present");
            reader.Read().Should().BeFalse();
        }

        destination.ExecuteScalar<string>("SELECT text_value FROM data_view WHERE integer_value = 42;").Should().Be("second");
        destination.ExecuteScalar<long>("SELECT rowid FROM aliases WHERE id = 71;").Should().Be(71);
        destination.ExecuteScalar<long>("SELECT doubled FROM generated WHERE base = 21;").Should().Be(42);
        destination.ExecuteScalar<long>("PRAGMA schema_version;").Should().Be(sourceSchemaVersion);
        destination.ExecuteScalar<long>("PRAGMA user_version;").Should().Be(123);
        destination.ExecuteScalar<long>("PRAGMA application_id;").Should().Be(456);

        InsertData(destination, 123, 9, 0, "after backup", [], null);
        destination.ExecuteScalar<long>("SELECT rowid FROM data WHERE text_value = 'after backup';").Should().Be(123);
    }

    [Test]
    public void ManagedBackupReplacesNonemptyDestination()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        source.ExecuteNonQuery("CREATE TABLE sqliteX(value TEXT); INSERT INTO sqliteX VALUES ('valid prefix');");
        destination.ExecuteNonQuery("CREATE TABLE sqliteY(value TEXT); INSERT INTO sqliteY VALUES ('destination');");

        source.BackupDatabase(destination);

        destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
        destination.ExecuteScalar<string>("SELECT value FROM sqliteX;").Should().Be("valid prefix");
        destination.Invoking(connection => connection.ExecuteScalar<string>("SELECT value FROM sqliteY;"))
            .Should().Throw<SqliteException>().WithMessage("*no such table: sqliteY*");
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(2);
    }

    [Test]
    public void ManagedBackupRollsBackDestinationAndReleasesSourceSnapshotOnCopyFailure()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE all_rowid_aliases(rowid TEXT, _rowid_ TEXT, oid TEXT);");
        source.ExecuteNonQuery("INSERT INTO all_rowid_aliases VALUES ('a', 'b', 'c');");
        source.ExecuteNonQuery("PRAGMA user_version = 123;");
        destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('destination');");
        destination.ExecuteNonQuery("PRAGMA user_version = 77;");

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should().Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupRowidNotAccessible("all_rowid_aliases"));

        destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("destination");
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(1);
        destination.ExecuteScalar<long>("PRAGMA user_version;").Should().Be(77);
        source.ExecuteScalar<string>("SELECT rowid FROM all_rowid_aliases;").Should().Be("a");
    }

    [Test]
    public void ManagedBackupRejectsActiveDestinationTransactionBeforeSnapshot()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("PRAGMA foreign_keys = ON;");

        using var transaction = destination.BeginTransaction();
        var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

        exception!.SqliteErrorCode.Should().Be(5);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        destination.ExecuteScalar<long>("PRAGMA foreign_keys;").Should().Be(1);
        transaction.Rollback();
    }

    [Test]
    public void ManagedBackupMapsRawDestinationTransactionToBusyWithoutChangingIt()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('destination');");
        destination.ExecuteNonQuery("BEGIN;");

        var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

        exception!.SqliteErrorCode.Should().Be(5);
        destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("destination");
        destination.ExecuteNonQuery("ROLLBACK;");
    }

    [Test]
    public void ManagedBackupCopiesActiveSourceTransactionWithoutCompletingIt()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('committed');");

        using var transaction = source.BeginTransaction();
        source.ExecuteNonQuery("INSERT INTO source_data VALUES ('uncommitted');");

        source.BackupDatabase(destination);

        source.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
        transaction.Rollback();
        source.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(1);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
    }

    [Test]
    public void ManagedBackupPersistsActiveSourceSnapshotAfterSourceRollbackAndReopen()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            {
                source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('committed');");
                using var transaction = source.BeginTransaction();
                source.ExecuteNonQuery("INSERT INTO source_data VALUES ('rolled back later');");

                source.BackupDatabase(destination);
                transaction.Rollback();
            }

            using var reopenedSource = OpenManagedConnection(sourcePath);
            using var reopenedDestination = OpenManagedConnection(destinationPath);
            reopenedSource.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(1);
            reopenedDestination.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedBackupCopiesWhileSourceReaderRemainsActive()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        using var reader = source.ExecuteReader("SELECT value FROM source_data;");
        reader.Read().Should().BeTrue();

        source.BackupDatabase(destination);

        destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
        reader.GetString(0).Should().Be("source");
    }

    [Test]
    public void ManagedBackupRejectsOpenDestinationReaderWithoutChangingIt()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("CREATE TABLE destination_data(value TEXT); INSERT INTO destination_data VALUES ('destination');");
        using var reader = destination.ExecuteReader("SELECT value FROM destination_data;");
        reader.Read().Should().BeTrue();

        var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

        exception!.SqliteErrorCode.Should().Be(5);
        reader.GetString(0).Should().Be("destination");
        reader.Dispose();
        destination.ExecuteScalar<string>("SELECT value FROM destination_data;").Should().Be("destination");
    }

    [Test]
    public void ManagedBackupAllowsUnrelatedActiveAttachments()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        var sourceAttachmentPath = CreateManagedDatabasePath();
        var destinationAttachmentPath = CreateManagedDatabasePath();
        try
        {
            using var source = OpenManagedConnection(sourcePath);
            using var destination = OpenManagedConnection(destinationPath);
            source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
            destination.ExecuteNonQuery("CREATE TABLE destination_data(value TEXT); INSERT INTO destination_data VALUES ('destination');");
            source.ExecuteNonQuery($"ATTACH DATABASE '{sourceAttachmentPath}' AS source_aux;");
            destination.ExecuteNonQuery($"ATTACH DATABASE '{destinationAttachmentPath}' AS destination_aux;");
            source.ExecuteNonQuery("CREATE TABLE source_aux.marker(value TEXT); INSERT INTO source_aux.marker VALUES ('source attachment');");
            destination.ExecuteNonQuery("CREATE TABLE destination_aux.marker(value TEXT); INSERT INTO destination_aux.marker VALUES ('destination attachment');");

            source.BackupDatabase(destination);

            destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
            source.ExecuteScalar<string>("SELECT value FROM source_aux.marker;").Should().Be("source attachment");
            destination.ExecuteScalar<string>("SELECT value FROM destination_aux.marker;").Should().Be("destination attachment");
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
            DeleteManagedDatabase(sourceAttachmentPath);
            DeleteManagedDatabase(destinationAttachmentPath);
        }
    }

    [TestCase("main", "missing")]
    [TestCase("missing", "main")]
    public void ManagedBackupRejectsUnknownDatabaseNamesWithoutChangingDestination(
        string destinationName,
        string sourceName)
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('destination');");

        var exception = Assert.Throws<SqliteException>(
            () => source.BackupDatabase(destination, destinationName, sourceName));

        exception!.SqliteErrorCode.Should().Be(1);
        exception.Message.Should().Contain("no such database: missing");
        destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("destination");
    }

    [Test]
    public void ManagedBackupAtomicallyReplacesAndPersistsAFileBackedDestination()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            {
                source.ExecuteNonQuery("CREATE TABLE data(value TEXT, payload BLOB);");
                destination.ExecuteNonQuery(
                "CREATE TABLE old_data(value TEXT);"
                + " INSERT INTO old_data VALUES ('old');"
                + " CREATE TABLE older_data(value TEXT);");
                var sourceSchemaVersion = source.ExecuteScalar<long>("PRAGMA schema_version;");
                using var command = source.CreateCommand();
                command.CommandText = "INSERT INTO data(rowid, value, payload) VALUES (9, 'persisted', $payload);";
                command.Parameters.Add("$payload", SqliteType.Blob).Value = new byte[] { 6, 7, 8 };
                command.ExecuteNonQuery();

                source.BackupDatabase(destination);
                destination.ExecuteScalar<long>("PRAGMA schema_version;").Should().Be(sourceSchemaVersion);
            }

            using var reopened = OpenManagedConnection(destinationPath);
            using var reader = reopened.ExecuteReader("SELECT rowid, value, payload FROM data;");
            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(9);
            reader.GetString(1).Should().Be("persisted");
            ((byte[])reader.GetValue(2)).Should().Equal(6, 7, 8);
            reopened.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'old_data';").Should().Be(0);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedBackupFailurePreservesFileDestinationAcrossReopen()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            {
                source.ExecuteNonQuery("CREATE TABLE inaccessible(rowid TEXT, _rowid_ TEXT, oid TEXT);");
                source.ExecuteNonQuery("INSERT INTO inaccessible VALUES ('a', 'b', 'c');");
                destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('durable');");

                source.Invoking(connection => connection.BackupDatabase(destination))
                    .Should().Throw<NotSupportedException>()
                    .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupRowidNotAccessible("inaccessible"));

                destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("durable");
            }

            using var reopened = OpenManagedConnection(destinationPath);
            reopened.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("durable");
            reopened.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'inaccessible';").Should().Be(0);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedBackupRejectsDistinctConnectionsToTheSameFile()
    {
        var path = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(path))
            using (var destination = OpenManagedConnection(path))
            {
                source.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('same file');");

                var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

                exception!.SqliteErrorCode.Should().Be(1);
                exception.Message.Should().Contain("source and destination must be distinct");
                source.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("same file");
            }

            using var reopened = OpenManagedConnection(path);
            reopened.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("same file");
        }
        finally
        {
            DeleteManagedDatabase(path);
        }
    }

    [Test]
    public void ManagedIncrementalBlobWritesThroughTheManagedConnection()
    {
        using var connection = OpenManagedConnection();
        connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data VALUES (X'0102');");

        using (var blob = new SqliteBlob(connection, "data", "value", 1))
            blob.Write([3], 0, 1);

        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(3, 2);
    }

    private static SqliteConnection OpenManagedConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenManagedConnection(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void InsertData(
        SqliteConnection connection,
        long rowid,
        long integerValue,
        double realValue,
        string textValue,
        byte[] blobValue,
        string? nullValue)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO data(rowid, integer_value, real_value, text_value, blob_value, null_value)
            VALUES ($rowid, $integer_value, $real_value, $text_value, $blob_value, $null_value);
            """;
        command.Parameters.Add("$rowid", SqliteType.Integer).Value = rowid;
        command.Parameters.Add("$integer_value", SqliteType.Integer).Value = integerValue;
        command.Parameters.Add("$real_value", SqliteType.Real).Value = realValue;
        command.Parameters.Add("$text_value", SqliteType.Text).Value = textValue;
        command.Parameters.Add("$blob_value", SqliteType.Blob).Value = blobValue;
        command.Parameters.Add("$null_value", SqliteType.Text).Value = (object?)nullValue ?? DBNull.Value;
        command.ExecuteNonQuery();
    }

    private static string CreateManagedDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-backup-snapshot-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"backup-{Guid.NewGuid():N}.db");
    }

    private static void DeleteManagedDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
