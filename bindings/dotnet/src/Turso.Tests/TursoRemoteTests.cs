using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Turso.Raw.Public;

namespace Turso.Tests;

public class TursoRemoteTests
{
    [Test]
    public void TestConnectionStringBuilderNormalizesRemoteKeywords()
    {
        var builder = new TursoConnectionStringBuilder(
            "Data Source=libsql://example.turso.io;AuthToken=secret;ReadYourWrites=False;ReplicaPath=replica.db;SyncInterval=5;TLS=True");

        builder.DataSource.Should().Be("libsql://example.turso.io");
        builder.AuthToken.Should().Be("secret");
        builder.ReadYourWrites.Should().BeFalse();
        builder.ReplicaPath.Should().Be("replica.db");
        builder.SyncInterval.Should().Be(5);
        builder.Tls.Should().BeTrue();
        builder.ConnectionString.Should().Contain("Auth Token=secret");
        builder.ConnectionString.Should().Contain("Read Your Writes=False");
        builder.ContainsKey("AuthToken").Should().BeTrue();
    }

    [Test]
    public void TestRemoteReplicaFailsBeforeNetworkAccess()
    {
        using var connection = new TursoConnection(
            "Data Source=libsql://example.turso.io;Auth Token=secret;Replica Path=replica.db");

        connection.Invoking(x => x.Open())
            .Should().Throw<NotSupportedException>()
            .WithMessage("Embedded replica connections are not supported yet by the .NET provider.*");
    }

    [Test]
    public void TestRemoteTlsConflictFailsBeforeNetworkAccess()
    {
        using var connection = new TursoConnection("Data Source=http://localhost:8080;Tls=True");

        connection.Invoking(x => x.Open())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Tls=True conflicts with the http URL scheme.");
    }

    [Test]
    public void TestRemoteAuthTokenRequiresHttpsExceptLoopback()
    {
        using var httpConnection = new TursoConnection("Data Source=http://example.com;Auth Token=secret");
        httpConnection.Invoking(x => x.Open())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Auth Token requires an HTTPS remote Turso URL unless the host is localhost or loopback.");

        using var libsqlCleartextConnection = new TursoConnection("Data Source=libsql://example.com;Tls=False;Auth Token=secret");
        libsqlCleartextConnection.Invoking(x => x.Open())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Auth Token requires an HTTPS remote Turso URL unless the host is localhost or loopback.");

        using var loopbackConnection = new TursoConnection("Data Source=http://localhost:8080;Auth Token=secret");
        loopbackConnection.Open();
        loopbackConnection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Test]
    public void TestLocalConnectionRejectsRemoteOnlyOptions()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Auth Token=secret");

        connection.Invoking(x => x.Open())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Auth Token requires a remote Turso URL Data Source.");
    }

    [Test]
    public void TestCanCreateBatchIsRemoteOnly()
    {
        using var localConnection = new TursoConnection("Data Source=:memory:");
        localConnection.CanCreateBatch.Should().BeFalse();
        localConnection.Invoking(x => x.CreateBatch())
            .Should().Throw<NotSupportedException>()
            .WithMessage("Turso batch execution is currently supported only for remote connections.");

        using var remoteConnection = new TursoConnection("Data Source=https://example.com");
        remoteConnection.CanCreateBatch.Should().BeTrue();
    }

    [Test]
    public void TestRemoteOpenCloseStateDoesNotRequireNetwork()
    {
        using var connection = new TursoConnection("Data Source=http://localhost:8080;Read Your Writes=False");

        connection.Open();
        connection.State.Should().Be(System.Data.ConnectionState.Open);
        connection.Invoking(x => x.Open())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("The connection is already open.");

        connection.Close();
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Test]
    public async Task TestRemoteClientSerializesParametersAndReadsRows()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [
                        { "name": "n", "decltype": "INTEGER" },
                        { "name": "name", "decltype": "TEXT" }
                      ],
                      "rows": [
                        [
                          { "type": "integer", "value": "42" },
                          { "type": "text", "value": "alice" }
                        ]
                      ],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                },
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: "secret",
            disposeHttpClient: false);

        var parameters = new TursoParameterCollection();
        parameters.Add(42);
        parameters.AddWithValue(":name", "alice");

        var result = await client.ExecuteAsync(
            "SELECT ?, :name",
            parameters,
            wantRows: true,
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("http://localhost:8080/v2/pipeline"));
        handler.Authorization.Should().Be("Bearer secret");

        using var document = JsonDocument.Parse(handler.RequestBody);
        var requests = document.RootElement.GetProperty("requests");
        requests.GetArrayLength().Should().Be(2);
        requests[0].GetProperty("type").GetString().Should().Be("execute");
        requests[0].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("SELECT ?, :name");
        requests[0].GetProperty("stmt").GetProperty("args")[0].GetProperty("value").GetString().Should().Be("42");
        requests[0].GetProperty("stmt").GetProperty("named_args")[0].GetProperty("name").GetString().Should().Be(":name");
        requests[1].GetProperty("type").GetString().Should().Be("close");

        result.Columns.Should().HaveCount(2);
        result.Rows.Should().ContainSingle();
        result.Rows[0][0].GetInt64().Should().Be(42);
        result.Rows[0][1].ToClrValue().Should().Be("alice");
    }

    [Test]
    public void TestRemoteReaderDoesNotConvertNullToEmptyString()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [
                        { "name": "value", "decltype": "TEXT" }
                      ],
                      "rows": [
                        [
                          { "type": "null" }
                        ]
                      ],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                },
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NULL";
        using var reader = command.ExecuteReader();

        reader.GetDataTypeName(0).Should().Be("TEXT");
        reader.GetFieldType(0).Should().Be(typeof(string));
        reader.Read().Should().BeTrue();
        reader.IsDBNull(0).Should().BeTrue();
        reader.GetFieldType(0).Should().Be(typeof(DBNull));
        reader.GetValue(0).Should().Be(DBNull.Value);
        reader.Invoking(x => x.GetString(0))
            .Should().Throw<InvalidCastException>()
            .WithMessage("Cannot convert remote null value to String.");
        reader.Invoking(x => x.GetDateTime(0))
            .Should().Throw<InvalidCastException>()
            .WithMessage("Cannot convert remote null value to DateTime.");
    }

    [Test]
    public void TestRemoteNonQueryUsesNoRowsAndIgnoresTrailingCloseError()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 1,
                      "last_insert_rowid": "1"
                    }
                  }
                },
                {
                  "type": "error",
                  "error": {
                    "message": "close failed",
                    "code": "CLOSE_FAILED"
                  }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (1)";
        command.ExecuteNonQuery().Should().Be(1);

        using var document = JsonDocument.Parse(handler.RequestBody);
        var requests = document.RootElement.GetProperty("requests");
        requests.GetArrayLength().Should().Be(2);
        requests[0].GetProperty("stmt").GetProperty("want_rows").GetBoolean().Should().BeFalse();
        requests[1].GetProperty("type").GetString().Should().Be("close");
    }

    [Test]
    public async Task TestRemoteClientSurfacesSqlErrors()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "type": "error",
                  "error": {
                    "message": "no such table: missing",
                    "code": "SQLITE_ERROR"
                  }
                }
              ]
            }
            """;

        const string closeResponseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(responseJson, closeResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);

        var act = async () => await client.ExecuteAsync(
            "SELECT * FROM missing",
            new TursoParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);

        await act.Should().ThrowAsync<TursoException>()
            .WithMessage("Remote SQL execution failed: no such table: missing (SQLITE_ERROR)");
    }

    [Test]
    public async Task TestRemoteClientRejectsCleartextBaseUrlWithAuthToken()
    {
        const string responseJson = """
            {
              "base_url": "http://example.com",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new TursoRemoteClient(
            httpClient,
            new Uri("https://example.com"),
            authToken: "secret",
            disposeHttpClient: false);

        var act = async () => await client.ExecuteAsync(
            "SELECT 1",
            new TursoParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: false,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Auth Token requires an HTTPS remote Turso URL unless the host is localhost or loopback.");
    }

    [Test]
    public void TestRemoteTransactionsUseBaton()
    {
        const string beginResponseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string commitResponseJson = """
            {
              "baton": "stream.2",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string closeResponseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(beginResponseJson, commitResponseJson, closeResponseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var transaction = connection.BeginTransaction();
        transaction.Commit();

        handler.RequestBodies.Should().HaveCount(3);
        using var beginDocument = JsonDocument.Parse(handler.RequestBodies[0]);
        beginDocument.RootElement.TryGetProperty("baton", out _).Should().BeFalse();
        beginDocument.RootElement.GetProperty("requests").GetArrayLength().Should().Be(1);
        beginDocument.RootElement.GetProperty("requests")[0].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("BEGIN");

        using var commitDocument = JsonDocument.Parse(handler.RequestBodies[1]);
        commitDocument.RootElement.GetProperty("baton").GetString().Should().Be("stream.1");
        var commitRequests = commitDocument.RootElement.GetProperty("requests");
        commitRequests.GetArrayLength().Should().Be(1);
        commitRequests[0].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("COMMIT");

        using var closeDocument = JsonDocument.Parse(handler.RequestBodies[2]);
        closeDocument.RootElement.GetProperty("baton").GetString().Should().Be("stream.2");
        closeDocument.RootElement.GetProperty("requests").GetArrayLength().Should().Be(1);
        closeDocument.RootElement.GetProperty("requests")[0].GetProperty("type").GetString().Should().Be("close");
    }

    [Test]
    public void TestRemoteCommandAndBatchRejectTransactionFromDifferentConnection()
    {
        const string beginResponseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string rollbackResponseJson = """
            {
              "baton": "stream.2",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string closeResponseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler1 = new CapturingHandler(beginResponseJson, rollbackResponseJson, closeResponseJson);
        using var httpClient1 = new HttpClient(handler1);
        using var remoteClient1 = new TursoRemoteClient(
            httpClient1,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection1 = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient1);
        using var transaction = connection1.BeginTransaction();

        using var handler2 = new CapturingHandler();
        using var httpClient2 = new HttpClient(handler2);
        using var remoteClient2 = new TursoRemoteClient(
            httpClient2,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection2 = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=True",
            remoteClient2);

        using var command = connection2.CreateCommand();
        command.CommandText = "SELECT 1";
        command.Transaction = transaction;
        command.Invoking(x => x.ExecuteScalar())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("The transaction is not associated with the command's connection.");

        using var batch = (TursoBatch)connection2.CreateBatch();
        batch.Transaction = transaction;
        var batchCommand = batch.CreateBatchCommand();
        batchCommand.CommandText = "SELECT 1";
        batch.BatchCommands.Add(batchCommand);
        batch.Invoking(x => x.ExecuteNonQuery())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("The transaction is not associated with the batch's connection.");

        handler2.RequestBodies.Should().BeEmpty();
    }

    [Test]
    public void TestRemoteTransactionRollbackUsesErrorResponseBaton()
    {
        const string beginResponseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string commandErrorResponseJson = """
            {
              "baton": "stream.2",
              "results": [
                {
                  "type": "error",
                  "error": {
                    "message": "no such table: missing",
                    "code": "SQLITE_ERROR"
                  }
                }
              ]
            }
            """;

        const string rollbackResponseJson = """
            {
              "baton": "stream.3",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string closeResponseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(beginResponseJson, commandErrorResponseJson, rollbackResponseJson, closeResponseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM missing";
        command.Invoking(x => x.ExecuteNonQuery())
            .Should().Throw<TursoException>()
            .WithMessage("Remote SQL execution failed: no such table: missing (SQLITE_ERROR)");

        transaction.Rollback();

        handler.RequestBodies.Should().HaveCount(4);
        using var rollbackDocument = JsonDocument.Parse(handler.RequestBodies[2]);
        rollbackDocument.RootElement.GetProperty("baton").GetString().Should().Be("stream.2");
        var rollbackRequests = rollbackDocument.RootElement.GetProperty("requests");
        rollbackRequests.GetArrayLength().Should().Be(1);
        rollbackRequests[0].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("ROLLBACK");

        using var closeDocument = JsonDocument.Parse(handler.RequestBodies[3]);
        closeDocument.RootElement.GetProperty("baton").GetString().Should().Be("stream.3");
        closeDocument.RootElement.GetProperty("requests").GetArrayLength().Should().Be(1);
        closeDocument.RootElement.GetProperty("requests")[0].GetProperty("type").GetString().Should().Be("close");
    }

    [Test]
    public void TestRemoteCommitDoesNotThrowWhenPostCommitCloseFails()
    {
        const string beginResponseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string commitResponseJson = """
            {
              "baton": "stream.2",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string closeErrorResponseJson = """
            {
              "results": [
                {
                  "type": "error",
                  "error": {
                    "message": "close failed",
                    "code": "CLOSE_FAILED"
                  }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(beginResponseJson, commitResponseJson, closeErrorResponseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var transaction = connection.BeginTransaction();
        transaction.Invoking(x => x.Commit()).Should().NotThrow();
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Test]
    public void TestRemoteCommitSqlErrorKeepsBatonForRollback()
    {
        const string beginResponseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string commitErrorResponseJson = """
            {
              "baton": "stream.2",
              "results": [
                {
                  "type": "error",
                  "error": {
                    "message": "FOREIGN KEY constraint failed",
                    "code": "SQLITE_CONSTRAINT_FOREIGNKEY"
                  }
                }
              ]
            }
            """;

        const string rollbackResponseJson = """
            {
              "baton": "stream.3",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string closeResponseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(beginResponseJson, commitErrorResponseJson, rollbackResponseJson, closeResponseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var transaction = connection.BeginTransaction();
        transaction.Invoking(x => x.Commit())
            .Should().Throw<TursoException>()
            .WithMessage("Remote SQL execution failed: FOREIGN KEY constraint failed (SQLITE_CONSTRAINT_FOREIGNKEY)");

        transaction.Rollback();

        using var rollbackDocument = JsonDocument.Parse(handler.RequestBodies[2]);
        rollbackDocument.RootElement.GetProperty("baton").GetString().Should().Be("stream.2");
        rollbackDocument.RootElement.GetProperty("requests")[0].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("ROLLBACK");
    }

    [Test]
    public void TestRemoteCommitTransportFailureInvalidatesConnection()
    {
        const string beginResponseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(beginResponseJson, "not json");
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var transaction = connection.BeginTransaction();
        transaction.Invoking(x => x.Commit())
            .Should().Throw<TursoException>()
            .WithMessage("Unable to parse remote response:*");
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Test]
    public void TestRemoteBeginTransportFailureInvalidatesConnection()
    {
        using var handler = new CapturingHandler("not json");
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=True",
            remoteClient);

        connection.Invoking(x => x.BeginTransaction())
            .Should().Throw<TursoException>()
            .WithMessage("Unable to parse remote response:*");
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Test]
    public void TestRemoteInTransactionTransportFailureInvalidatesConnection()
    {
        const string beginResponseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(beginResponseJson, "not json");
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=True",
            remoteClient);

        var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO t VALUES (1)";

        command.Invoking(x => x.ExecuteNonQuery())
            .Should().Throw<TursoException>()
            .WithMessage("Unable to parse remote response:*");
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
        transaction.Invoking(x => x.Dispose()).Should().NotThrow();
    }

    [Test]
    public void TestRemoteRollbackFailureInvalidatesConnection()
    {
        const string beginResponseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "execute",
                    "result": {
                      "cols": [],
                      "rows": [],
                      "affected_row_count": 0,
                      "last_insert_rowid": null
                    }
                  }
                }
              ]
            }
            """;

        const string rollbackErrorResponseJson = """
            {
              "baton": "stream.2",
              "results": [
                {
                  "type": "error",
                  "error": {
                    "message": "rollback failed",
                    "code": "ROLLBACK_FAILED"
                  }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(beginResponseJson, rollbackErrorResponseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var transaction = connection.BeginTransaction();
        transaction.Invoking(x => x.Rollback())
            .Should().Throw<TursoException>()
            .WithMessage("Remote SQL execution failed: rollback failed (ROLLBACK_FAILED)");
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Test]
    public void TestRemoteBatchSerializesStatementsAndReadsMultipleResults()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "batch",
                    "result": {
                      "step_results": [
                        {
                          "cols": [
                            { "name": "n", "decltype": "INTEGER" }
                          ],
                          "rows": [
                            [
                              { "type": "integer", "value": "7" }
                            ]
                          ],
                          "affected_row_count": 0,
                          "last_insert_rowid": null
                        },
                        {
                          "cols": [],
                          "rows": [],
                          "affected_row_count": 2,
                          "last_insert_rowid": "3"
                        }
                      ],
                      "step_errors": [null, null]
                    }
                  }
                },
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var batch = (TursoBatch)connection.CreateBatch();
        var select = (TursoBatchCommand)batch.CreateBatchCommand();
        select.CommandText = "SELECT ?";
        select.Parameters.Add(7);
        batch.BatchCommands.Add(select);

        var insert = (TursoBatchCommand)batch.CreateBatchCommand();
        insert.CommandText = "INSERT INTO t VALUES (:name), (:other)";
        insert.Parameters.AddWithValue(":name", "alice");
        insert.Parameters.AddWithValue(":other", "bob");
        batch.BatchCommands.Add(insert);

        using var reader = batch.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(7);
        reader.NextResult().Should().BeTrue();
        reader.Read().Should().BeFalse();
        reader.RecordsAffected.Should().Be(2);
        select.RecordsAffected.Should().Be(0);
        insert.RecordsAffected.Should().Be(2);

        using var document = JsonDocument.Parse(handler.RequestBody);
        var requests = document.RootElement.GetProperty("requests");
        requests.GetArrayLength().Should().Be(2);
        requests[0].GetProperty("type").GetString().Should().Be("batch");
        var steps = requests[0].GetProperty("batch").GetProperty("steps");
        steps.GetArrayLength().Should().Be(2);
        steps[0].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("SELECT ?");
        steps[0].GetProperty("stmt").GetProperty("args")[0].GetProperty("value").GetString().Should().Be("7");
        steps[1].GetProperty("stmt").GetProperty("named_args")[0].GetProperty("name").GetString().Should().Be(":name");
        requests[1].GetProperty("type").GetString().Should().Be("close");
    }

    [Test]
    public void TestRemoteBatchNonQueryUsesNoRowsAndIgnoresTrailingCloseError()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "batch",
                    "result": {
                      "step_results": [
                        {
                          "cols": [],
                          "rows": [],
                          "affected_row_count": 3,
                          "last_insert_rowid": "3"
                        }
                      ],
                      "step_errors": [null]
                    }
                  }
                },
                {
                  "type": "error",
                  "error": {
                    "message": "close failed",
                    "code": "CLOSE_FAILED"
                  }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=False",
            remoteClient);

        using var batch = (TursoBatch)connection.CreateBatch();
        var command = (TursoBatchCommand)batch.CreateBatchCommand();
        command.CommandText = "INSERT INTO t VALUES (1), (2), (3)";
        batch.BatchCommands.Add(command);

        batch.ExecuteNonQuery().Should().Be(3);
        command.RecordsAffected.Should().Be(3);

        using var document = JsonDocument.Parse(handler.RequestBody);
        var requests = document.RootElement.GetProperty("requests");
        requests.GetArrayLength().Should().Be(2);
        requests[0].GetProperty("type").GetString().Should().Be("batch");
        var steps = requests[0].GetProperty("batch").GetProperty("steps");
        steps.GetArrayLength().Should().Be(1);
        steps[0].GetProperty("stmt").GetProperty("want_rows").GetBoolean().Should().BeFalse();
        requests[1].GetProperty("type").GetString().Should().Be("close");
    }

    [Test]
    public void TestRemoteBatchSurfacesStepErrors()
    {
        const string responseJson = """
            {
              "baton": "stream.1",
              "results": [
                {
                  "type": "ok",
                  "response": {
                    "type": "batch",
                    "result": {
                      "step_results": [null],
                      "step_errors": [
                        {
                          "message": "no such table: missing",
                          "code": "SQLITE_ERROR"
                        }
                      ]
                    }
                  }
                }
              ]
            }
            """;

        const string closeResponseJson = """
            {
              "results": [
                {
                  "type": "ok",
                  "response": { "type": "close" }
                }
              ]
            }
            """;

        using var handler = new CapturingHandler(responseJson, closeResponseJson);
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Read Your Writes=True",
            remoteClient);

        using var batch = (TursoBatch)connection.CreateBatch();
        var command = (TursoBatchCommand)batch.CreateBatchCommand();
        command.CommandText = "SELECT * FROM missing";
        batch.BatchCommands.Add(command);

        batch.Invoking(x => x.ExecuteNonQuery())
            .Should().Throw<TursoException>()
            .WithMessage("Remote SQL execution failed: no such table: missing (SQLITE_ERROR)");
    }

    [Test]
    public void TestSyncRequiresReplicaConnection()
    {
        using var connection = new TursoConnection("Data Source=http://localhost:8080");
        connection.Open();

        connection.Invoking(x => x.Sync())
            .Should().Throw<NotSupportedException>()
            .WithMessage("Sync requires an embedded replica connection.");
    }

    [Test]
    public async Task TestSyncAsyncReportsReplicaUnsupported()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var remoteClient = new TursoRemoteClient(
            httpClient,
            new Uri("http://localhost:8080"),
            authToken: null,
            disposeHttpClient: false);
        using var connection = new TursoConnection(
            "Data Source=http://localhost:8080;Replica Path=replica.db",
            remoteClient);

        var act = async () => await connection.SyncAsync();
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("Embedded replica sync is not supported yet by the .NET provider.");
    }

    private sealed class CapturingHandler : HttpMessageHandler, IDisposable
    {
        private readonly Queue<string> _responseJson;

        public CapturingHandler(params string[] responseJson)
        {
            _responseJson = new Queue<string>(responseJson);
        }

        public Uri? RequestUri { get; private set; }

        public string? Authorization { get; private set; }

        public string RequestBody { get; private set; } = "";

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responseJson.Count == 0)
                throw new InvalidOperationException("No fake HTTP response is available.");

            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(RequestBody);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
