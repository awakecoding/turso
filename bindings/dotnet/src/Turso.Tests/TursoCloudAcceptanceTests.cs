using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using FacadeConnection = Turso.Data.Sqlite.SqliteConnection;

namespace Turso.Tests;

[NonParallelizable]
public sealed class TursoCloudAcceptanceTests
{
    private enum AcceptanceValue
    {
        Expected = 7,
    }

    [Test]
    public async Task RemoteAndReplicaConsumerWorkflowConverges()
    {
        var (remoteUrl, authToken) = GetCloudCredentials();
        var tableName = "dotnet_acceptance_" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        var replicaDirectory = NewReplicaDirectory("acceptance");
        var replicaPath = Path.Combine(replicaDirectory, "replica.db");
        var remoteConnectionString = $"Data Source={remoteUrl};Auth Token={authToken}";

        await using var remote = new TursoConnection(remoteConnectionString);
        await remote.OpenAsync();
        try
        {
            await ExecuteNonQueryAsync(
                remote,
                $"CREATE TABLE {tableName}(id GUID PRIMARY KEY, value INTEGER, optional TEXT)");
            await using (var insert = remote.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO {tableName}(id, value, optional) VALUES ($id, 1, NULL)";
                insert.Parameters.Add(new TursoParameter("$id", id));
                await insert.ExecuteNonQueryAsync();
            }

            await using (var select = remote.CreateCommand())
            {
                select.CommandText = $"SELECT id, value, optional FROM {tableName}";
                using var adapter = new AcceptanceDataAdapter { SelectCommand = select };
                var table = new DataTable();
                adapter.Fill(table);
                table.Rows.Count.Should().Be(1);
                table.Columns["id"]!.DataType.Should().Be(typeof(string));
                table.Rows[0]["id"].Should().Be(id.ToString());
                table.Rows[0]["optional"].Should().Be(DBNull.Value);
            }

            await using (var enumCommand = remote.CreateCommand())
            {
                enumCommand.CommandText = "SELECT $value";
                enumCommand.Parameters.Add(new TursoParameter("$value", AcceptanceValue.Expected));
                (await enumCommand.ExecuteScalarAsync()).Should().Be(7L);
            }

            await using (var transaction = await remote.BeginTransactionAsync(IsolationLevel.RepeatableRead))
            {
                transaction.IsolationLevel.Should().Be(IsolationLevel.Serializable);
                await transaction.RollbackAsync();
            }

            await using var replica = new TursoConnection(
                remoteConnectionString + $";Replica Path={replicaPath};Pooling=False");
            await replica.OpenAsync();
            await ExecuteNonQueryAsync(remote, $"UPDATE {tableName} SET value = 2 WHERE id = $id", id);
            await replica.SyncAsync();

            await using var replicaRead = replica.CreateCommand();
            replicaRead.CommandText = $"SELECT value FROM {tableName} WHERE id = $id";
            replicaRead.Parameters.Add(new TursoParameter("$id", id));
            (await replicaRead.ExecuteScalarAsync()).Should().Be(2L);
        }
        finally
        {
            await DropTableAsync(remote, tableName);
            Directory.Delete(replicaDirectory, recursive: true);
        }
    }

    [Test]
    public async Task ReplicaLocalWritePushesToRemote()
    {
        var (remoteUrl, authToken) = GetCloudCredentials();
        var tableName = "dotnet_push_" + Guid.NewGuid().ToString("N");
        var replicaDirectory = NewReplicaDirectory("push");
        var replicaPath = Path.Combine(replicaDirectory, "replica.db");
        var remoteConnectionString = $"Data Source={remoteUrl};Auth Token={authToken}";

        await using var remote = new TursoConnection(remoteConnectionString);
        await remote.OpenAsync();
        try
        {
            await ExecuteNonQueryAsync(remote, $"CREATE TABLE {tableName}(id TEXT PRIMARY KEY, value TEXT)");
            await using var replica = new TursoConnection(
                remoteConnectionString + $";Replica Path={replicaPath};Pooling=False");
            await replica.OpenAsync();
            await using (var insert = replica.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO {tableName}(id, value) VALUES ($id, $value)";
                insert.Parameters.Add(new TursoParameter("$id", "local"));
                insert.Parameters.Add(new TursoParameter("$value", "pushed"));
                await insert.ExecuteNonQueryAsync();
            }

            await replica.SyncDatabase!.PushAsync();

            await using var verify = remote.CreateCommand();
            verify.CommandText = $"SELECT value FROM {tableName} WHERE id = 'local'";
            (await verify.ExecuteScalarAsync()).Should().Be("pushed");
        }
        finally
        {
            await DropTableAsync(remote, tableName);
            Directory.Delete(replicaDirectory, recursive: true);
        }
    }

    [Test]
    public async Task PartialBootstrapPrefixAndQueryReadRemoteData()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore(
                "Partial sync on Windows is deferred until native sparse-file hole detection is implemented.");
        }

        var (remoteUrl, authToken) = GetCloudCredentials();
        var tableName = "dotnet_partial_" + Guid.NewGuid().ToString("N");
        var prefixDirectory = NewReplicaDirectory("partial-prefix");
        var queryDirectory = NewReplicaDirectory("partial-query");
        var remoteConnectionString = $"Data Source={remoteUrl};Auth Token={authToken}";

        await using var remote = new TursoConnection(remoteConnectionString);
        await remote.OpenAsync();
        try
        {
            await ExecuteNonQueryAsync(remote, $"CREATE TABLE {tableName}(id INTEGER PRIMARY KEY, value TEXT)");
            await ExecuteNonQueryAsync(remote, $"INSERT INTO {tableName} VALUES (1, 'partial')");

            await using (var prefixDatabase = await TursoSyncDatabase.CreateAsync(
                             new TursoSyncDatabaseOptions(
                                 Path.Combine(prefixDirectory, "replica.db"),
                                 new Uri(remoteUrl))
                             {
                                 AuthToken = authToken,
                                 PartialSync = new TursoPartialSyncOptions
                                 {
                                     PrefixLength = 4096,
                                     SegmentSize = 4096,
                                     Prefetch = true,
                                 },
                             }))
            await using (var prefixConnection = await prefixDatabase.ConnectAsync())
            {
                using var query = new TursoCommand(
                    prefixConnection,
                    $"SELECT value FROM {tableName} WHERE id = 1");
                query.ExecuteScalar().Should().Be("partial");
            }

            await using (var queryDatabase = await TursoSyncDatabase.CreateAsync(
                             new TursoSyncDatabaseOptions(
                                 Path.Combine(queryDirectory, "replica.db"),
                                 new Uri(remoteUrl))
                             {
                                 AuthToken = authToken,
                                 PartialSync = new TursoPartialSyncOptions
                                 {
                                     Query = $"SELECT value FROM {tableName} WHERE id = 1",
                                     SegmentSize = 4096,
                                 },
                             }))
            await using (var queryConnection = await queryDatabase.ConnectAsync())
            {
                using var query = new TursoCommand(
                    queryConnection,
                    $"SELECT value FROM {tableName} WHERE id = 1");
                query.ExecuteScalar().Should().Be("partial");
            }
        }
        finally
        {
            await DropTableAsync(remote, tableName);
            Directory.Delete(prefixDirectory, recursive: true);
            Directory.Delete(queryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task SqliteFacadeAndEfQueryDirectRemoteAndReplica()
    {
        var (remoteUrl, authToken) = GetCloudCredentials();
        var tableName = "dotnet_facade_" + Guid.NewGuid().ToString("N");
        var replicaDirectory = NewReplicaDirectory("facade-ef");
        var replicaPath = Path.Combine(replicaDirectory, "replica.db");
        var remoteConnectionString = $"Data Source={remoteUrl};Auth Token={authToken}";
        var replicaConnectionString =
            remoteConnectionString + $";Replica Path={replicaPath};Pooling=True";

        await using var remote = new TursoConnection(remoteConnectionString);
        await remote.OpenAsync();
        try
        {
            await ExecuteNonQueryAsync(remote, $"CREATE TABLE {tableName}(value TEXT)");
            await ExecuteNonQueryAsync(remote, $"INSERT INTO {tableName} VALUES ('facade-ef')");

            await using (var facadeRemote = new FacadeConnection(remoteConnectionString))
            {
                await facadeRemote.OpenAsync();
                await using var command = facadeRemote.CreateCommand();
                command.CommandText = $"SELECT value FROM {tableName}";
                (await command.ExecuteScalarAsync()).Should().Be("facade-ef");
            }

            await using (var remoteContext = new DbContext(
                             new DbContextOptionsBuilder()
                                 .UseTurso(remoteConnectionString)
                                 .Options))
            {
#pragma warning disable EF1002 // tableName is generated from a fixed prefix and Guid("N").
                var value = await remoteContext.Database
                    .SqlQueryRaw<string>($"SELECT value AS Value FROM {tableName}")
                    .SingleAsync();
#pragma warning restore EF1002
                value.Should().Be("facade-ef");
            }

            await using (var facadeReplica = new FacadeConnection(replicaConnectionString))
            {
                await facadeReplica.OpenAsync();
                await using var command = facadeReplica.CreateCommand();
                command.CommandText = $"SELECT value FROM {tableName}";
                (await command.ExecuteScalarAsync()).Should().Be("facade-ef");
            }

            await using (var replicaContext = new DbContext(
                             new DbContextOptionsBuilder()
                                 .UseTurso(replicaConnectionString)
                                 .Options))
            {
#pragma warning disable EF1002 // tableName is generated from a fixed prefix and Guid("N").
                var value = await replicaContext.Database
                    .SqlQueryRaw<string>($"SELECT value AS Value FROM {tableName}")
                    .SingleAsync();
#pragma warning restore EF1002
                value.Should().Be("facade-ef");
            }
        }

        finally
        {
            await DropTableAsync(remote, tableName);
            Directory.Delete(replicaDirectory, recursive: true);
        }
    }

    [Test]
    public async Task RemoteEncryptedReplicaOpensWhenCredentialsAreConfigured()
    {
        var remoteUrl = Environment.GetEnvironmentVariable("TURSO_ENCRYPTED_REMOTE_URL");
        var authToken = Environment.GetEnvironmentVariable("TURSO_ENCRYPTED_AUTH_TOKEN");
        var encryptionKey = Environment.GetEnvironmentVariable("TURSO_REMOTE_ENCRYPTION_KEY");
        var cipher = Environment.GetEnvironmentVariable("TURSO_REMOTE_ENCRYPTION_CIPHER");
        if (string.IsNullOrWhiteSpace(remoteUrl)
            || string.IsNullOrWhiteSpace(authToken)
            || string.IsNullOrWhiteSpace(encryptionKey)
            || string.IsNullOrWhiteSpace(cipher))
        {
            Assert.Ignore(
                "Set TURSO_ENCRYPTED_REMOTE_URL, TURSO_ENCRYPTED_AUTH_TOKEN, "
                + "TURSO_REMOTE_ENCRYPTION_KEY, and TURSO_REMOTE_ENCRYPTION_CIPHER "
                + "to run the encrypted replica acceptance test.");
        }

        var directory = NewReplicaDirectory("encrypted");
        try
        {
            await using var database = await TursoSyncDatabase.CreateAsync(
                new TursoSyncDatabaseOptions(
                    Path.Combine(directory, "replica.db"),
                    new Uri(remoteUrl))
                {
                    AuthToken = authToken,
                    RemoteEncryption = new TursoRemoteEncryptionOptions
                    {
                        Key = encryptionKey,
                        Cipher = TursoRemoteEncryptionOptions.ParseCipher(cipher),
                    },
                });
            await using var connection = await database.ConnectAsync();
            using var command = new TursoCommand(connection, "SELECT 1");
            command.ExecuteScalar().Should().Be(1L);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        TursoConnection connection,
        string sql,
        Guid? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id.HasValue)
            command.Parameters.Add(new TursoParameter("$id", id.Value));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropTableAsync(TursoConnection remote, string tableName)
    {
        if (remote.State != ConnectionState.Open)
            await remote.OpenAsync();
        await ExecuteNonQueryAsync(remote, $"DROP TABLE IF EXISTS {tableName}");
    }

    private static (string RemoteUrl, string AuthToken) GetCloudCredentials()
    {
        var remoteUrl = Environment.GetEnvironmentVariable("TURSO_REMOTE_URL");
        var authToken = Environment.GetEnvironmentVariable("TURSO_AUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(remoteUrl) || string.IsNullOrWhiteSpace(authToken))
            Assert.Ignore("Set TURSO_REMOTE_URL and TURSO_AUTH_TOKEN to run the Turso Cloud acceptance tests.");

        return (remoteUrl, authToken);
    }

    private static string NewReplicaDirectory(string suffix)
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"turso-cloud-{suffix}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class AcceptanceDataAdapter : DbDataAdapter;
}
