using AwesomeAssertions;

namespace Turso.Tests;

public sealed class ManagedProviderReaderLifecycleTests
{
    [Test]
    public void ClosingAndReopeningManagedConnectionPermanentlyClosesActiveTursoReader()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();

        connection.Close();
        connection.Open();

        reader.IsClosed.Should().BeTrue();
        reader.Invoking(static value => value.Read()).Should().Throw<InvalidOperationException>();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);").Should().Be(0);
    }
}
