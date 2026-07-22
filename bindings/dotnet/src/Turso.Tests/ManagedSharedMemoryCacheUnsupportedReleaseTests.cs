using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedSharedMemoryCacheUnsupportedReleaseTests
{
    [Test]
    public void SqliteFacadeRejectsManagedSharedMemoryCacheBeforeOpening()
    {
        using var connection = new SqliteConnection(
            "Data Source=managed-shared-memory;Mode=Memory;Cache=Shared;Local Provider=Managed");

        connection.Invoking(static value => value.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("Cache=Shared is not supported when Local Provider=Managed because managed connections do not share page caches.");
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }
}
