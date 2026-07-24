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

    [TestCase("Managed", "COMMIT", 2)]
    [TestCase("Managed", "ROLLBACK", 1)]
    [TestCase("Native", "COMMIT", 2)]
    [TestCase("Native", "ROLLBACK", 1)]
    public void TursoBatchRefreshesTransactionAfterCompletion(
        string provider,
        string completion,
        long expectedRows)
    {
        using var connection = OpenTursoConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var transaction = connection.BeginTransaction();
        using var batch = (TursoBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (1);"));
        batch.BatchCommands.Add(new TursoBatchCommand(completion));
        batch.BatchCommands.Add(new TursoBatchCommand("INSERT INTO data VALUES (2);"));

        batch.ExecuteNonQuery().Should().Be(2);

        transaction.Invoking(static value => value.Commit())
            .Should().Throw<InvalidOperationException>();
        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(expectedRows);
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

    [TestCase("Managed", "COMMIT", 2)]
    [TestCase("Managed", "ROLLBACK", 1)]
    [TestCase("Native", "COMMIT", 2)]
    [TestCase("Native", "ROLLBACK", 1)]
    public void SqliteBatchRefreshesTransactionAfterCompletion(
        string provider,
        string completion,
        long expectedRows)
    {
        using var connection = OpenSqliteConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var transaction = connection.BeginTransaction();
        using var batch = (SqliteBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO data VALUES (1);"));
        batch.BatchCommands.Add(new SqliteBatchCommand(completion));
        batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO data VALUES (2);"));

        using (var reader = batch.ExecuteReader())
        {
            while (reader.NextResult())
            {
            }
        }

        transaction.Invoking(static value => value.Commit())
            .Should().Throw<InvalidOperationException>();
        connection.ExecuteScalar<long>("SELECT count(*) FROM data;").Should().Be(expectedRows);
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

    [TestCase("Managed")]
    [TestCase("Native")]
    public void TransactionCompletionParsingHandlesCommentsAndRollbackToSavepoint(string provider)
    {
        using var connection = OpenTursoConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using (var transaction = connection.BeginTransaction())
        {
            connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");
            connection.ExecuteNonQuery("SAVEPOINT pending;");
            connection.ExecuteNonQuery("INSERT INTO data VALUES (2);");

            connection.ExecuteNonQuery(
                "/* leading */ ROLLBACK /* between */ TRANSACTION TO SAVEPOINT pending;");

            transaction.Commit();
        }

        Scalar<long>(connection, "SELECT count(*) FROM data;").Should().Be(1);

        using var completed = connection.BeginTransaction();
        connection.ExecuteNonQuery("-- leading\nCOMMIT TRANSACTION;");
        completed.Invoking(static value => value.Commit())
            .Should().Throw<InvalidOperationException>();
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public void SelectOnlySqliteBatchPreservesUnknownRecordsAffected(string provider)
    {
        using var connection = OpenSqliteConnection(provider);
        using var batch = (SqliteBatch)connection.CreateBatch();
        var first = new SqliteBatchCommand("SELECT 1;");
        var second = new SqliteBatchCommand("SELECT 2;");
        batch.BatchCommands.Add(first);
        batch.BatchCommands.Add(second);

        using var reader = batch.ExecuteReader();
        reader.RecordsAffected.Should().Be(-1);
        reader.NextResult().Should().BeTrue();
        reader.RecordsAffected.Should().Be(-1);
        reader.NextResult().Should().BeFalse();
        reader.RecordsAffected.Should().Be(-1);
        first.RecordsAffected.Should().Be(-1);
        second.RecordsAffected.Should().Be(-1);
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public void ZeroRowDmlChangesUnknownSqliteBatchRecordsAffectedToZero(string provider)
    {
        using var connection = OpenSqliteConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var batch = (SqliteBatch)connection.CreateBatch();
        var command = new SqliteBatchCommand(
            "SELECT 1; /* comment */ UPDATE data SET value = 2 WHERE 0;");
        batch.BatchCommands.Add(command);

        using var reader = batch.ExecuteReader();
        reader.RecordsAffected.Should().Be(-1);
        reader.NextResult().Should().BeFalse();
        reader.RecordsAffected.Should().Be(0);
        command.RecordsAffected.Should().Be(0);
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public async Task CommentedZeroRowDmlIsCountedByAsyncSqliteBatchReader(string provider)
    {
        using var connection = OpenSqliteConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        await using var batch = (SqliteBatch)connection.CreateBatch();
        var command = new SqliteBatchCommand(
            "SELECT 1; -- comment\nUPDATE data SET value = 2 WHERE 0;");
        batch.BatchCommands.Add(command);

        await using var reader = await batch.ExecuteReaderAsync();
        reader.RecordsAffected.Should().Be(-1);
        (await reader.NextResultAsync()).Should().BeFalse();
        reader.RecordsAffected.Should().Be(0);
        command.RecordsAffected.Should().Be(0);
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public async Task CommentedZeroRowDmlIsCountedBySqliteCommandReaders(string provider)
    {
        using var connection = OpenSqliteConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT 1; /* comment */ UPDATE data SET value = 2 WHERE 0;";
            using var reader = command.ExecuteReader();
            reader.RecordsAffected.Should().Be(-1);
            reader.NextResult().Should().BeFalse();
            reader.RecordsAffected.Should().Be(0);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT 1; -- comment\nUPDATE data SET value = 2 WHERE 0;";
            await using var reader = await command.ExecuteReaderAsync();
            reader.RecordsAffected.Should().Be(-1);
            (await reader.NextResultAsync()).Should().BeFalse();
            reader.RecordsAffected.Should().Be(0);
        }
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public void CommentedWithDmlIsCountedWithoutMatchingLiteralText(string provider)
    {
        using var connection = OpenSqliteConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var batch = (SqliteBatch)connection.CreateBatch();
        var dml = new SqliteBatchCommand(
            """
            SELECT 1;
            /* lead */ WITH candidate(value) AS (SELECT 1)
            /* operation */ UPDATE data SET value = 2
            WHERE value IN (SELECT value FROM candidate) AND 0;
            """);
        var select = new SqliteBatchCommand(
            """
            WITH candidate(value) AS (SELECT ') UPDATE'),
                 replace AS (SELECT value FROM candidate)
            SELECT value FROM replace;
            """);
        batch.BatchCommands.Add(dml);
        batch.BatchCommands.Add(select);

        using var reader = batch.ExecuteReader();
        reader.NextResult().Should().BeTrue();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be(") UPDATE");
        reader.NextResult().Should().BeFalse();
        reader.RecordsAffected.Should().Be(0);
        dml.RecordsAffected.Should().Be(0);
        select.RecordsAffected.Should().Be(-1);
    }

    [TestCase("Managed")]
    [TestCase("Native")]
    public void ZeroRowDmlBeforeResultSetStartsSqliteBatchRecordsAffectedAtZero(string provider)
    {
        using var connection = OpenSqliteConnection(provider);
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var batch = (SqliteBatch)connection.CreateBatch();
        var command = new SqliteBatchCommand(
            "UPDATE data SET value = 2 WHERE 0; SELECT 1;");
        batch.BatchCommands.Add(command);

        using var reader = batch.ExecuteReader();
        reader.RecordsAffected.Should().Be(-1);
        reader.NextResult().Should().BeFalse();
        reader.RecordsAffected.Should().Be(0);
        command.RecordsAffected.Should().Be(0);
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
