using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class FacadeCapabilityMatrixTests
{
    public static IEnumerable<TestCaseData> CapabilityCases()
    {
        yield return CapabilityCase(
            "Turso.Data / managed local",
            new TursoConnection("Data Source=:memory:;Local Provider=Managed"),
            TursoConnectionFacade.TursoData,
            TursoConnectionMode.ManagedLocal,
            [true, true, true, true, false, false, false, false, false, false, true, true, false]);
        yield return CapabilityCase(
            "Turso.Data / native local",
            new TursoConnection("Data Source=:memory:;Local Provider=Native"),
            TursoConnectionFacade.TursoData,
            TursoConnectionMode.NativeLocal,
            [true, true, true, true, false, false, false, false, false, false, true, false, false]);
        yield return CapabilityCase(
            "Turso.Data / remote Hrana",
            new TursoConnection("Data Source=https://example.turso.io"),
            TursoConnectionFacade.TursoData,
            TursoConnectionMode.RemoteHrana,
            [true, true, true, true, false, false, false, false, false, false, false, false, false]);
        yield return CapabilityCase(
            "Turso.Data / embedded replica",
            new TursoConnection("Data Source=https://example.turso.io;Replica Path=replica.db"),
            TursoConnectionFacade.TursoData,
            TursoConnectionMode.EmbeddedReplica,
            [false, true, true, true, false, false, false, false, false, false, false, false, true]);
        yield return CapabilityCase(
            "Turso.Data.Sqlite / managed local",
            new SqliteConnection("Data Source=:memory:;Local Provider=Managed"),
            TursoConnectionFacade.Sqlite,
            TursoConnectionMode.ManagedLocal,
            [true, true, true, true, true, true, true, true, true, false, true, true, false]);
        yield return CapabilityCase(
            "Turso.Data.Sqlite / native local",
            new SqliteConnection("Data Source=:memory:;Local Provider=Native"),
            TursoConnectionFacade.Sqlite,
            TursoConnectionMode.NativeLocal,
            [true, true, true, true, true, true, true, true, true, true, true, false, false]);
    }

    [TestCaseSource(nameof(CapabilityCases))]
    public void PublicCapabilityContractMatchesProviderAndFacade(
        System.Data.Common.DbConnection connection,
        TursoConnectionFacade facade,
        TursoConnectionMode mode,
        bool[] expected)
    {
        using (connection)
        {
            var capabilities = connection switch
            {
                TursoConnection turso => turso.Capabilities,
                SqliteConnection sqlite => sqlite.Capabilities,
                _ => throw new AssertionException("Unexpected connection type."),
            };

            capabilities.Facade.Should().Be(facade);
            capabilities.Mode.Should().Be(mode);
            connection.CanCreateBatch.Should().Be(capabilities.CanCreateBatch);
            GetCapabilityValues(capabilities).Should().Equal(expected);
        }
    }

    [Test]
    public Task ManagedTursoTransactionsImplementSavepointContract()
        => AssertTursoSavepointContract("Managed");

    [Test]
    [Category("Native")]
    public Task NativeTursoTransactionsImplementSavepointContract()
    {
        NativeProviderTestFixture.EnsureRegistered();
        return AssertTursoSavepointContract("Native");
    }

    private static async Task AssertTursoSavepointContract(string provider)
    {
        using var connection = new TursoConnection(
            $"Data Source=:memory:;Local Provider={provider}");
        await connection.OpenAsync();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var transaction = await connection.BeginTransactionAsync();

        transaction.SupportsSavepoints.Should().BeTrue();
        await transaction.SaveAsync("checkpoint");
        connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");
        await transaction.RollbackAsync("checkpoint");
        await transaction.ReleaseAsync("checkpoint");
        await transaction.CommitAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM data;";
        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Test]
    public void EmbeddedReplicaBatchFailsAtCreationWithoutNativeOrNetworkAccess()
    {
        using var connection = new TursoConnection(
            "Data Source=https://example.turso.io;Replica Path=replica.db");

        connection.CanCreateBatch.Should().BeFalse();
        connection.Invoking(static value => value.CreateBatch())
            .Should().Throw<NotSupportedException>()
            .WithMessage("Turso batch execution is not supported for embedded replica connections.");
    }

    [Test]
    public void RemoteAttachFailsBeforeNetworkAccessForCommandsAndBatches()
    {
        using var connection = new TursoConnection("Data Source=http://localhost:1");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "; -- capability gate\nATTACH DATABASE 'other.db' AS other;";
        command.Invoking(static value => value.ExecuteNonQuery())
            .Should().Throw<NotSupportedException>()
            .WithMessage("ATTACH and DETACH are supported only for local database connections.");

        using var batch = (TursoBatch)connection.CreateBatch();
        batch.BatchCommands.Add(new TursoBatchCommand(";;; DETACH DATABASE other;"));
        batch.Invoking(static value => value.ExecuteNonQuery())
            .Should().Throw<NotSupportedException>()
            .WithMessage("ATTACH and DETACH are supported only for local database connections.");
    }

    [TestCase("Data Source=:memory:;Local Provider=Native;Pooling=True")]
    [TestCase("Data Source=http://localhost:1;Pooling=True")]
    [TestCase("Data Source=https://example.turso.io;Replica Path=replica.db;Pooling=True")]
    public void TursoPoolingRejectsUnsupportedModesBeforeProviderAccess(string connectionString)
    {
        using var connection = new TursoConnection(connectionString);

        connection.Invoking(static value => value.Open())
            .Should().Throw<NotSupportedException>()
            .WithMessage("Pooling=True is supported only for unencrypted managed local file databases.");
    }

    [Test]
    public void SqliteFacadeRejectsRemoteDataSourcesBeforeTreatingThemAsFiles()
    {
        using var connection = new SqliteConnection(
            "Data Source=libsql://example.turso.io;Local Provider=Managed");

        connection.Invoking(static value => value.Open())
            .Should().Throw<NotSupportedException>()
            .WithMessage(
                "Turso.Data.Sqlite supports only local database connections. Use TursoConnection for remote Hrana or embedded replica connections.");
    }

    [Test]
    public void SyncRejectsNonReplicaConnectionsThroughTheCapabilityGate()
    {
        using var connection = new TursoConnection(
            "Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        connection.Invoking(static value => value.Sync())
            .Should().Throw<NotSupportedException>()
            .WithMessage("Sync requires an embedded replica connection.");
    }

    [Test]
    public async Task SqliteTransactionAsyncMethodsUseTheExecutableContract()
    {
        using var connection = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        using var transaction = await connection.BeginTransactionAsync();

        transaction.SupportsSavepoints.Should().BeTrue();
        await transaction.SaveAsync("checkpoint");
        connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");
        await transaction.RollbackAsync("checkpoint");
        await transaction.ReleaseAsync("checkpoint");
        await transaction.CommitAsync();

        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);
    }

    private static TestCaseData CapabilityCase(
        string name,
        System.Data.Common.DbConnection connection,
        TursoConnectionFacade facade,
        TursoConnectionMode mode,
        bool[] expected)
        => new(connection, facade, mode, expected)
        {
            TestName = name,
        };

    private static bool[] GetCapabilityValues(TursoConnectionCapabilities capabilities)
        =>
        [
            capabilities.CanCreateBatch,
            capabilities.SupportsAsyncOperations,
            capabilities.SupportsTransactions,
            capabilities.SupportsSavepoints,
            capabilities.SupportsBackup,
            capabilities.SupportsIncrementalBlob,
            capabilities.SupportsUserDefinedFunctions,
            capabilities.SupportsUserDefinedAggregates,
            capabilities.SupportsCustomCollations,
            capabilities.SupportsExtensions,
            capabilities.SupportsAttach,
            capabilities.SupportsPooling,
            capabilities.SupportsSync,
        ];
}
