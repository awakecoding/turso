using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedBackupPreflightTests
{
    [Test]
    public void ManagedBackupRejectsClosedMixedProviderDestinationBeforeOpeningIt()
    {
        using var source = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        using var destination = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
        source.Open();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupMixedProvidersNotSupported);

        destination.State.Should().Be(System.Data.ConnectionState.Closed);
        source.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
    }
}
