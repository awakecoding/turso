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

        InsertData(destination, 123, 9, 0, "after backup", [], null);
        destination.ExecuteScalar<long>("SELECT rowid FROM data WHERE text_value = 'after backup';").Should().Be(123);
    }

    [Test]
    public void ManagedBackupRejectsNonemptyDestinationWithoutChangingIt()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("CREATE TABLE destination_data(value TEXT); INSERT INTO destination_data VALUES ('destination');");

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should().Throw<InvalidOperationException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupDestinationMustBeEmpty);

        destination.ExecuteScalar<string>("SELECT value FROM destination_data;").Should().Be("destination");
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(1);
    }

    [Test]
    public void ManagedBackupRollsBackDestinationAndReleasesSourceSnapshotOnCopyFailure()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE all_rowid_aliases(rowid TEXT, _rowid_ TEXT, oid TEXT);");
        source.ExecuteNonQuery("INSERT INTO all_rowid_aliases VALUES ('a', 'b', 'c');");

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should().Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupRowidNotAccessible("all_rowid_aliases"));

        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        source.ExecuteScalar<string>("SELECT rowid FROM all_rowid_aliases;").Should().Be("a");
    }

    [Test]
    public void ManagedBackupRejectsActiveDestinationTransactionBeforeSnapshot()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");

        using var transaction = destination.BeginTransaction();
        var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

        exception!.SqliteErrorCode.Should().Be(5);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        transaction.Rollback();
    }

    [Test]
    public void ManagedBackupPersistsAFileBackedSnapshotWithoutNativeFallback()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            {
                source.ExecuteNonQuery("CREATE TABLE data(value TEXT, payload BLOB);");
                using var command = source.CreateCommand();
                command.CommandText = "INSERT INTO data(rowid, value, payload) VALUES (9, 'persisted', $payload);";
                command.Parameters.Add("$payload", SqliteType.Blob).Value = new byte[] { 6, 7, 8 };
                command.ExecuteNonQuery();

                source.BackupDatabase(destination);
            }

            using var reopened = OpenManagedConnection(destinationPath);
            using var reader = reopened.ExecuteReader("SELECT rowid, value, payload FROM data;");
            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(9);
            reader.GetString(1).Should().Be("persisted");
            ((byte[])reader.GetValue(2)).Should().Equal(6, 7, 8);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedIncrementalBlobRemainsExplicitlyRejected()
    {
        using var connection = OpenManagedConnection();
        connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data VALUES (X'0102');");

        connection.Invoking(connection => new SqliteBlob(connection, "data", "value", 1))
            .Should().Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedIncrementalBlobNotSupported);
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
