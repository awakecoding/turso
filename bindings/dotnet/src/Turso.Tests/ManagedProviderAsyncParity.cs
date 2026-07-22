using AwesomeAssertions;

namespace Turso.Tests;

public sealed class ManagedProviderAsyncParityTests
{
    [Test]
    public async Task ManagedTursoReaderAsyncOperationsHonorCancellationAndRemainUsable()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 UNION ALL SELECT 2;";
        var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        (await reader.GetFieldValueAsync<long>(0)).Should().Be(1);
        (await reader.IsDBNullAsync(0)).Should().BeFalse();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AssertCanceled(reader.ReadAsync(cancellation.Token));
        AssertCanceled(reader.NextResultAsync(cancellation.Token));
        AssertCanceled(reader.IsDBNullAsync(0, cancellation.Token));
        AssertCanceled(reader.GetFieldValueAsync<long>(0, cancellation.Token));

        (await reader.ReadAsync()).Should().BeTrue();

        await reader.DisposeAsync();
        reader.IsClosed.Should().BeTrue();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.ReadAsync());

        using var verification = connection.CreateCommand();
        verification.CommandText = "SELECT 3;";
        (await verification.ExecuteScalarAsync()).Should().Be(3L);
    }

    [Test]
    public async Task ManagedTursoAsyncCommandSurfacesPreparationErrors()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM missing_table;";

        Assert.ThrowsAsync<TursoException>(async () => await command.ExecuteReaderAsync());
    }

    private static void AssertCanceled(Task task)
    {
        task.IsCanceled.Should().BeTrue();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
    }
}
