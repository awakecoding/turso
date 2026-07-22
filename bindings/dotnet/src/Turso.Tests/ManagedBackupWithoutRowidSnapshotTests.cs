using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedBackupWithoutRowidSnapshotTests
{
    [Test]
    public void ManagedBackupCopiesWithoutRowidTablesWithCompositePrimaryKeys()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("""
            CREATE TABLE account_value(
                account_id INTEGER NOT NULL,
                value_key TEXT NOT NULL,
                payload BLOB,
                PRIMARY KEY(account_id, value_key)
            ) WITHOUT ROWID;
            INSERT INTO account_value VALUES (1, 'a', X'0102');
            INSERT INTO account_value VALUES (2, 'b', X'FE');
            """);

        source.BackupDatabase(destination);

        using var reader = destination.ExecuteReader(
            "SELECT account_id, value_key, payload FROM account_value ORDER BY account_id;");
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetString(1).Should().Be("a");
        ((byte[])reader.GetValue(2)).Should().Equal(1, 2);
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        reader.GetString(1).Should().Be("b");
        ((byte[])reader.GetValue(2)).Should().Equal(254);
        reader.Read().Should().BeFalse();
    }

    [Test]
    public void ManagedBackupRollsBackWithoutRowidRowsWhenALaterTableCannotPreserveRowids()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("""
            CREATE TABLE account_value(
                account_id INTEGER NOT NULL,
                value_key TEXT NOT NULL,
                PRIMARY KEY(account_id, value_key)
            ) WITHOUT ROWID;
            INSERT INTO account_value VALUES (1, 'kept-only-in-source');
            CREATE TABLE inaccessible_rowid(rowid TEXT, _rowid_ TEXT, oid TEXT);
            INSERT INTO inaccessible_rowid VALUES ('a', 'b', 'c');
            """);

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should().Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupRowidNotAccessible("inaccessible_rowid"));

        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        source.ExecuteScalar<string>(
                "SELECT value_key FROM account_value WHERE account_id = 1;")
            .Should()
            .Be("kept-only-in-source");
    }

    [Test]
    public void ManagedBackupPreservesRowidsWhenSchemaTextMentionsWithoutRowid()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("""
            CREATE TABLE ordinary_data(
                value TEXT DEFAULT 'WITHOUT ROWID'
            );
            INSERT INTO ordinary_data(rowid) VALUES (73);
            """);

        source.BackupDatabase(destination);

        destination.ExecuteScalar<long>("SELECT rowid FROM ordinary_data;").Should().Be(73);
    }

    private static SqliteConnection OpenManagedConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }
}
