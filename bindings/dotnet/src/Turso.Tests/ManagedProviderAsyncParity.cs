using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedProviderAsyncParityTests
{
    [Test]
    public async Task ManagedSqliteReaderAsyncOperationsHonorCancellationAndRemainUsable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 UNION ALL SELECT 2;";
        using var reader = await command.ExecuteReaderAsync();

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
    }

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

    [Test]
    public async Task ManagedSqliteReaderAsyncOperationsReturnFaultedTasksAfterDisposal()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        var reader = await command.ExecuteReaderAsync();
        await reader.DisposeAsync();

        Task<bool>? read = null;
        Assert.DoesNotThrow(() => read = reader.ReadAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await read!);

        Task<bool>? nextResult = null;
        Assert.DoesNotThrow(() => nextResult = reader.NextResultAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await nextResult!);

        Task<bool>? isDbNull = null;
        Assert.DoesNotThrow(() => isDbNull = reader.IsDBNullAsync(0));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await isDbNull!);

        Task<long>? fieldValue = null;
        Assert.DoesNotThrow(() => fieldValue = reader.GetFieldValueAsync<long>(0));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await fieldValue!);

        using var verification = connection.CreateCommand();
        verification.CommandText = "SELECT 2;";
        (await verification.ExecuteScalarAsync()).Should().Be(2L);
    }

    private static void AssertCanceled(Task task)
    {
        task.IsCanceled.Should().BeTrue();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
    }
}
