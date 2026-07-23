using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso;

public class TursoConnection : DbConnection
{
    private TursoNativeDatabase? _nativeDatabase;
    private TursoReplicaDatabase? _replicaDatabase;
    private IManagedDatabaseAdapter? _managedDatabase;
    private TursoRemoteClient? _remoteClient;
    private TursoConnectionOptions _connectionOptions;
    private TursoEncryptionFileSystem? _managedEncryptionFileSystem;
    private bool _disposed;
    private bool _readUncommitted;
    private bool _remoteTransactionActive;
    private bool _managedReadOnly;
    private readonly HashSet<TursoDataReader> _openReaders = [];

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionOptions.GetConnectionString();
        set
        {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException("ConnectionString cannot be set while the connection is open.");

            _connectionOptions = TursoConnectionOptions.Parse(value ?? string.Empty);
        }
    }

    public override string Database => "main";

    public override string DataSource => _connectionOptions["Data Source"] ?? "";

    public override string ServerVersion => typeof(TursoConnection).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public override ConnectionState State => _nativeDatabase is not null || _managedDatabase is not null || _remoteClient is not null
        ? ConnectionState.Open
        : ConnectionState.Closed;

    public override bool CanCreateBatch => _connectionOptions.IsRemote && !_connectionOptions.IsReplica;

    protected override DbProviderFactory DbProviderFactory => TursoFactory.Instance;

    public TursoConnection() : this("")
    {
    }

    public TursoConnection(string connectionString)
    {
        _connectionOptions = TursoConnectionOptions.Parse(connectionString);
    }

    public override void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nativeDatabase is not null || _managedDatabase is not null || _remoteClient is not null)
            throw new InvalidOperationException("The connection is already open.");
        if (!string.IsNullOrWhiteSpace(_connectionOptions["Password"]))
        {
            if (!_connectionOptions.IsRemote && _connectionOptions.LocalProvider == TursoLocalProvider.Managed)
            {
                throw new NotSupportedException(
                    "Password is not supported when Local Provider=Managed because the managed engine does not provide encryption.");
            }

            throw new NotSupportedException(
                "Password is not supported. Use Encryption Cipher and Encryption Key for local encrypted databases.");
        }

        if (_connectionOptions.IsRemote)
        {
            OpenRemote();
            return;
        }

        ValidateLocalOnlyOptions();

        if (_connectionOptions.LocalProvider == TursoLocalProvider.Managed)
        {
            using var managedOptions = _connectionOptions.GetManagedLocalOpenOptions();
            OpenManagedDatabase(managedOptions);

            return;
        }

        var filename = _connectionOptions["Data Source"] ?? ":memory:";
        var cipher = _connectionOptions.GetEncryptionCipher();
        var hexkey = _connectionOptions["Encryption Key"];

        if (cipher.HasValue)
        {
            if (string.IsNullOrWhiteSpace(hexkey))
                throw new InvalidOperationException("Encryption Key is required when Encryption Cipher is specified.");

            _nativeDatabase = TursoNativeProvider.OpenDatabase(filename, cipher, hexkey);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(hexkey))
                throw new InvalidOperationException("Encryption Cipher is required when Encryption Key is specified.");

            _nativeDatabase = TursoNativeProvider.OpenDatabase(filename, cipher: null, encryptionKey: null);
        }
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        Open();
        return Task.CompletedTask;
    }

    public override void Close()
    {
        if (_remoteClient is not null)
        {
            CloseRemote();
            return;
        }

        var nativeDatabase = _nativeDatabase;
        var managedDatabase = _managedDatabase;
        var managedEncryptionFileSystem = _managedEncryptionFileSystem;
        try
        {
            CloseOpenReaders();
        }
        finally
        {
            _nativeDatabase = null;
            _replicaDatabase = null;
            _managedDatabase = null;
            _managedEncryptionFileSystem = null;
            try
            {
                nativeDatabase?.Dispose();
            }
            finally
            {
                try
                {
                    managedDatabase?.Dispose();
                }
                finally
                {
                    managedEncryptionFileSystem?.Dispose();
                    _readUncommitted = false;
                    _managedReadOnly = false;
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();

        _disposed = true;
        base.Dispose(disposing);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_nativeDatabase is null && _managedDatabase is null && _remoteClient is null)
        {
            throw new InvalidOperationException("Turso database is closed.");
        }

        return new TursoTransaction(this, isolationLevel);
    }

    protected override DbCommand CreateDbCommand()
    {
        return new TursoCommand(this);
    }

    protected override DbBatch CreateDbBatch()
    {
        if (!CanCreateBatch)
            throw new NotSupportedException("Turso batch execution is currently supported only for remote connections.");

        return new TursoBatch(this);
    }

    public int ExecuteNonQuery(string sql)
    {
        using var command = CreateCommand();
        command.CommandText = sql;

        return command.ExecuteNonQuery();
    }

    public void Sync()
    {
        SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task SyncAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        if (State != ConnectionState.Open)
            throw new InvalidOperationException("Turso database is closed.");
        if (!_connectionOptions.IsReplica)
            throw new NotSupportedException("Sync requires an embedded replica connection.");

        return (_replicaDatabase ?? throw new InvalidOperationException("Turso database is closed."))
            .SyncAsync(cancellationToken);
    }

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("Turso does not support changing the active database.");
    }

    internal int DefaultTimeout => _connectionOptions.DefaultTimeout;

    internal bool IsRemote => _remoteClient is not null;

    internal bool IsManagedReadOnly => _managedReadOnly;

    internal bool IsManaged => _managedDatabase is not null;

    internal bool ReadUncommitted
    {
        get => _readUncommitted;
        set => _readUncommitted = value;
    }

    internal TursoNativeDatabase NativeDatabase
        => _nativeDatabase ?? throw new InvalidOperationException("Turso database is closed.");

    internal IManagedConnectionAdapter ManagedConnection
        => _managedDatabase?.Connection ?? throw new InvalidOperationException("Turso database is closed.");

    internal void ReaderOpened(TursoDataReader reader) => _openReaders.Add(reader);

    internal void ReaderClosed(TursoDataReader reader) => _openReaders.Remove(reader);

    internal async Task<RemoteStatementResult> ExecuteRemoteAsync(
        string sql,
        TursoParameterCollection parameters,
        bool wantRows,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Turso database is closed.");
        var closeAfter = !_connectionOptions.ReadYourWrites && !_remoteTransactionActive;
        try
        {
            return await remoteClient.ExecuteAsync(sql, parameters, wantRows, commandTimeout, closeAfter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TursoRemoteSqlException)
        {
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal async Task<IReadOnlyList<RemoteStatementResult>> ExecuteRemoteBatchAsync(
        IReadOnlyList<TursoBatchCommand> batchCommands,
        int commandTimeout,
        bool wantRows,
        CancellationToken cancellationToken)
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Turso database is closed.");
        var closeAfter = !_connectionOptions.ReadYourWrites && !_remoteTransactionActive;
        try
        {
            return await remoteClient.ExecuteBatchAsync(batchCommands, commandTimeout, wantRows, closeAfter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TursoRemoteSqlException)
        {
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal void BeginRemoteTransaction(IsolationLevel isolationLevel)
    {
        _ = isolationLevel;
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Turso database is closed.");
        if (_remoteTransactionActive)
            throw new InvalidOperationException("A transaction is already active on this connection.");

        _remoteTransactionActive = true;
        try
        {
            remoteClient
                .ExecuteAsync("BEGIN", new TursoParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (TursoRemoteSqlException)
        {
            _remoteTransactionActive = false;
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal void CommitRemoteTransaction()
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Turso database is closed.");
        if (!_remoteTransactionActive)
            throw new InvalidOperationException("No remote transaction is active on this connection.");

        try
        {
            remoteClient
                .ExecuteAsync("COMMIT", new TursoParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (TursoRemoteSqlException)
        {
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }

        _remoteTransactionActive = false;
    }

    internal void RollbackRemoteTransaction()
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Turso database is closed.");
        if (!_remoteTransactionActive)
            throw new InvalidOperationException("No remote transaction is active on this connection.");

        try
        {
            remoteClient
                .ExecuteAsync("ROLLBACK", new TursoParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            _remoteTransactionActive = false;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal void CloseRemoteSessionIfStateless()
    {
        if (_connectionOptions.ReadYourWrites || _remoteClient is not { HasOpenSession: true } remoteClient)
            return;

        try
        {
            remoteClient.CloseAsync(DefaultTimeout, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            InvalidateRemoteSession();
        }
    }

    private void OpenRemote()
    {
        if (_connectionOptions.LocalProvider == TursoLocalProvider.Managed)
            throw new NotSupportedException("Local Provider=Managed is supported only for local database connections.");

        if (_connectionOptions.IsReplica)
        {
            if (_connectionOptions.SyncInterval > 0)
            {
                throw new NotSupportedException(
                    "Sync Interval is not supported yet for embedded replica connections. Call Sync or SyncAsync explicitly.");
            }

            if (_connectionOptions.GetEncryptionCipher().HasValue
                || !string.IsNullOrWhiteSpace(_connectionOptions["Encryption Key"]))
            {
                throw new InvalidOperationException(
                    "Encryption Cipher and Encryption Key are local database options and cannot be used with remote Turso URLs.");
            }

            var replicaDatabase = TursoReplicaProvider.OpenReplica(
                new TursoReplicaOptions(
                    _connectionOptions.ReplicaPath,
                    _connectionOptions.GetRemoteUri(),
                    _connectionOptions.AuthToken));
            _replicaDatabase = replicaDatabase;
            _nativeDatabase = replicaDatabase;
            return;
        }

        if (_connectionOptions.SyncInterval > 0)
            throw new NotSupportedException("Sync Interval requires embedded replica support, which is not supported yet by the .NET provider.");

        if (_connectionOptions.GetEncryptionCipher().HasValue || !string.IsNullOrWhiteSpace(_connectionOptions["Encryption Key"]))
            throw new InvalidOperationException("Encryption Cipher and Encryption Key are local database options and cannot be used with remote Turso URLs.");

        _remoteClient = new TursoRemoteClient(_connectionOptions.GetRemoteUri(), _connectionOptions.AuthToken);
    }

    private void ValidateLocalOnlyOptions()
    {
        if (!string.IsNullOrWhiteSpace(_connectionOptions.AuthToken))
            throw new InvalidOperationException("Auth Token requires a remote Turso URL Data Source.");
        if (!string.IsNullOrWhiteSpace(_connectionOptions.ReplicaPath))
            throw new InvalidOperationException("Replica Path requires a remote Turso URL Data Source.");
        if (_connectionOptions.SyncInterval > 0)
            throw new InvalidOperationException("Sync Interval requires a remote embedded replica connection.");
        if (_connectionOptions.Tls.HasValue)
            throw new InvalidOperationException("Tls requires a remote Turso URL Data Source.");
    }

    private void OpenManagedDatabase(ManagedLocalOpenOptions options)
    {
        if (options.Encryption is null && !options.ReadOnly)
        {
            var managedDatabase = ManagedDatabaseAdapter.Open(options.DataSource);
            try
            {
                _ = managedDatabase.Connect();
                _managedDatabase = managedDatabase;
            }
            catch
            {
                managedDatabase.Dispose();
                throw;
            }
        }
        else
        {
            TursoEncryptionFileSystem? managedEncryptionFileSystem = null;
            IManagedDatabaseAdapter? managedDatabase = null;
            try
            {
                IFileSystem fileSystem = PhysicalFileSystem.Instance;
                if (options.Encryption is not null)
                {
                    managedEncryptionFileSystem = new TursoEncryptionFileSystem(
                        PhysicalFileSystem.Instance,
                        options.Encryption);
                    fileSystem = managedEncryptionFileSystem;
                }

                managedDatabase = ManagedDatabaseAdapter.OpenFile(
                    options.DataSource,
                    fileSystem,
                    readOnly: options.ReadOnly);
                try
                {
                    _ = managedDatabase.Connect();
                    _managedDatabase = managedDatabase;
                    managedDatabase = null;
                    _managedEncryptionFileSystem = managedEncryptionFileSystem;
                    managedEncryptionFileSystem = null;
                }
                catch
                {
                    throw;
                }
            }
            finally
            {
                managedDatabase?.Dispose();
                managedEncryptionFileSystem?.Dispose();
            }
        }

        if (!options.ReadOnly)
            return;

        try
        {
            using var command = CreateCommand();
            command.CommandText = "PRAGMA query_only = ON;";
            command.ExecuteNonQuery();
            _managedReadOnly = true;
        }
        catch
        {
            Close();
            throw;
        }
    }

    private void CloseRemote()
    {
        var remoteClient = _remoteClient;
        if (remoteClient is null)
            return;

        Exception? closeError = null;
        try
        {
            if (_remoteTransactionActive)
            {
                remoteClient
                    .ExecuteAsync("ROLLBACK", new TursoParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: true, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                remoteClient.CloseAsync(DefaultTimeout, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            closeError = ex;
        }
        finally
        {
            remoteClient.Dispose();
            _remoteClient = null;
            _remoteTransactionActive = false;
            _readUncommitted = false;
            _managedReadOnly = false;
        }

        if (closeError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeError).Throw();
    }

    private void InvalidateRemoteSession()
    {
        _remoteClient?.Dispose();
        _remoteClient = null;
        _remoteTransactionActive = false;
        _readUncommitted = false;
    }

    private void CloseOpenReaders()
    {
        foreach (var reader in _openReaders.ToArray())
            reader.CloseFromConnection();
    }
}
