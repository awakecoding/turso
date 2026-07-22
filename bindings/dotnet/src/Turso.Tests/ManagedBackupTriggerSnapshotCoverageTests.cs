using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedBackupTriggerSnapshotCoverageTests
{
    [Test]
    public void ManagedBackupCopiesTriggersAfterRestoringRows()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("""
            CREATE TABLE event_data(value TEXT);
            CREATE TABLE audit(value TEXT);
            INSERT INTO event_data VALUES ('before backup');
            CREATE TRIGGER event_data_audit AFTER INSERT ON event_data
            BEGIN
                INSERT INTO audit VALUES ('inserted');
            END;
            """);

        source.BackupDatabase(destination);

        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM event_data;").Should().Be(1);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM audit;").Should().Be(0);
        destination.ExecuteNonQuery("INSERT INTO event_data VALUES ('after backup');");
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM audit;").Should().Be(1);
        destination.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'event_data_audit';")
            .Should()
            .Contain("CREATE TRIGGER event_data_audit AFTER INSERT ON event_data");
    }

    [Test]
    public void ManagedBackupRollsBackCopiedTriggerSchemaWhenALaterTableCannotPreserveRowids()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("""
            CREATE TABLE event_data(value TEXT);
            CREATE TABLE audit(value TEXT);
            INSERT INTO event_data VALUES ('source row');
            CREATE TRIGGER event_data_audit AFTER INSERT ON event_data
            BEGIN
                INSERT INTO audit VALUES ('inserted');
            END;
            CREATE TABLE inaccessible_rowid(rowid TEXT, _rowid_ TEXT, oid TEXT);
            INSERT INTO inaccessible_rowid VALUES ('a', 'b', 'c');
            """);

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should().Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupRowidNotAccessible("inaccessible_rowid"));

        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';").Should().Be(0);
        source.ExecuteScalar<string>("SELECT value FROM event_data;").Should().Be("source row");
    }

    private static SqliteConnection OpenManagedConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }
}
