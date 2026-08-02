using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ahtola;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite;

public partial class SqliteConnection : DbConnection, ILocalReaderConnection
{
    private const int SQLITE_ERROR = 1;
    private const int SQLITE_CANTOPEN = 14;
    private static readonly object SharedMemoryLock = new();
    private static readonly Dictionary<string, int> SharedMemoryReferences = new(StringComparer.OrdinalIgnoreCase);

    private AhtolaNativeDatabase? _database;
    private IManagedDatabaseAdapter? _managedDatabase;
    private ManagedConnectionPoolLease? _managedPoolLease;
    private SqliteConnectionStringBuilder _connectionOptions = new();
    private bool _disposed;
    private int? _defaultTimeout;
    private readonly HashSet<IConnectionOwnedReader> _openReaders = [];
    private readonly object _readerGate = new();
    private readonly ManualResetEventSlim _noOpenReaders = new(initialState: true);
    private readonly HashSet<SqliteBlob> _openManagedBlobs = [];
    private readonly HashSet<SqliteCommand> _openCommands = [];
    private string? _dataSource;
    private bool _readOnly;
    private AhtolaEncryptionFileSystem? _managedEncryptionFileSystem;
    private bool _recursiveTriggers;
    private bool _readUncommitted;
    private bool _managedSharedMemory;
    private string? _sharedMemoryPath;
    private bool _extensionsEnabled;
    private readonly List<(string File, string? Proc)> _pendingExtensions = [];

    public SqliteConnection()
    {
    }

    public SqliteConnection(string? connectionString)
    {
        ConnectionString = connectionString;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionOptions.ConnectionString;
        set
        {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException(Properties.Resources.ConnectionStringRequiresClosedConnection);

            _connectionOptions = new SqliteConnectionStringBuilder(value);
            _defaultTimeout = null;
        }
    }

    public override string Database => "main";

    public override string DataSource => _dataSource switch
    {
        // Native SQLite's sqlite3_db_filename returns "" for in-memory databases;
        // Microsoft.Data.Sqlite exposes that empty string via DataSource. EFCore's
        // SqliteDatabaseCreator.Delete branches on !string.IsNullOrEmpty(path) and
        // would otherwise try File.Delete(":memory:"), so match the contract.
        ":memory:" => string.Empty,
        null => _connectionOptions.DataSource,
        var path => path,
    };

    public int DefaultTimeout
    {
        get => _defaultTimeout ?? _connectionOptions.DefaultTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _defaultTimeout = value;
        }
    }

    /// <summary>
    ///     Raw SQLitePCL <c>sqlite3*</c> interop is not supported by the Ahtola-backed provider.
    /// </summary>
    public virtual dynamic? Handle => null;

    public SqliteTransaction? Transaction { get; internal set; }

    internal bool IsSharedCache => _connectionOptions.Cache == SqliteCacheMode.Shared;

    internal bool ReadUncommitted
    {
        get => _readUncommitted;
        set
        {
            if (value && _managedSharedMemory)
                throw new NotSupportedException(Properties.Resources.ManagedSharedCacheReadUncommittedNotSupported);

            _readUncommitted = value;
        }
    }

    /// <summary>
    /// The SQLite version this engine is wire- and SQL-compatible with. Mirrors what
    /// <c>sqlite_version()</c> returns, so callers that feature-gate on the server
    /// version (e.g. EFCore) see the real compatibility level rather than a stub.
    /// </summary>
    public override string ServerVersion => Ahtola.Core.EmbeddedDatabase.SqliteCompatibilityVersion;

    public override ConnectionState State => _database is null && _managedDatabase is null
        ? ConnectionState.Closed
        : ConnectionState.Open;

    public AhtolaConnectionCapabilities Capabilities
        => AhtolaConnectionCapabilities.ForSqlite(_connectionOptions.EffectiveLocalProvider);

    public override bool CanCreateBatch => Capabilities.CanCreateBatch;

    protected override DbProviderFactory DbProviderFactory => SqliteFactory.Instance;

    public override void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_database is not null || _managedDatabase is not null)
            throw new InvalidOperationException("The connection is already open.");
        if (AhtolaConnectionCapabilities.IsRemoteDataSource(_connectionOptions.DataSource))
        {
            throw new NotSupportedException(
                "Ahtola.Data.Sqlite supports only local database connections. Use AhtolaConnection for remote Hrana or embedded replica connections.");
        }
        ValidateManagedSharedCacheOptions();
        ValidateForeignReadOnlyOptions();
        if (!string.IsNullOrEmpty(_connectionOptions.Password))
            throw new InvalidOperationException(Properties.Resources.EncryptionNotSupported("e_sqlite3"));

        var originalState = State;
        var filename = NormalizeDataSource(_connectionOptions);
        var readOnly = _connectionOptions.Mode == SqliteOpenMode.ReadOnly;
        var managedSharedMemory = IsManagedSharedMemoryConfiguration(_connectionOptions);
        var sharedMemoryPath = IsNativeSharedMemory(_connectionOptions) ? RegisterSharedMemoryFile(filename) : null;
        AhtolaEncryptionOptions? managedEncryption = null;
        try
        {
            if (_connectionOptions.EffectiveLocalProvider == AhtolaLocalProvider.Managed)
            {
                managedEncryption = _connectionOptions.CreateManagedEncryptionOptions();
                if (managedEncryption is not null
                    && (_connectionOptions.Mode == SqliteOpenMode.Memory
                        || filename.Equals(":memory:", StringComparison.Ordinal)))
                {
                    throw new NotSupportedException(Properties.Resources.ManagedMemoryEncryptionNotSupported);
                }

                if (managedSharedMemory)
                {
                    _managedDatabase = ManagedSharedMemoryDatabase.Open(filename);
                    _managedSharedMemory = true;
                }
                else if (CanUseManagedPooling(filename, managedEncryption))
                {
                    var poolKey = ManagedConnectionPoolKey.Create(filename, readOnly);
                    _managedPoolLease = ManagedConnectionPool.Rent(
                        poolKey,
                        () => OpenManagedDatabase(filename, readOnly, encryption: null, out _));
                    _managedDatabase = _managedPoolLease.Database;
                }
                else
                {
                    _managedDatabase = OpenManagedDatabase(
                        filename,
                        readOnly,
                        managedEncryption,
                        out var managedEncryptionFileSystem,
                        _connectionOptions.ForeignReadOnly);
                    _managedEncryptionFileSystem = managedEncryptionFileSystem;
                }
            }
            else
            {
                if (_connectionOptions.HasEncryptionOptions)
                {
                    throw new InvalidOperationException(
                        "Encryption Cipher and Encryption Key require Local Provider=Managed.");
                }

                _database = AhtolaNativeProvider.OpenDatabase(filename, cipher: null, encryptionKey: null);
            }

            _dataSource = filename;
            _readOnly = readOnly;
            _sharedMemoryPath = sharedMemoryPath;
            if (IsManagedReadOnly)
                EnableManagedReadOnly();
            ApplyExtensionSettings();
            ApplyConnectionOptions();
            RegisterScalarFunctions();
            RegisterAggregateFunctions();
            RegisterCollations();
            RegisterHooks();
            LoadPendingExtensions();
            OnStateChange(new StateChangeEventArgs(originalState, State));
        }
        catch (AhtolaException ex)
        {
            CleanupFailedOpen(sharedMemoryPath);
            throw ToSqliteException(ex);
        }
        catch (SqliteException)
        {
            CleanupFailedOpen(sharedMemoryPath);
            throw;
        }
        catch
        {
            CleanupFailedOpen(sharedMemoryPath);
            throw;
        }
        finally
        {
            managedEncryption?.Dispose();
        }
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

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

    public override void Close()
    {
        if (_database is null && _managedDatabase is null)
            return;

        var originalState = State;
        var reusable = false;
        try
        {
            CloseOpenManagedBlobs();
            CloseOpenReaders();
            Transaction?.Dispose();
            ResetOpenCommands();
            reusable = !HasManagedCallbacks;
        }
        finally
        {
            DisposeDatabaseAndManagedEncryptionFileSystem(reusable);
            _dataSource = null;
            _readOnly = false;
            _recursiveTriggers = false;
            _readUncommitted = false;
            _managedSharedMemory = false;
            if (_sharedMemoryPath is not null)
            {
                ReleaseSharedMemoryFile(_sharedMemoryPath);
                _sharedMemoryPath = null;
            }

            OnStateChange(new StateChangeEventArgs(originalState, State));
        }
    }

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("Changing databases is not supported.");
    }

    public override DataTable GetSchema()
        => GetSchema(DbMetaDataCollectionNames.MetaDataCollections, null);

    public override DataTable GetSchema(string collectionName)
        => GetSchema(collectionName, null);

    public override DataTable GetSchema(string collectionName, string?[]? restrictionValues)
        => AhtolaSchemaCollections.GetSchema(this, collectionName, restrictionValues);

    public static void ClearAllPools()
    {
        ManagedConnectionPool.ClearAll();
    }

    public static void ClearPool(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.TryGetManagedPoolKey(out var key))
            ManagedConnectionPool.Clear(key);
    }

    public new virtual SqliteTransaction BeginTransaction()
        => BeginTransaction(IsolationLevel.Unspecified);

    public virtual SqliteTransaction BeginTransaction(bool deferred)
        => BeginTransaction(IsolationLevel.Unspecified, deferred);

    public new virtual SqliteTransaction BeginTransaction(IsolationLevel isolationLevel)
        => BeginTransaction(isolationLevel, deferred: isolationLevel == IsolationLevel.ReadUncommitted);

    public virtual SqliteTransaction BeginTransaction(IsolationLevel isolationLevel, bool deferred)
    {
        if (State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection(nameof(BeginTransaction)));
        if (Transaction is not null)
            throw new InvalidOperationException(Properties.Resources.ParallelTransactionsNotSupported);

        Transaction = new SqliteTransaction(this, isolationLevel, deferred);
        return Transaction;
    }

    public virtual void CreateCollation(string name, Comparison<string>? comparison)
    {
        RegisterCollation(name, comparison is null ? null : (left, right) => comparison(left, right));
    }

    public virtual void CreateCollation<T>(string name, T state, Func<T, string, string, int>? comparison)
    {
        RegisterCollation(name, comparison is null ? null : (left, right) => comparison(state, left, right));
    }

    public virtual void CreateFunction<TResult>(string name, Func<TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 0, isDeterministic, function is null ? null : _ => function());
    }

    public virtual void CreateFunction<T1, TResult>(string name, Func<T1, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 1, isDeterministic, function is null ? null : args => InvokeTypedFunction(name, function, args));
    }

    public virtual void CreateFunction<T1, T2, TResult>(string name, Func<T1, T2, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 2, isDeterministic, function is null ? null : args => InvokeTypedFunction(name, function, args));
    }

    public virtual void CreateFunction<TState, TResult>(string name, TState state, Func<TState, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 0, isDeterministic, function is null ? null : args => InvokeTypedFunction(state, function, args));
    }

    public virtual void CreateFunction<TState, T1, TResult>(string name, TState state, Func<TState, T1, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 1, isDeterministic, function is null ? null : args => InvokeTypedFunction(name, state, function, args));
    }

    public virtual void CreateFunction<TState, T1, T2, TResult>(string name, TState state, Func<TState, T1, T2, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 2, isDeterministic, function is null ? null : args => InvokeTypedFunction(name, state, function, args));
    }

    public virtual void CreateFunction<TResult>(string name, Func<object?[], TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, -1, isDeterministic, function is null ? null : args => function(args));
    }

    public virtual void CreateFunction<TState, TResult>(string name, TState state, Func<TState, object?[], TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, -1, isDeterministic, function is null ? null : args => function(state, args));
    }

    public virtual void CreateAggregate<TAccumulate>(string name, Func<TAccumulate?, TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 0, isDeterministic, default(TAccumulate), func is null ? null : (accumulator, args) => InvokeNullableAggregateStep(func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<T1, TAccumulate>(string name, Func<TAccumulate?, T1, TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 1, isDeterministic, default(TAccumulate), func is null ? null : (accumulator, args) => InvokeNullableAggregateStep(name, func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<TAccumulate>(string name, Func<TAccumulate?, object?[], TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, -1, isDeterministic, default(TAccumulate), func is null ? null : (accumulator, args) => InvokeNullableAggregateStep(func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<TAccumulate>(string name, TAccumulate seed, Func<TAccumulate, TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 0, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<T1, TAccumulate>(string name, TAccumulate seed, Func<TAccumulate, T1, TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 1, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(name, func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<TAccumulate>(string name, TAccumulate seed, Func<TAccumulate, object?[], TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, -1, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<TAccumulate, TResult>(string name, TAccumulate seed, Func<TAccumulate, TAccumulate>? func, Func<TAccumulate, TResult>? resultSelector, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 0, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(func, accumulator, args), accumulator => InvokeResultSelector(resultSelector!, accumulator));
    }

    public virtual void CreateAggregate<T1, TAccumulate, TResult>(string name, TAccumulate seed, Func<TAccumulate, T1, TAccumulate>? func, Func<TAccumulate, TResult>? resultSelector, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 1, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(name, func, accumulator, args), accumulator => InvokeResultSelector(resultSelector!, accumulator));
    }

    public virtual void CreateAggregate<T1, T2, TAccumulate, TResult>(string name, TAccumulate seed, Func<TAccumulate, T1, T2, TAccumulate>? func, Func<TAccumulate, TResult>? resultSelector, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 2, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(name, func, accumulator, args), accumulator => InvokeResultSelector(resultSelector!, accumulator));
    }

    public virtual void CreateAggregate<TAccumulate, TResult>(string name, TAccumulate seed, Func<TAccumulate, object?[], TAccumulate>? func, Func<TAccumulate, TResult>? resultSelector, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, -1, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(func, accumulator, args), accumulator => InvokeResultSelector(resultSelector!, accumulator));
    }

    public virtual void EnableExtensions(bool enable = true)
    {
        if (enable && !Capabilities.SupportsExtensions)
            throw new NotSupportedException(Properties.Resources.ManagedExtensionsNotSupported);

        _extensionsEnabled = enable;
        if (_database is not null)
            SqliteNativeProvider.Current.EnableExtensions(NativeDatabase, enable);
    }

    public virtual void LoadExtension(string file, string? proc = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!Capabilities.SupportsExtensions)
            throw new NotSupportedException(Properties.Resources.ManagedExtensionsNotSupported);
        if (proc is not null)
            throw new NotSupportedException("Custom extension entry points are not yet supported by the Ahtola SQLite-compatible provider.");

        if (State != ConnectionState.Open)
        {
            _pendingExtensions.Add((file, proc));
            return;
        }

        LoadExtensionCore(file, proc);
    }

    public virtual void BackupDatabase(SqliteConnection destination)
        => BackupDatabase(destination, "main", "main");

    public virtual void BackupDatabase(SqliteConnection destination, string destinationName, string sourceName)
    {
        if (State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection("BackupDatabase"));
        ArgumentNullException.ThrowIfNull(destination);
        if (IsManagedProvider != destination.IsManagedProvider)
            throw new NotSupportedException(Properties.Resources.ManagedBackupMixedProvidersNotSupported);
        if (IsManagedProvider)
        {
            if (destination.State != ConnectionState.Open)
                destination.Open();
            SqliteManagedBackup.Copy(this, destination, destinationName, sourceName);
            return;
        }
        if (Transaction is not null)
            throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
        if (destination.State != ConnectionState.Open)
            destination.Open();

        foreach (var createSql in GetSchemaSql())
            destination.ExecuteNonQuery(createSql);

        foreach (var tableName in GetUserTableNames())
            CopyTableRows(destination, tableName);
    }

    public new virtual SqliteCommand CreateCommand() => new(this) { Transaction = Transaction };

    protected override DbCommand CreateDbCommand() => CreateCommand();

    protected override DbBatch CreateDbBatch() => new SqliteBatch(this);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => BeginTransaction(isolationLevel);

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection(nameof(BeginTransaction)));
        if (Transaction is not null)
            throw new InvalidOperationException(Properties.Resources.ParallelTransactionsNotSupported);

        return Transaction = await SqliteTransaction
            .CreateAsync(
                this,
                isolationLevel,
                deferred: isolationLevel == IsolationLevel.ReadUncommitted,
                cancellationToken)
            .ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            Close();
            _noOpenReaders.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    internal AhtolaNativeDatabase NativeDatabase => _database ?? throw new InvalidOperationException("The connection is not open.");

    internal IManagedConnectionAdapter ManagedConnection
        => _managedDatabase?.Connection ?? throw new InvalidOperationException("The connection is not open.");

    internal bool IsManagedConnection => _managedDatabase is not null;

    internal bool UsesManagedDatabase => IsManagedConnection;

    internal bool IsManagedSharedMemory => _managedSharedMemory;

    internal bool HasOpenReader
    {
        get
        {
            lock (_readerGate)
                return _openReaders.Any(static reader => reader is SqliteDataReader);
        }
    }

    internal bool IsReadOnly => _readOnly;

    internal bool IsManagedReadOnly => _readOnly && UsesManagedDatabase;

    internal bool RecursiveTriggers => _recursiveTriggers;

    void ILocalReaderConnection.ReaderOpened(IConnectionOwnedReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_readerGate)
        {
            if (!_openReaders.Add(reader))
                throw new InvalidOperationException("The data reader is already registered with this connection.");
            if (reader is SqliteDataReader)
                _noOpenReaders.Reset();
        }
    }

    void ILocalReaderConnection.ReaderClosed(IConnectionOwnedReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_readerGate)
        {
            if (_openReaders.Remove(reader)
                && reader is SqliteDataReader
                && !_openReaders.Any(static openReader => openReader is SqliteDataReader))
            {
                _noOpenReaders.Set();
            }
        }
    }

    internal bool WaitForNoOpenReader(TimeSpan timeout, CancellationToken cancellationToken)
        => _noOpenReaders.Wait(timeout, cancellationToken);

    internal void ManagedBlobOpened(SqliteBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (!_openManagedBlobs.Add(blob))
            throw new InvalidOperationException("The managed incremental blob is already registered with this connection.");
    }

    internal void ManagedBlobClosed(SqliteBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        _openManagedBlobs.Remove(blob);
    }

    internal void CommandOpened(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _openCommands.Add(command);
    }

    internal void CommandClosed(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _openCommands.Remove(command);
    }

    internal void ExecuteNonQuery(string sql)
    {
        using var command = new SqliteCommand(sql, this);
        command.ExecuteNonQuery();
    }

    private List<string> GetSchemaSql()
    {
        var schema = new List<string>();
        using var command = new SqliteCommand("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 WHEN 'view' THEN 2 ELSE 3 END;", this);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            schema.Add(reader.GetString(0));

        return schema;
    }

    private List<string> GetUserTableNames()
    {
        var tables = new List<string>();
        using var command = new SqliteCommand("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;", this);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        return tables;
    }

    private void CopyTableRows(SqliteConnection destination, string tableName)
    {
        using var select = new SqliteCommand("SELECT * FROM " + QuoteIdentifier(tableName) + ";", this);
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            using var insert = destination.CreateCommand();
            var parameterNames = Enumerable.Range(0, reader.FieldCount).Select(i => "$p" + i).ToArray();
            insert.CommandText = "INSERT INTO " + QuoteIdentifier(tableName) + " VALUES (" + string.Join(", ", parameterNames) + ");";
            for (var i = 0; i < reader.FieldCount; i++)
                insert.Parameters.AddWithValue(parameterNames[i], reader.GetValue(i));

            insert.ExecuteNonQuery();
        }
    }

    internal void EnsureOpen()
    {
        if (State != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");
    }

    private void ApplyConnectionOptions()
    {
        // The e_sqlite3 build Microsoft.Data.Sqlite ships is compiled with
        // SQLITE_DEFAULT_FOREIGN_KEYS=1, so managed connections default to enforcing
        // foreign keys unless the connection string says otherwise. The managed engine
        // itself keeps the SQLite CLI default (OFF).
        if (_connectionOptions.ForeignKeys.HasValue)
            ExecuteNonQuery("PRAGMA foreign_keys = " + (_connectionOptions.ForeignKeys.Value ? "1" : "0") + ";");
        else if (IsManagedConnection)
            ExecuteNonQuery("PRAGMA foreign_keys = 1;");
        if (_connectionOptions.RecursiveTriggers)
            _recursiveTriggers = true;
    }

    private void EnableManagedReadOnly()
    {
        using var statement = ManagedConnection.Prepare("PRAGMA query_only = ON;");
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private void ApplyExtensionSettings()
    {
        if (_extensionsEnabled)
        {
            if (UsesManagedDatabase)
                throw new NotSupportedException(Properties.Resources.ManagedExtensionsNotSupported);

            SqliteNativeProvider.Current.EnableExtensions(NativeDatabase, enable: true);
        }
    }

    private void LoadPendingExtensions()
    {
        foreach (var (file, proc) in _pendingExtensions)
            LoadExtensionCore(file, proc);
        _pendingExtensions.Clear();
    }

    private void LoadExtensionCore(string file, string? proc)
    {
        if (UsesManagedDatabase)
            throw new NotSupportedException(Properties.Resources.ManagedExtensionsNotSupported);

        try
        {
            SqliteNativeProvider.Current.LoadExtension(NativeDatabase, file);
        }
        catch (AhtolaException ex)
        {
            throw SqliteCommand.ToSqliteException(ex);
        }
    }

    private void CleanupFailedOpen(string? sharedMemoryPath)
    {
        DisposeDatabaseAndManagedEncryptionFileSystem();
        _dataSource = null;
        _readOnly = false;
        _managedSharedMemory = false;
        _sharedMemoryPath = null;
        if (sharedMemoryPath is not null)
            ReleaseSharedMemoryFile(sharedMemoryPath);
    }

    private static IManagedDatabaseAdapter OpenManagedDatabase(
        string filename,
        bool readOnly,
        AhtolaEncryptionOptions? encryption,
        out AhtolaEncryptionFileSystem? managedEncryptionFileSystem,
        bool foreignReadOnly = false)
    {
        managedEncryptionFileSystem = null;
        IManagedDatabaseAdapter? database = null;

        try
        {
            if (encryption is null && !readOnly)
            {
                database = ManagedDatabaseAdapter.Open(filename);
                _ = database.Connect();
                return database;
            }

            IFileSystem fileSystem = PhysicalFileSystem.Instance;
            if (encryption is not null)
            {
                managedEncryptionFileSystem = new AhtolaEncryptionFileSystem(
                    PhysicalFileSystem.Instance,
                    encryption);
                fileSystem = managedEncryptionFileSystem;
            }

            database = ManagedDatabaseAdapter.OpenFile(
                filename,
                fileSystem,
                readOnly: readOnly,
                foreignReadOnly: foreignReadOnly);
            _ = database.Connect();
            return database;
        }
        catch
        {
            database?.Dispose();
            managedEncryptionFileSystem?.Dispose();
            managedEncryptionFileSystem = null;
            throw;
        }
    }

    private void DisposeDatabaseAndManagedEncryptionFileSystem(bool pooledReusable = false)
    {
        var database = _database;
        var managedDatabase = _managedDatabase;
        var managedPoolLease = _managedPoolLease;
        var managedEncryptionFileSystem = _managedEncryptionFileSystem;
        _database = null;
        _managedDatabase = null;
        _managedPoolLease = null;
        _managedEncryptionFileSystem = null;
        try
        {
            database?.Dispose();
        }
        finally
        {
            try
            {
                if (managedPoolLease is not null)
                    managedPoolLease.Release(pooledReusable);
                else
                    managedDatabase?.Dispose();
            }
            finally
            {
                managedEncryptionFileSystem?.Dispose();
            }
        }
    }

    private void CloseOpenReaders()
    {
        IConnectionOwnedReader[] readers;
        lock (_readerGate)
            readers = _openReaders.ToArray();
        foreach (var reader in readers)
            reader.CloseFromConnection();
    }

    private void CloseOpenManagedBlobs()
    {
        foreach (var blob in _openManagedBlobs.ToArray())
            blob.CloseFromConnection();
    }

    private void ResetOpenCommands()
    {
        foreach (var command in _openCommands.ToArray())
            command.ResetFromConnection();
    }

    private static string NormalizeDataSource(
        SqliteConnectionStringBuilder options,
        bool validateFileExists = true)
    {
        var dataSource = options.DataSource;
        if (string.IsNullOrEmpty(dataSource))
            return ":memory:";
        if (options.EffectiveLocalProvider == AhtolaLocalProvider.Managed && options.Vfs is { Length: > 0 })
            throw new NotSupportedException(Properties.Resources.ManagedVfsNotSupported);
        if (options.Vfs is { Length: > 0 } vfs && !IsSupportedVfs(vfs))
            throw new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_ERROR, "no such vfs: " + vfs), SQLITE_ERROR);
        if (dataSource == ":memory:")
            return dataSource;
        if (options.Mode == SqliteOpenMode.Memory)
            return options.Cache == SqliteCacheMode.Shared && dataSource.Length > 0
                ? options.EffectiveLocalProvider == AhtolaLocalProvider.Managed
                    ? dataSource
                    : GetSharedMemoryFile(dataSource)
                : ":memory:";
        if (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return NormalizeUriDataSource(dataSource, validateFileExists);

        const string dataDirectory = "|DataDirectory|";
        if (dataSource.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var baseDirectory = AppDomain.CurrentDomain.GetData("DataDirectory") as string
                                ?? AppContext.BaseDirectory;
            dataSource = Path.Combine(baseDirectory, dataSource[dataDirectory.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var filename = Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.Combine(AppContext.BaseDirectory, dataSource);

        if (validateFileExists
            && (options.Mode == SqliteOpenMode.ReadOnly || options.Mode == SqliteOpenMode.ReadWrite)
            && !File.Exists(filename))
            throw new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_CANTOPEN, "unable to open database file"), SQLITE_CANTOPEN);

        return filename;
    }

    private static string NormalizeUriDataSource(string dataSource, bool validateFileExists)
    {
        var queryStart = dataSource.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? dataSource[5..] : dataSource[5..queryStart];
        var query = queryStart < 0 ? string.Empty : dataSource[(queryStart + 1)..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (!pieces[0].Equals("mode", StringComparison.OrdinalIgnoreCase))
                continue;

            var mode = pieces.Length == 2 ? pieces[1] : string.Empty;
            if (!mode.Equals("ro", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("rw", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("rwc", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("memory", StringComparison.OrdinalIgnoreCase))
                throw new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_ERROR, "no such access mode: " + mode), SQLITE_ERROR);
            if (mode.Equals("memory", StringComparison.OrdinalIgnoreCase))
                return ":memory:";
            if (validateFileExists
                && (mode.Equals("ro", StringComparison.OrdinalIgnoreCase) || mode.Equals("rw", StringComparison.OrdinalIgnoreCase))
                && !File.Exists(path))
                throw new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_CANTOPEN, "unable to open database file"), SQLITE_CANTOPEN);
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private static bool IsSupportedVfs(string vfs)
        => vfs.Equals("win32-longpath", StringComparison.OrdinalIgnoreCase)
           || vfs.Equals("unix-dotfile", StringComparison.OrdinalIgnoreCase);

    private static string GetSharedMemoryFile(string dataSource)
    {
        var sanitized = string.Join("_", dataSource.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (sanitized.Length == 0)
            sanitized = Math.Abs(dataSource.GetHashCode(StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture);

        return Path.Combine(Path.GetTempPath(), "Ahtola-dotnet-shared-" + sanitized + ".db");
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool IsManagedSharedMemoryConfiguration(SqliteConnectionStringBuilder options)
        => options.EffectiveLocalProvider == AhtolaLocalProvider.Managed
           && options.Mode == SqliteOpenMode.Memory
           && options.Cache == SqliteCacheMode.Shared
           && !string.IsNullOrWhiteSpace(options.DataSource);

    private static bool IsNativeSharedMemory(SqliteConnectionStringBuilder options)
        => options.EffectiveLocalProvider != AhtolaLocalProvider.Managed
           && options.Mode == SqliteOpenMode.Memory
           && options.Cache == SqliteCacheMode.Shared
           && options.DataSource.Length > 0;

    private void ValidateManagedSharedCacheOptions()
    {
        if (_connectionOptions.EffectiveLocalProvider != AhtolaLocalProvider.Managed
            || _connectionOptions.Cache != SqliteCacheMode.Shared)
        {
            return;
        }

        if (!IsManagedSharedMemoryConfiguration(_connectionOptions))
        {
            // Anonymous in-memory databases stay connection-private, and file-backed
            // Cache=Shared opens as an ordinary private file connection: the managed
            // engine cannot emulate SQLite shared-cache semantics for file databases,
            // so it deliberately provides stronger isolation instead of rejecting the
            // keyword outright (matches AhtolaConnectionOptions.GetManagedLocalOpenOptions).
            return;
        }

        if (HasManagedCallbacks)
            throw new NotSupportedException(Properties.Resources.ManagedSharedCacheCallbacksNotSupported);
    }

    private void ValidateForeignReadOnlyOptions()
    {
        if (!_connectionOptions.ForeignReadOnly)
            return;

        if (_connectionOptions.EffectiveLocalProvider != AhtolaLocalProvider.Managed
            || _connectionOptions.Mode != SqliteOpenMode.ReadOnly
            || _connectionOptions.Pooling
            || _connectionOptions.Cache == SqliteCacheMode.Shared
            || _connectionOptions.HasEncryptionOptions
            || !string.IsNullOrEmpty(_connectionOptions.Password)
            || string.IsNullOrWhiteSpace(_connectionOptions.DataSource)
            || _connectionOptions.DataSource.Equals(":memory:", StringComparison.Ordinal))
        {
            throw new NotSupportedException(Properties.Resources.ManagedForeignReadOnlyNotSupported);
        }
    }

    private bool CanUseManagedPooling(string filename, AhtolaEncryptionOptions? encryption)
        => _connectionOptions.Pooling
           && encryption is null
           && !HasManagedCallbacks
           && !_connectionOptions.ForeignReadOnly
           && _connectionOptions.Mode != SqliteOpenMode.Memory
           && !filename.Equals(":memory:", StringComparison.Ordinal);

    private bool TryGetManagedPoolKey(out ManagedConnectionPoolKey key)
    {
        key = default;
        if (_connectionOptions.EffectiveLocalProvider != AhtolaLocalProvider.Managed
            || !_connectionOptions.Pooling
            || _connectionOptions.HasEncryptionOptions
            || _connectionOptions.Mode == SqliteOpenMode.Memory
            || _connectionOptions.Cache == SqliteCacheMode.Shared)
        {
            return false;
        }

        var filename = NormalizeDataSource(_connectionOptions, validateFileExists: false);
        if (filename.Equals(":memory:", StringComparison.Ordinal))
            return false;

        key = ManagedConnectionPoolKey.Create(
            filename,
            _connectionOptions.Mode == SqliteOpenMode.ReadOnly);
        return true;
    }

    private bool HasManagedCallbacks
        => _scalarFunctions.Count != 0
           || _aggregateFunctions.Count != 0
           || _collations.Count != 0
           || HasHooks;

    private static string RegisterSharedMemoryFile(string path)
    {
        lock (SharedMemoryLock)
        {
            if (!SharedMemoryReferences.TryGetValue(path, out var references))
            {
                if (File.Exists(path))
                    File.Delete(path);
                SharedMemoryReferences[path] = 1;
            }
            else
            {
                SharedMemoryReferences[path] = references + 1;
            }

            return path;
        }
    }

    private static void ReleaseSharedMemoryFile(string path)
    {
        lock (SharedMemoryLock)
        {
            if (!SharedMemoryReferences.TryGetValue(path, out var references))
                return;

            if (references > 1)
            {
                SharedMemoryReferences[path] = references - 1;
                return;
            }

            SharedMemoryReferences.Remove(path);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static SqliteException ToSqliteException(AhtolaException exception)
    {
        var message = exception.Message;
        return new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_ERROR, message), SQLITE_ERROR);
    }

    private bool IsManagedProvider => _connectionOptions.EffectiveLocalProvider == AhtolaLocalProvider.Managed;

}
