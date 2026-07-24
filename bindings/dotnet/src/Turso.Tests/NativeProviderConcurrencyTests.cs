using System.Diagnostics;
using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

[NonParallelizable]
public sealed class NativeProviderConcurrencyTests
{
    private const string LongRunningQuery =
        """
        SELECT sum(a.value + b.value + c.value + d.value)
        FROM cancellation_values AS a
        CROSS JOIN cancellation_values AS b
        CROSS JOIN cancellation_values AS c
        CROSS JOIN cancellation_values AS d;
        """;

    [Test]
    public async Task CancellationTokenInterruptsNativeExecutionAndLeavesConnectionUsable()
    {
        using var connection = OpenNativeConnection();
        SeedCancellationValues(connection);
        using var command = connection.CreateCommand();
        command.CommandText = LongRunningQuery;
        using var cancellation = new CancellationTokenSource();

        var execution = command.ExecuteScalarAsync(cancellation.Token);
        await Task.Delay(25);
        await cancellation.CancelAsync();

        await AssertCanceledAsync(execution);
        command.CommandText = "SELECT 1;";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Test]
    public async Task CommandCancelInterruptsNativeExecution()
    {
        using var connection = OpenNativeConnection();
        SeedCancellationValues(connection);
        using var command = connection.CreateCommand();
        command.CommandText = LongRunningQuery;

        var execution = command.ExecuteScalarAsync();
        await Task.Delay(25);
        command.Cancel();

        await AssertCanceledAsync(execution);
    }

    [Test]
    public async Task ConcurrentCommandsOnOneNativeConnectionAreSerialized()
    {
        using var connection = OpenNativeConnection();
        var executions = Enumerable.Range(1, 32).Select(async value =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {value};";
            return await command.ExecuteScalarAsync();
        });

        var results = await Task.WhenAll(executions).WaitAsync(TimeSpan.FromSeconds(10));
        results.Should().Equal(Enumerable.Range(1, 32).Select(value => (object)(long)value));
    }

    [Test]
    public async Task NativeCallbackReentrancyFailsInsteadOfDeadlocking()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
        connection.Open();
        connection.CreateFunction<long>("reenter", () =>
        {
            using var nested = connection.CreateCommand();
            nested.CommandText = "SELECT 1;";
            return (long)nested.ExecuteScalar()!;
        });
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT reenter();";

        var execute = async () => await command.ExecuteScalarAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await execute.Should().ThrowAsync<SqliteException>()
            .WithMessage("*does not support reentrant operations*");
    }

    [Test]
    public async Task DisposingReaderCancelsAndWaitsForNativeRead()
    {
        using var connection = OpenNativeConnection();
        SeedCancellationValues(connection);
        using var command = connection.CreateCommand();
        command.CommandText = LongRunningQuery;
        var reader = await command.ExecuteReaderAsync();

        var read = reader.ReadAsync();
        await Task.Delay(25);
        var dispose = Task.Run(reader.Dispose);

        await AssertCanceledAsync(read);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        using var next = connection.CreateCommand();
        next.CommandText = "SELECT 1;";
        next.ExecuteScalar().Should().Be(1L);
    }

    [Test]
    public async Task ClosingConnectionCancelsAndWaitsForNativeRead()
    {
        using var connection = OpenNativeConnection();
        SeedCancellationValues(connection);
        using var command = connection.CreateCommand();
        command.CommandText = LongRunningQuery;
        var reader = await command.ExecuteReaderAsync();

        var read = reader.ReadAsync();
        await Task.Delay(25);
        var close = Task.Run(connection.Close);

        await AssertCanceledAsync(read);
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
        reader.IsClosed.Should().BeTrue();
    }

    [Test]
    public async Task RepeatedCommandCancellationDoesNotMissActiveNativeReads()
    {
        using var connection = OpenNativeConnection();
        SeedCancellationValues(connection);

        for (var iteration = 0; iteration < 16; iteration++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = LongRunningQuery;
            using var reader = await command.ExecuteReaderAsync();

            var read = reader.ReadAsync();
            if (iteration % 2 != 0)
                await Task.Delay(5);
            command.Cancel();

            await AssertCanceledAsync(read);
        }

        using var next = connection.CreateCommand();
        next.CommandText = "SELECT 1;";
        next.ExecuteScalar().Should().Be(1L);
    }

    [Test]
    public void NativeCommandTimeoutControlsBusyWait()
    {
        var path = Path.Combine(Path.GetTempPath(), $"turso-native-busy-{Guid.NewGuid():N}.db");
        try
        {
            using var first = OpenNativeConnection(path);
            using var second = OpenNativeConnection(path);
            using (var setup = first.CreateCommand())
            {
                setup.CommandText = "CREATE TABLE data(value INTEGER);";
                setup.ExecuteNonQuery();
            }

            using var transaction = first.BeginTransaction();
            using (var insert = first.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO data VALUES (1);";
                insert.ExecuteNonQuery();
            }

            using var blocked = second.CreateCommand();
            blocked.CommandTimeout = 1;
            blocked.CommandText = "INSERT INTO data VALUES (2);";
            var stopwatch = Stopwatch.StartNew();

            blocked.Invoking(command => command.ExecuteNonQuery()).Should().Throw<TursoException>();
            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(
                         Path.GetDirectoryName(path)!,
                         Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [Test]
    public async Task ZeroNativeCommandTimeoutWaitsUntilLockIsReleased()
    {
        var path = Path.Combine(Path.GetTempPath(), $"turso-native-busy-{Guid.NewGuid():N}.db");
        try
        {
            using var first = OpenNativeConnection(path);
            using var second = OpenNativeConnection(path);
            using (var setup = first.CreateCommand())
            {
                setup.CommandText = "CREATE TABLE data(value INTEGER);";
                setup.ExecuteNonQuery();
            }

            using var transaction = first.BeginTransaction();
            using (var insert = first.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO data VALUES (1);";
                insert.ExecuteNonQuery();
            }

            using var blocked = second.CreateCommand();
            blocked.CommandTimeout = 0;
            blocked.CommandText = "INSERT INTO data VALUES (2);";
            var execution = blocked.ExecuteNonQueryAsync();

            await Task.Delay(250);
            execution.IsCompleted.Should().BeFalse();
            transaction.Rollback();
            (await execution.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(
                         Path.GetDirectoryName(path)!,
                         Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    private static TursoConnection OpenNativeConnection(string path = ":memory:")
    {
        var connection = new TursoConnection($"Data Source={path};Local Provider=Native");
        connection.Open();
        return connection;
    }

    private static void SeedCancellationValues(TursoConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE cancellation_values(value INTEGER);";
        command.ExecuteNonQuery();
        command.CommandText =
            "INSERT INTO cancellation_values VALUES "
            + string.Join(", ", Enumerable.Range(0, 100).Select(value => $"({value})"))
            + ";";
        command.ExecuteNonQuery();
    }

    private static async Task AssertCanceledAsync(Task task)
    {
        var exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
        exception.Should().NotBeNull();
        task.IsCanceled.Should().BeTrue();
    }
}
