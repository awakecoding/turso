using System.Data;
using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

[NonParallelizable]
public sealed class ManagedSharedCacheContractTests
{
    private const string UnsupportedConfigurationMessage =
        "Cache=Shared with Local Provider=Managed is supported only for named in-memory databases using Mode=Memory and a non-empty Data Source other than :memory:; file-backed and anonymous in-memory shared caches are not supported.";

    private const string ReadUncommittedNotSupportedMessage =
        "PRAGMA read_uncommitted and IsolationLevel.ReadUncommitted are not supported for managed shared-memory databases because the managed engine preserves transaction isolation and does not expose dirty reads.";

    private const string CallbacksNotSupportedMessage =
        "Managed shared-memory databases do not support connection-local functions, aggregates, or collations because the managed catalog is shared across connections.";

    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    [Test]
    public void NamedMemorySharesCatalogUntilTheLastLogicalConnectionCloses()
    {
        var connectionString = CreateConnectionString();
        using var first = new SqliteConnection(connectionString);
        using var second = new SqliteConnection(connectionString);
        first.Open();
        var firstPhysicalConnection = first.ManagedConnection;
        first.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (42);");

        second.Open();
        second.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(42);

        first.Close();
        SqliteConnection.ClearAllPools();
        second.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(42);

        first.Open();
        first.ManagedConnection.Should().NotBeSameAs(firstPhysicalConnection);
        first.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(42);

        first.Close();
        second.Close();

        using var replacement = new SqliteConnection(connectionString);
        replacement.Open();
        CountTable(replacement, "data").Should().Be(0);
    }

    [Test]
    public void TursoAndSqliteConnectionsShareTheSameNamedMemoryCatalog()
    {
        var name = "managed-cross-surface-" + Guid.NewGuid().ToString("N");
        var connectionString =
            $"Data Source={name};Mode=Memory;Cache=Shared;Pooling=True;Local Provider=Managed";
        using var turso = new TursoConnection(connectionString);
        using var sqlite = new SqliteConnection(connectionString);

        turso.Open();
        turso.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        turso.ExecuteNonQuery("INSERT INTO data VALUES (7);");

        sqlite.Open();
        sqlite.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(7);

        turso.Close();
        sqlite.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(7);
    }

    [Test]
    public void PrivateNamedMemoryConnectionsRemainIsolated()
    {
        var name = "managed-private-memory-" + Guid.NewGuid().ToString("N");
        var connectionString =
            $"Data Source={name};Mode=Memory;Cache=Private;Pooling=True;Local Provider=Managed";
        using var first = new SqliteConnection(connectionString);
        using var second = new SqliteConnection(connectionString);
        first.Open();
        second.Open();

        first.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        CountTable(first, "data").Should().Be(1);
        CountTable(second, "data").Should().Be(0);
    }

    [Test]
    public void SharedMemoryNamesAreCaseSensitive()
    {
        var name = "managed-case-sensitive-" + Guid.NewGuid().ToString("N");
        using var lower = new SqliteConnection(
            $"Data Source={name};Mode=Memory;Cache=Shared;Local Provider=Managed");
        using var upper = new SqliteConnection(
            $"Data Source={name.ToUpperInvariant()};Mode=Memory;Cache=Shared;Local Provider=Managed");
        lower.Open();
        upper.Open();

        lower.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        CountTable(lower, "data").Should().Be(1);
        CountTable(upper, "data").Should().Be(0);
    }

    [Test]
    public async Task ConcurrentConnectionsSerializeSharedCatalogMutations()
    {
        var connectionString = CreateConnectionString();
        using var anchor = new SqliteConnection(connectionString);
        anchor.Open();
        anchor.ExecuteNonQuery("CREATE TABLE data(value INTEGER PRIMARY KEY);");

        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            for (var index = 0; index < 25; index++)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    $"INSERT INTO data VALUES ({worker * 25 + index});");
            }
        }));

        await Task.WhenAll(workers);

        anchor.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(100);
    }

    [Test]
    public void SharedMemoryPreservesIsolationAndRejectsDirtyReadRequests()
    {
        var connectionString = CreateConnectionString();
        using var writer = new SqliteConnection(connectionString);
        using var reader = new SqliteConnection(connectionString);
        writer.Open();
        reader.Open();
        writer.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var transaction = writer.BeginTransaction();
        writer.ExecuteNonQuery("INSERT INTO data VALUES (1);");
        reader.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);

        reader.Invoking(static connection => connection.ExecuteNonQuery("PRAGMA read_uncommitted = 1;"))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        reader.ExecuteNonQuery("PRAGMA read_uncommitted = 0;");
        reader.ExecuteScalar<long>("PRAGMA read_uncommitted;").Should().Be(0);
        reader.Invoking(static connection => connection.BeginTransaction(IsolationLevel.ReadUncommitted))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        reader.Invoking(static connection => connection.BeginTransaction(IsolationLevel.ReadUncommitted, deferred: false))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        reader.Transaction.Should().BeNull();

        transaction.Commit();
        reader.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(1);
    }

    [Test]
    public void TursoConnectionRejectsDirtyReadRequestsForSharedMemory()
    {
        using var connection = new TursoConnection(CreateConnectionString());
        connection.Open();

        connection.Invoking(static value => value.ExecuteNonQuery("PRAGMA read_uncommitted(ON);"))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        ReadTursoInt64(connection, "PRAGMA read_uncommitted;").Should().Be(0);
        connection.Invoking(static value => value.BeginTransaction(IsolationLevel.ReadUncommitted))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
    }

    [TestCase("0x1")]
    [TestCase("1.0")]
    [TestCase("2e0")]
    [TestCase("1e999")]
    [TestCase("+1")]
    [TestCase("257")]
    [TestCase("0x101")]
    [TestCase("'1'")]
    public void ManagedSharedMemoryRejectsNumericDirtyReadPragmaForms(string value)
    {
        var connectionString = CreateConnectionString();
        using var sqlite = new SqliteConnection(connectionString);
        using var turso = new TursoConnection(connectionString);
        sqlite.Open();
        turso.Open();

        sqlite.Invoking(connection => connection.ExecuteNonQuery($"PRAGMA read_uncommitted = {value};"))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
        turso.Invoking(connection => connection.ExecuteNonQuery($"PRAGMA read_uncommitted = {value};"))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(ReadUncommittedNotSupportedMessage);
    }

    [TestCase("-1")]
    [TestCase("-0x1")]
    [TestCase("256")]
    [TestCase("0x100")]
    [TestCase("2147483648")]
    [TestCase("0xffffffff")]
    [TestCase("'+1'")]
    public void ManagedSharedMemoryAcceptsNumericPragmaFormsThatSQLiteTreatsAsDisabled(string value)
    {
        var connectionString = CreateConnectionString();
        using var sqlite = new SqliteConnection(connectionString);
        using var turso = new TursoConnection(connectionString);
        sqlite.Open();
        turso.Open();

        sqlite.ExecuteNonQuery($"PRAGMA read_uncommitted = {value};");
        turso.ExecuteNonQuery($"PRAGMA read_uncommitted = {value};");

        sqlite.ExecuteScalar<long>("PRAGMA read_uncommitted;").Should().Be(0);
        ReadTursoInt64(turso, "PRAGMA read_uncommitted;").Should().Be(0);
    }

    [Test]
    public void ManagedSharedMemoryRejectsEncryption()
    {
        var key = Convert.ToHexString(new byte[32]);
        var connectionString = CreateConnectionString()
                               + $";Encryption Cipher=Aes256Gcm;Encryption Key={key}";
        using var sqlite = new SqliteConnection(connectionString);
        using var turso = new TursoConnection(connectionString);

        sqlite.Invoking(static connection => connection.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("Encryption is supported only for file-backed databases when Local Provider=Managed.");
        turso.Invoking(static connection => connection.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("Encryption is supported only for file-backed databases when Local Provider=Managed.");
    }

    [TestCase("Data Source=:memory:;Cache=Shared")]
    [TestCase("Data Source=:memory:;Mode=Memory;Cache=Shared")]
    [TestCase("Data Source=managed-file-shared;Cache=Shared")]
    [TestCase("Mode=Memory;Cache=Shared")]
    public void ManagedProviderRejectsSharedCacheOutsideNamedMemoryMode(string options)
    {
        var connectionString = options + ";Local Provider=Managed";
        using var sqlite = new SqliteConnection(connectionString);
        using var turso = new TursoConnection(connectionString);

        sqlite.Invoking(static connection => connection.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(UnsupportedConfigurationMessage);
        turso.Invoking(static connection => connection.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(UnsupportedConfigurationMessage);
        sqlite.State.Should().Be(ConnectionState.Closed);
        turso.State.Should().Be(ConnectionState.Closed);
    }

    [Test]
    public void ManagedSharedMemoryRejectsConnectionLocalCallbacks()
    {
        using var beforeOpen = new SqliteConnection(CreateConnectionString());
        beforeOpen.CreateFunction("local_value", static () => 1L);
        beforeOpen.Invoking(static connection => connection.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(CallbacksNotSupportedMessage);
        beforeOpen.State.Should().Be(ConnectionState.Closed);

        using var afterOpen = new SqliteConnection(CreateConnectionString());
        afterOpen.Open();
        afterOpen.Invoking(static connection => connection.CreateCollation("local", StringComparer.Ordinal.Compare))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(CallbacksNotSupportedMessage);
    }

    [Test]
    public async Task CanceledOpenDoesNotAcquireSharedMemoryLifetime()
    {
        var connectionString = CreateConnectionString();
        using var canceled = new SqliteConnection(connectionString);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await canceled.Awaiting(connection => connection.OpenAsync(cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        canceled.State.Should().Be(ConnectionState.Closed);

        using (var writer = new SqliteConnection(connectionString))
        {
            await writer.OpenAsync();
            await ExecuteNonQueryAsync(writer, "CREATE TABLE data(value INTEGER);");
        }

        using var replacement = new SqliteConnection(connectionString);
        replacement.Open();
        CountTable(replacement, "data").Should().Be(0);
    }

    private static string CreateConnectionString()
    {
        var name = "managed-shared-memory-" + Guid.NewGuid().ToString("N");
        return $"Data Source={name};Mode=Memory;Cache=Shared;Pooling=True;Local Provider=Managed";
    }

    private static long CountTable(SqliteConnection connection, string name)
        => connection.ExecuteScalar<long>(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{name}';");

    private static long ReadTursoInt64(TursoConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task<int> ExecuteNonQueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }
}
