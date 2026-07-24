using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class LocalBatchTests
{
    [TestCase("Managed")]
    [TestCase("Native")]
    public void TursoBatchReaderExecutesCommandsSequentially(string provider)
    {
        using var connection = OpenTursoConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var batch = (TursoBatch)connection.CreateBatch();
        var insert = (TursoBatchCommand)batch.CreateBatchCommand();
        insert.CommandText = "INSERT INTO data VALUES ($value);";
        insert.Parameters.AddWithValue("$value", 1);
        batch.BatchCommands.Add(insert);

        var select = (TursoBatchCommand)batch.CreateBatchCommand();
        select.CommandText = "SELECT value FROM data WHERE value = $value;";
        select.Parameters.AddWithValue("$value", 1);
        batch.BatchCommands.Add(select);

        var update = (TursoBatchCommand)batch.CreateBatchCommand();
        update.CommandText = "UPDATE data SET value = $next WHERE value = $current;";
        update.Parameters.AddWithValue("$next", 2);
        update.Parameters.AddWithValue("$current", 1);
        batch.BatchCommands.Add(update);

        using var reader = batch.ExecuteReader();
        reader.FieldCount.Should().Be(0);
        insert.RecordsAffected.Should().Be(1);

        reader.NextResult().Should().BeTrue();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.Read().Should().BeFalse();

        reader.NextResult().Should().BeTrue();
        reader.FieldCount.Should().Be(0);
        update.RecordsAffected.Should().Be(1);
        reader.RecordsAffected.Should().Be(2);
        reader.NextResult().Should().BeFalse();

        select.RecordsAffected.Should().Be(0);
        Scalar<long>(connection, "SELECT value FROM data;").Should().Be(2);
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public void TursoBatchScalarDrainsTrailingCommands(string provider)
    {
        using var connection = OpenTursoConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var batch = (TursoBatch)connection.CreateBatch();
        var firstInsert = (TursoBatchCommand)batch.CreateBatchCommand();
        firstInsert.CommandText = "INSERT INTO data VALUES ($value);";
        firstInsert.Parameters.AddWithValue("$value", 7);
        batch.BatchCommands.Add(firstInsert);

        var select = new TursoBatchCommand("SELECT count(*) FROM data;");
        batch.BatchCommands.Add(select);

        var trailingInsert = (TursoBatchCommand)batch.CreateBatchCommand();
        trailingInsert.CommandText = "INSERT INTO data VALUES ($value);";
        trailingInsert.Parameters.AddWithValue("$value", 9);
        batch.BatchCommands.Add(trailingInsert);

        batch.ExecuteScalar().Should().Be(1L);
        firstInsert.RecordsAffected.Should().Be(1);
        trailingInsert.RecordsAffected.Should().Be(1);
        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(2);
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public void TursoBatchDoesNotImplicitlyRollbackEarlierCommands(string provider)
    {
        using var connection = OpenTursoConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var batch = (TursoBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (1);"));
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO missing VALUES (2);"));

        Assert.Throws<TursoException>(() => batch.ExecuteNonQuery());
        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(1);
    }

    [Test]
    public void TursoBatchUsesAndValidatesTransactions()
    {
        using var connection = OpenTursoConnection("Managed");
        using var otherConnection = OpenTursoConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using (var transaction = connection.BeginTransaction())
        {
            using var batch = (TursoBatch)connection.CreateBatch();
            batch.Transaction = transaction;
            batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (1);"));
            batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (2);"));

            batch.ExecuteNonQuery().Should().Be(2);
            transaction.Rollback();
        }

        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(0);

        using var otherTransaction = otherConnection.BeginTransaction();
        using var mismatched = (TursoBatch)connection.CreateBatch();
        mismatched.Transaction = otherTransaction;
        mismatched.BatchCommands.Add(new TursoBatchCommand("SELECT 1;"));
        mismatched.Invoking(static value => value.ExecuteNonQuery())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("The transaction is not associated with the batch's connection.");

        otherTransaction.Rollback();
        using var detached = (TursoBatch)connection.CreateBatch();
        detached.BatchCommands.Add(new TursoBatchCommand("SELECT 1;"));
        using var activeTransaction = connection.BeginTransaction();
        detached.Invoking(static value => value.ExecuteNonQuery())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("The batch must be associated with the connection's active transaction.");
    }

    [Test]
    public void TursoBatchCancelStopsBeforeTheNextCommand()
    {
        using var connection = OpenTursoConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var batch = (TursoBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new TursoBatchCommand("SELECT 1;"));
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (1);"));

        var reader = batch.ExecuteReader();
        reader.Read().Should().BeTrue();
        batch.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.NextResult());
        reader.Dispose();

        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(0);
    }

    [Test]
    public void DisposingTursoBatchCancelsItsActiveReader()
    {
        using var connection = OpenTursoConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        var batch = (TursoBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new TursoBatchCommand("SELECT 1;"));
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (1);"));
        using var reader = batch.ExecuteReader();
        reader.Read().Should().BeTrue();

        batch.Dispose();

        Assert.Throws<OperationCanceledException>(() => reader.NextResult());
        reader.Dispose();
        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(0);
        batch.Invoking(static value => value.ExecuteNonQuery())
            .Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task TursoBatchAsyncExecutionHonorsPreCanceledToken()
    {
        using var connection = OpenTursoConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var batch = (TursoBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (1);"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var execution = batch.ExecuteNonQueryAsync(cancellation.Token);

        execution.IsCanceled.Should().BeTrue();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await execution);
        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(0);
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public void SqliteBatchMatchesSequentialReaderAndAffectedRowSemantics(string provider)
    {
        using var connection = OpenSqliteConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var batch = (SqliteBatch)connection.CreateBatch();
        var insert = new SqliteBatchCommand("INSERT INTO data VALUES ($value);");
        insert.Parameters.AddWithValue("$value", 3);
        batch.BatchCommands.Add(insert);

        var select = new SqliteBatchCommand("SELECT value FROM data;");
        batch.BatchCommands.Add(select);

        var update = new SqliteBatchCommand("UPDATE data SET value = $value;");
        update.Parameters.Add("$value", SqliteType.Integer).Value = 4;
        batch.BatchCommands.Add(update);

        using var reader = batch.ExecuteReader();
        reader.FieldCount.Should().Be(0);
        reader.NextResult().Should().BeTrue();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(3);
        reader.NextResult().Should().BeTrue();
        reader.FieldCount.Should().Be(0);
        reader.RecordsAffected.Should().Be(2);
        reader.NextResult().Should().BeFalse();

        insert.RecordsAffected.Should().Be(1);
        select.RecordsAffected.Should().Be(-1);
        update.RecordsAffected.Should().Be(1);
        connection.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(4);
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public void SqliteBatchFailurePreservesPriorStatementWithoutTransaction(string provider)
    {
        using var connection = OpenSqliteConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var batch = (SqliteBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO data VALUES (1);"));
        batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO missing VALUES (2);"));

        Assert.Throws<SqliteException>(() => batch.ExecuteNonQuery());
        connection.ExecuteScalar<long>("SELECT count(*) FROM data;").Should().Be(1);
    }

    [Test]
    public void SqliteBatchUsesCurrentTransactionAndRejectsMissingAssociation()
    {
        using var connection = OpenSqliteConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using (var transaction = connection.BeginTransaction())
        {
            using var batch = (SqliteBatch)connection.CreateBatch();
            batch.Transaction.Should().BeSameAs(transaction);
            batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO data VALUES (1);"));
            batch.ExecuteNonQuery().Should().Be(1);
            transaction.Rollback();
        }

        connection.ExecuteScalar<long>("SELECT count(*) FROM data;").Should().Be(0);

        using var detached = (SqliteBatch)connection.CreateBatch();
        detached.BatchCommands.Add(new SqliteBatchCommand("SELECT 1;"));
        using var activeTransaction = connection.BeginTransaction();
        detached.Invoking(static value => value.ExecuteNonQuery())
            .Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void SqliteBatchSnapshotsParametersBeforeLazyResultTransitions()
    {
        using var connection = OpenSqliteConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var batch = (SqliteBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new SqliteBatchCommand("SELECT 1;"));
        var insert = new SqliteBatchCommand("INSERT INTO data VALUES ($value);");
        var parameter = insert.Parameters.AddWithValue("$value", 7);
        batch.BatchCommands.Add(insert);

        using var reader = batch.ExecuteReader();
        parameter.Value = 9;
        reader.Close();

        connection.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(7);
    }

    [Test]
    public void SqliteBatchPreservesInferredParameterSizes()
    {
        using var connection = OpenSqliteConnection("Managed");
        using var batch = (SqliteBatch)connection.CreateBatch();
        var command = new SqliteBatchCommand("SELECT length($text), length($blob);");
        var guid = Guid.Parse("8e78d135-f76b-4af0-a5f4-08aac126c388");
        command.Parameters.AddWithValue("$text", guid);
        command.Parameters.AddWithValue("$blob", new Memory<byte>([1, 2, 3]));
        batch.BatchCommands.Add(command);

        using var reader = batch.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(36);
        reader.GetInt64(1).Should().Be(3);
    }

    [Test]
    public void SqliteBatchPreservesMissingParameterValidation()
    {
        using var connection = OpenSqliteConnection("Managed");
        using var batch = (SqliteBatch)connection.CreateBatch();
        var command = new SqliteBatchCommand("SELECT $value;");
        command.Parameters.Add("$value", SqliteType.Integer);
        batch.BatchCommands.Add(command);

        batch.Invoking(static value => value.ExecuteScalar())
            .Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void SqliteBatchScalarSkipsNonQueryCommandResults()
    {
        using var connection = OpenSqliteConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var batch = (SqliteBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO data VALUES (1);"));
        batch.BatchCommands.Add(new SqliteBatchCommand("SELECT count(*) FROM data;"));

        batch.ExecuteScalar().Should().Be(1L);
    }

    [Test]
    public void FailedFirstBatchCommandReleasesItsReader()
    {
        using var connection = OpenTursoConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER UNIQUE);");
        connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");
        using var batch = (TursoBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (1);"));

        Assert.Throws<TursoException>(() => batch.ExecuteReader());

        GetPrivateCollectionCount(connection, "_openReaders").Should().Be(0);
    }

    [Test]
    public void RawTransactionCompletionClearsTrackedTransaction()
    {
        using var connection = OpenTursoConnection("Managed");
        using var transaction = connection.BeginTransaction();

        connection.ExecuteNonQuery("COMMIT;");

        transaction.Invoking(static value => value.Commit())
            .Should().Throw<InvalidOperationException>();
        using var nextTransaction = connection.BeginTransaction();
        nextTransaction.Rollback();
    }

    [Test]
    public void BatchReadersCanBeDisposedAfterTheirConnectionsClose()
    {
        using (var connection = new TrackingTursoConnection(
                   "Data Source=:memory:;Local Provider=Managed"))
        {
            connection.Open();
            using var batch = (TursoBatch)connection.CreateBatch();
            batch.BatchCommands.Add(new TursoBatchCommand("SELECT 1;"));
            var reader = batch.ExecuteReader(System.Data.CommandBehavior.CloseConnection);
            connection.Close();

            reader.IsClosed.Should().BeTrue();
            connection.CloseCalls.Should().Be(1);
            connection.Open();
            batch.ExecuteScalar().Should().Be(1L);
            reader.Invoking(static value => value.Dispose()).Should().NotThrow();
        }

        using (var connection = OpenSqliteConnection("Managed"))
        {
            using var batch = (SqliteBatch)connection.CreateBatch();
            batch.BatchCommands.Add(new SqliteBatchCommand("SELECT 1;"));
            var closedTransitions = 0;
            connection.StateChange += (_, args) =>
            {
                if (args.OriginalState == System.Data.ConnectionState.Open
                    && args.CurrentState == System.Data.ConnectionState.Closed)
                {
                    closedTransitions++;
                }
            };
            var reader = batch.ExecuteReader(System.Data.CommandBehavior.CloseConnection);
            connection.Close();

            reader.IsClosed.Should().BeTrue();
            closedTransitions.Should().Be(1);
            connection.Open();
            batch.ExecuteScalar().Should().Be(1L);
            reader.Invoking(static value => value.Dispose()).Should().NotThrow();
        }
    }

    [Test]
    public async Task NextResultAsyncCancellationDoesNotStartTheNextCommand()
    {
        using var connection = OpenTursoConnection("Managed");
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var batch = (TursoBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new TursoBatchCommand("SELECT 1;"));
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (1);"));
        var reader = batch.ExecuteReader();
        reader.Read().Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var transition = reader.NextResultAsync(cancellation.Token);

        transition.IsCanceled.Should().BeTrue();
        Assert.CatchAsync<OperationCanceledException>(async () => await transition);
        batch.Cancel();
        reader.Dispose();
        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(0);
    }

    [Test]
    public void ProviderFactoriesAdvertiseBatchSupport()
    {
        TursoFactory.Instance.CanCreateBatch.Should().BeTrue();
        TursoFactory.Instance.CreateBatch().Should().BeOfType<TursoBatch>();
        TursoFactory.Instance.CreateBatchCommand().Should().BeOfType<TursoBatchCommand>();

        SqliteFactory.Instance.CanCreateBatch.Should().BeTrue();
        SqliteFactory.Instance.CreateBatch().Should().BeOfType<SqliteBatch>();
        SqliteFactory.Instance.CreateBatchCommand().Should().BeOfType<SqliteBatchCommand>();
    }

    private static TursoConnection OpenTursoConnection(string provider)
    {
        EnsureProvider(provider);
        var connection = new TursoConnection($"Data Source=:memory:;Local Provider={provider}");
        connection.Open();
        connection.CanCreateBatch.Should().BeTrue();
        return connection;
    }

    private static SqliteConnection OpenSqliteConnection(string provider)
    {
        EnsureProvider(provider);
        var connection = new SqliteConnection($"Data Source=:memory:;Local Provider={provider}");
        connection.Open();
        connection.CanCreateBatch.Should().BeTrue();
        return connection;
    }

    private static void EnsureProvider(string provider)
    {
        if (provider == "Native")
            NativeProviderTestFixture.EnsureRegistered();
    }

    private static T Scalar<T>(TursoConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static int GetPrivateCollectionCount(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to retrieve {fieldName}.");
        var collection = field.GetValue(instance)
            ?? throw new InvalidOperationException($"{fieldName} was null.");
        return (int)(collection.GetType().GetProperty("Count")?.GetValue(collection)
                     ?? throw new InvalidOperationException($"{fieldName} has no Count property."));
    }

    private sealed class TrackingTursoConnection(string connectionString)
        : TursoConnection(connectionString)
    {
        public int CloseCalls { get; private set; }

        public override void Close()
        {
            CloseCalls++;
            base.Close();
        }
    }
}
