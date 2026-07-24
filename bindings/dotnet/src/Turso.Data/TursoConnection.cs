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
    private ManagedConnectionPoolLease? _managedPoolLease;
    private ManagedConnectionPoolKey? _managedPoolKey;
    private TursoRemoteClient? _remoteClient;
    private TursoConnectionOptions _connectionOptions;
    private TursoReplicaOptions? _replicaOptions;
    private HttpMessageHandler? _ownedReplicaHttpHandler;
    private TursoEncryptionFileSystem? _managedEncryptionFileSystem;
    private bool _disposed;
    private bool _readUncommitted;
    private bool _managedSharedMemory;
    private bool _remoteTransactionActive;
    private bool _managedReadOnly;
    private readonly HashSet<TursoDataReader> _openReaders = [];
    private readonly object _readerLock = new();
    private readonly HashSet<TursoCommand> _openCommands = [];
    private TursoTransaction? _transaction;

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionOptions.GetConnectionString();
        set
        {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException("ConnectionString cannot be set while the connection is open.");

            _connectionOptions = TursoConnectionOptions.Parse(value ?? string.Empty);
            _managedPoolKey = null;
            _replicaOptions = null;
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

    /// <summary>
    /// Creates a connection configured as an embedded replica.
    /// </summary>
    /// <param name="replicaOptions">The embedded replica configuration.</param>
    public static TursoConnection CreateReplica(TursoReplicaOptions replicaOptions)
    {
        ArgumentNullException.ThrowIfNull(replicaOptions);
        replicaOptions.Validate();
        var ownedHttpHandler = replicaOptions.HttpPolicy.ClaimMessageHandlerOwnership();
        var connectionReplicaOptions = replicaOptions.CloneForConnection();
        return new TursoConnection
        {
            _replicaOptions = connectionReplicaOptions,
            _connectionOptions = TursoConnectionOptions.FromReplica(connectionReplicaOptions),
            _ownedReplicaHttpHandler = ownedHttpHandler,
        };
    }

    public override void Open()
    {
        ValidateCanOpen();
        OpenCore();
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (_connectionOptions.IsRemote && _connectionOptions.IsReplica)
        {
            ValidateCanOpen();
            ValidateReplicaLocalProvider();
            return OpenRemoteReplicaAsync(GetReplicaOptions(), cancellationToken);
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Open();
                if (!cancellationToken.IsCancellationRequested)
                    return;

                Close();
                cancellationToken.ThrowIfCancellationRequested();
            },
            CancellationToken.None);
    }

    public static void ClearAllPools() => ManagedConnectionPool.ClearAll();

    public static void ClearPool(TursoConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection._managedPoolKey is { } key
            || connection._connectionOptions.TryGetManagedPoolKey(out key))
        {
            ManagedConnectionPool.Clear(key);
        }
    }

    public override void Close()
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: true);
        if (_remoteClient is not null)
        {
            CloseRemote();
            return;
        }

        _replicaDatabase?.EnsureCanClose();
        var cancellationError = _replicaDatabase?.CancelPendingOperationsForClose();
        try
        {
            var nativeDatabase = _nativeDatabase;
            var managedDatabase = _managedDatabase;
            var managedPoolLease = _managedPoolLease;
            var managedEncryptionFileSystem = _managedEncryptionFileSystem;
            var reusable = false;
            try
            {
                CloseOpenReaders();
                _transaction?.Dispose();
                ResetOpenCommands();
                reusable = true;
            }
            finally
            {
                _nativeDatabase = null;
                _replicaDatabase = null;
                _managedDatabase = null;
                _managedPoolLease = null;
                _managedEncryptionFileSystem = null;
                try
                {
                    nativeDatabase?.Dispose();
                }
                finally
                {
                    try
                    {
                        if (managedPoolLease is not null)
                            managedPoolLease.Release(reusable);
                        else
                            managedDatabase?.Dispose();
                    }
                    finally
                    {
                        managedEncryptionFileSystem?.Dispose();
                        _readUncommitted = false;
                        _managedSharedMemory = false;
                        _managedReadOnly = false;
                    }
                }
            }
        }
        catch (Exception cleanupError) when (cancellationError is not null)
        {
            throw new AggregateException(
                "Embedded replica cancellation and connection cleanup both failed.",
                cancellationError,
                cleanupError);
        }

        if (cancellationError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationError).Throw();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            _disposed = true;
            base.Dispose(disposing);
            return;
        }

        Exception? disposalError = null;
        try
        {
            Close();
        }
        catch (Exception exception)
        {
            if (State != ConnectionState.Closed)
                throw;
            disposalError = exception;
        }

        _disposed = true;
        var ownedReplicaHttpHandler = _ownedReplicaHttpHandler;
        _ownedReplicaHttpHandler = null;
        try
        {
            ownedReplicaHttpHandler?.Dispose();
        }
        catch (Exception exception)
        {
            disposalError = CombineDisposalErrors(disposalError, exception);
        }

        try
        {
            base.Dispose(disposing);
        }
        catch (Exception exception)
        {
            disposalError = CombineDisposalErrors(disposalError, exception);
        }

        if (disposalError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalError).Throw();
    }

    private static Exception CombineDisposalErrors(Exception? existing, Exception next)
        => existing is null
            ? next
            : new AggregateException("Multiple errors occurred while disposing the Turso connection.", existing, next);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
        if (_nativeDatabase is null && _managedDatabase is null && _remoteClient is null)
        {
            throw new InvalidOperationException("Turso database is closed.");
        }

        if (_transaction is not null)
            throw new InvalidOperationException("Parallel transactions are not supported.");

        return _transaction = new TursoTransaction(this, isolationLevel);
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

    public TursoSyncResult Sync(TursoSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SyncAsync(options, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task SyncAsync(CancellationToken cancellationToken = default)
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
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

    public Task<TursoSyncResult> SyncAsync(
        TursoSyncOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<TursoSyncResult>(cancellationToken);
        if (State != ConnectionState.Open)
            throw new InvalidOperationException("Turso database is closed.");
        if (!_connectionOptions.IsReplica)
            throw new NotSupportedException("Sync requires an embedded replica connection.");

        return (_replicaDatabase ?? throw new InvalidOperationException("Turso database is closed."))
            .SyncAsync(options, cancellationToken);
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
        set
        {
            if (value && _managedSharedMemory)
                throw new NotSupportedException(ManagedSharedCacheContract.ReadUncommittedNotSupportedMessage);

            _readUncommitted = value;
        }
    }

    internal TursoNativeDatabase NativeDatabase
    {
        get
        {
            _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
            return _nativeDatabase ?? throw new InvalidOperationException("Turso database is closed.");
        }
    }

    internal IManagedConnectionAdapter ManagedConnection
        => _managedDatabase?.Connection ?? throw new InvalidOperationException("Turso database is closed.");

    internal void ReaderOpened(TursoDataReader reader)
    {
        lock (_readerLock)
            _openReaders.Add(reader);
    }

    internal void ReaderClosed(TursoDataReader reader)
    {
        lock (_readerLock)
            _openReaders.Remove(reader);
    }

    internal void CommandOpened(TursoCommand command) => _openCommands.Add(command);

    internal void CommandClosed(TursoCommand command) => _openCommands.Remove(command);

    internal void TransactionCompleted(TursoTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction))
            _transaction = null;
    }

    internal void TransactionCompletedExternally()
    {
        _remoteTransactionActive = false;
        _transaction?.MarkCompletedExternally();
        CloseRemoteSessionIfStateless();
    }

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
        ValidateReplicaLocalProvider();

        if (_connectionOptions.IsReplica)
        {
            SetReplicaDatabase(TursoReplicaProvider.OpenReplica(GetReplicaOptions()));
            return;
        }

        if (_connectionOptions.GetEncryptionCipher().HasValue || !string.IsNullOrWhiteSpace(_connectionOptions["Encryption Key"]))
            throw new InvalidOperationException("Encryption Cipher and Encryption Key are local database options and cannot be used with remote Turso URLs.");

        _remoteClient = new TursoRemoteClient(_connectionOptions.GetRemoteUri(), _connectionOptions.AuthToken);
    }

    private async Task OpenRemoteReplicaAsync(
        TursoReplicaOptions options,
        CancellationToken cancellationToken)
    {
        var ValidateRemoteLocalProvider = await TursoReplicaProvider
            .OpenReplicaAsync(options, cancellationToken)
            .ConfigureAwait(false);
        SetReplicaDatabase(ValidateRemoteLocalProvider);
    }

    private void ValidateReplicaLocalProvider()
    {
        if (_connectionOptions.LocalProvider == TursoLocalProvider.Managed)
            throw new NotSupportedException("Local Provider=Managed is supported only for local database connections.");
    }

    private TursoReplicaOptions GetReplicaOptions()
    {
        if (_connectionOptions.GetEncryptionCipher().HasValue
            || !string.IsNullOrWhiteSpace(_connectionOptions["Encryption Key"]))
        {
            throw new InvalidOperationException(
                "Encryption Cipher and Encryption Key are local database options and cannot be used with remote Turso URLs.");
        }

        return _replicaOptions ?? new TursoReplicaOptions(
            _connectionOptions.ReplicaPath,
            _connectionOptions.GetRemoteUri(),
            _connectionOptions.AuthToken);
    }

    private void SetReplicaDatabase(TursoReplicaDatabase replicaDatabase)
    {
        if (_disposed)
        {
            replicaDatabase.Dispose();
            throw new ObjectDisposedException(nameof(TursoConnection));
        }

        _replicaDatabase = replicaDatabase;
        _nativeDatabase = replicaDatabase;
    }

    private void ValidateCanOpen()
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nativeDatabase is not null || _managedDatabase is not null || _remoteClient is not null)
            throw new InvalidOperationException("The connection is already open.");
        ValidateAutomaticSyncPolicy();
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
    }

    private void ValidateAutomaticSyncPolicy()
    {
        var syncInterval = _connectionOptions.SyncInterval;
        if (syncInterval < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TursoConnectionStringBuilder.SyncInterval),
                syncInterval,
                "Sync Interval cannot be negative.");
        }

        if (syncInterval > 0)
        {
            throw new NotSupportedException(
                "Automatic synchronization is not supported. Sync Interval must be 0. Call Sync or SyncAsync explicitly.");
        }
    }

    private void OpenCore()
    {
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

    private void ValidateLocalOnlyOptions()
    {
        if (!string.IsNullOrWhiteSpace(_connectionOptions.AuthToken))
            throw new InvalidOperationException("Auth Token requires a remote Turso URL Data Source.");
        if (!string.IsNullOrWhiteSpace(_connectionOptions.ReplicaPath))
            throw new InvalidOperationException("Replica Path requires a remote Turso URL Data Source.");
        if (_connectionOptions.Tls.HasValue)
            throw new InvalidOperationException("Tls requires a remote Turso URL Data Source.");
    }

    private void OpenManagedDatabase(ManagedLocalOpenOptions options)
    {
        if (options.SharedMemoryName is not null)
        {
            _managedDatabase = ManagedSharedMemoryDatabase.Open(options.SharedMemoryName);
            _managedSharedMemory = true;
        }
        else if (_connectionOptions.Pooling
            && options.Encryption is null
            && !options.DataSource.Equals(":memory:", StringComparison.Ordinal))
        {
            var poolKey = ManagedConnectionPoolKey.Create(options.DataSource, options.ReadOnly);
            _managedPoolLease = ManagedConnectionPool.Rent(
                poolKey,
                () => OpenUnencryptedManagedDatabase(poolKey.DataSource, options.ReadOnly));
            _managedDatabase = _managedPoolLease.Database;
            _managedPoolKey = poolKey;
        }
        else if (options.Encryption is null && !options.ReadOnly)
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

    private static IManagedDatabaseAdapter OpenUnencryptedManagedDatabase(string dataSource, bool readOnly)
    {
        var managedDatabase = readOnly
            ? ManagedDatabaseAdapter.OpenFile(dataSource, PhysicalFileSystem.Instance, readOnly: true)
            : ManagedDatabaseAdapter.Open(dataSource);
        try
        {
            _ = managedDatabase.Connect();
            return managedDatabase;
        }
        catch
        {
            managedDatabase.Dispose();
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
            CloseOpenReaders();
            ResetOpenCommands();
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
            _transaction?.Dispose();
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
        TursoDataReader[] readers;
        lock (_readerLock)
            readers = _openReaders.ToArray();
        foreach (var reader in readers)
            reader.CloseFromConnection();
    }

    private void ResetOpenCommands()
    {
        foreach (var command in _openCommands.ToArray())
            command.ResetFromConnection();
    }
}
