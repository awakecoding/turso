using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Turso.Core.Compilation;
using Turso.Core.Execution;
using Turso.Core.Parsing;
using Turso.Core.Storage;

namespace Turso.Core;

public enum StatementStepResult
{
    Row,
    Done,
}

internal readonly record struct TransactionSnapshot(
    EmbeddedDatabase.SchemaCatalog Catalog,
    long Version,
    PragmaHeaderMetadata PragmaHeader);

public class EmbeddedSqlException : Exception
{
    public EmbeddedSqlException(string message) : base(message)
    {
    }

    internal EmbeddedSqlException(string message, InsertConflictAlgorithm conflictAlgorithm) : base(message)
    {
        ConflictAlgorithm = conflictAlgorithm;
    }

    internal EmbeddedSqlException(
        string message,
        InsertConflictAlgorithm? conflictAlgorithm,
        bool constraintViolation = true) : base(message)
    {
        ConflictAlgorithm = conflictAlgorithm ?? InsertConflictAlgorithm.Abort;
    }

    public EmbeddedSqlException(string message, Exception innerException) : base(message, innerException)
    {
    }

    internal InsertConflictAlgorithm? ConflictAlgorithm { get; }
}

internal readonly record struct PragmaHeaderMetadata(
    int SchemaVersion,
    int UserVersion,
    int ApplicationId);

internal enum ManagedSchemaObjectKind
{
    Table,
    View,
    Trigger,
    Index,
}

internal readonly record struct FileCatalogVersion(
    uint ChangeCounter,
    uint SchemaCookie,
    uint DatabaseSizeInPages,
    int UserVersion,
    int ApplicationId,
    int PageSize)
{
    public static FileCatalogVersion FromHeader(SqliteDatabaseHeader header)
        => new(
            header.ChangeCounter,
            header.SchemaCookie,
            header.DatabaseSizeInPages,
            header.UserVersion,
            header.ApplicationId,
            header.PageSize);
}

internal sealed class EmbeddedConflictRollbackException : EmbeddedSqlException
{
    public EmbeddedConflictRollbackException(EmbeddedSqlException conflict)
        : base(conflict.Message, conflict)
    {
    }
}

internal sealed class EmbeddedConflictFailException : EmbeddedSqlException
{
    public EmbeddedConflictFailException(EmbeddedSqlException conflict, long lastInsertRowId)
        : base(conflict.Message, conflict)
    {
        LastInsertRowId = lastInsertRowId;
    }

    public long LastInsertRowId { get; }
}

/// <summary>
/// Reports that a catalog mutation reached its durable WAL commit marker, but
/// subsequent checkpoint maintenance failed. Retrying the mutation would apply
/// it twice; dispose and reopen before attempting another write.
/// </summary>
public sealed class EmbeddedPostCommitMaintenanceException : EmbeddedSqlException
{
    public EmbeddedPostCommitMaintenanceException(Exception maintenanceFailure)
        : base(
            "The managed database mutation committed successfully, but post-commit checkpoint maintenance failed. "
            + "Do not retry the mutation; dispose and reopen the database before another write.",
            maintenanceFailure)
    {
        MaintenanceFailure = maintenanceFailure;
    }

    /// <summary>The maintenance failure that occurred after the durable commit.</summary>
    public Exception MaintenanceFailure { get; }
}

public sealed class EmbeddedDatabase : IDisposable
{
    private const int MaximumTriggerDepth = 1_000;
    private static readonly ConditionalWeakTable<IFileSystem, FileCatalogWriteLockScope> FileCatalogWriteLocks = new();
    private readonly object _gate = new();
    private Dictionary<string, EmbeddedTable> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ViewDefinition> _views = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TriggerDefinition> _triggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, int Arity), Func<IReadOnlyList<SqlValue>, SqlValue>> _scalarFunctions = new();
    private readonly Dictionary<(string Name, int Arity), ManagedAggregateFunction> _aggregateFunctions = new();
    private readonly Dictionary<string, Func<string, string, int>> _collations = new(StringComparer.OrdinalIgnoreCase);
    private EmbeddedFileStore? _fileStore;
    private readonly string _databasePath = string.Empty;
    private readonly IFileSystem? _fileSystem;
    private readonly object? _fileCatalogWriteLock;
    private readonly bool _readOnly;
    private FileCatalogVersion _fileCatalogVersion;
    private PragmaHeaderMetadata _inMemoryPragmaHeader;
    private long _version;
    private int _activeTransactions;
    private readonly Dictionary<BlobMutationIdentity, int> _activeBlobMutations = new();
    private readonly Dictionary<BlobMutationIdentity, long> _blobMutationGenerations = new();
    private long _nextBlobMutationGeneration;

    public EmbeddedDatabase()
    {
    }

    private EmbeddedDatabase(
        EmbeddedFileStore fileStore,
        EmbeddedFileCatalog catalog,
        string databasePath,
        IFileSystem fileSystem,
        FileCatalogVersion fileCatalogVersion,
        object fileCatalogWriteLock,
        bool readOnly)
    {
        _fileStore = fileStore;
        _databasePath = databasePath;
        _fileSystem = fileSystem;
        _fileCatalogVersion = fileCatalogVersion;
        _fileCatalogWriteLock = fileCatalogWriteLock;
        _readOnly = readOnly;
        _tables = catalog.Tables;
        _views = catalog.Views;
        _triggers = catalog.Triggers;
    }

    /// <summary>
    /// Opens (or creates) a file-backed managed database that persists its schema
    /// and table data as real SQLite pages. See <c>TursoBindings.OpenManagedDatabase</c>
    /// for the exact set of supported schema and data and the documented gaps.
    /// </summary>
    public static EmbeddedDatabase OpenFile(
        string path,
        IFileSystem? fileSystem = null,
        bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var effectiveFileSystem = fileSystem ?? PhysicalFileSystem.Instance;
        var fileCatalogWriteLock = GetFileCatalogWriteLock(effectiveFileSystem, path);
        lock (fileCatalogWriteLock)
        {
            using var catalogWriteLease = readOnly
                ? null
                : EnterPhysicalFileCatalogWriteLock(effectiveFileSystem, path);
            var store = EmbeddedFileStore.Open(
                path,
                effectiveFileSystem,
                out var catalog,
                readOnly: readOnly);
            try
            {
                // The store now owns the physical database before this second pager
                // reads the durable version, so no foreign client can race catalog load.
                var catalogVersion = ReadFileCatalogVersion(effectiveFileSystem, path);
                return new EmbeddedDatabase(
                    store,
                    catalog,
                    path,
                    effectiveFileSystem,
                    catalogVersion,
                    fileCatalogWriteLock,
                    readOnly);
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }
    }

    /// <summary>Releases the backing file store, if any.</summary>
    public void Dispose()
    {
        lock (_gate)
            _fileStore?.Dispose();
    }

    internal bool IsFileBacked => _fileStore is not null;

    internal bool IsReadOnly => _readOnly;

    internal string DatabasePath => _databasePath;

    internal IFileSystem FileSystem
        => _fileSystem ?? throw new InvalidOperationException("The managed database is not file-backed.");

    internal static StringComparer PhysicalPathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    internal bool ReferencesSameDatabase(EmbeddedDatabase other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
            return true;
        if (!IsFileBacked || !other.IsFileBacked || _fileSystem is null || other._fileSystem is null)
            return false;

        var fileSystem = TursoEncryptionFileSystem.Unwrap(_fileSystem);
        var otherFileSystem = TursoEncryptionFileSystem.Unwrap(other._fileSystem);
        if (fileSystem is PhysicalFileSystem && otherFileSystem is PhysicalFileSystem)
        {
            return PhysicalPathComparer.Equals(
                Path.GetFullPath(_databasePath),
                Path.GetFullPath(other._databasePath));
        }

        return ReferenceEquals(fileSystem, otherFileSystem)
               && string.Equals(_databasePath, other._databasePath, StringComparison.Ordinal);
    }

    private sealed record ManagedAggregateFunction(
        SqlValue Seed,
        Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> Step,
        Func<SqlValue, SqlValue> Finalize);

    private sealed record GroupedResult(SourceRow Representative, IReadOnlyList<SourceRow> Rows, SqlValue[] Values);

    private sealed record LimitedDmlCandidate(int Position, SqlValue[] OrderValues);

    internal sealed record QueryContext(
        Dictionary<string, EmbeddedTable> Tables,
        IReadOnlyDictionary<string, SourceData> CommonTableExpressions,
        IReadOnlyDictionary<string, ViewDefinition>? Views = null,
        IReadOnlyDictionary<string, TriggerDefinition>? Triggers = null,
        IReadOnlyList<string>? ExpandingViews = null,
        bool InsideTrigger = false,
        long LastInsertRowId = 0,
        bool ForeignKeysEnabled = false,
        bool RecursiveTriggersEnabled = false,
        IReadOnlySet<string>? ActiveTriggers = null,
        int TriggerDepth = 0,
        CancellationToken CancellationToken = default);

    // Bundles the mutable schema (tables, views, triggers) so a transaction can
    // snapshot and atomically publish all managed catalog state together.
    internal sealed class SchemaCatalog
    {
        public SchemaCatalog(
            Dictionary<string, EmbeddedTable> tables,
            Dictionary<string, ViewDefinition> views,
            Dictionary<string, TriggerDefinition> triggers)
        {
            Tables = tables;
            Views = views;
            Triggers = triggers;
        }

        public Dictionary<string, EmbeddedTable> Tables { get; }

        public Dictionary<string, ViewDefinition> Views { get; }

        public Dictionary<string, TriggerDefinition> Triggers { get; }

        public SchemaCatalog Clone() => new(
            CloneTables(Tables),
            new Dictionary<string, ViewDefinition>(Views, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, TriggerDefinition>(Triggers, StringComparer.OrdinalIgnoreCase));
    }

    public EmbeddedConnection Connect() => new(this);

    internal void CopyFunctionAndCollationRegistriesTo(EmbeddedDatabase target)
    {
        ArgumentNullException.ThrowIfNull(target);

        KeyValuePair<(string Name, int Arity), Func<IReadOnlyList<SqlValue>, SqlValue>>[] scalarFunctions;
        KeyValuePair<(string Name, int Arity), ManagedAggregateFunction>[] aggregateFunctions;
        KeyValuePair<string, Func<string, string, int>>[] collations;
        lock (_gate)
        {
            scalarFunctions = _scalarFunctions.ToArray();
            aggregateFunctions = _aggregateFunctions.ToArray();
            collations = _collations.ToArray();
        }

        foreach (var ((name, arity), function) in scalarFunctions)
            target.RegisterScalarFunction(name, arity, function);
        foreach (var ((name, arity), function) in aggregateFunctions)
            target.RegisterAggregateFunction(name, arity, function.Seed, function.Step, function.Finalize);
        foreach (var (name, compare) in collations)
            target.RegisterCollation(name, compare);
    }

    public void RegisterScalarFunction(string name, int arity, Func<IReadOnlyList<SqlValue>, SqlValue> function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (arity < -1)
            throw new ArgumentOutOfRangeException(nameof(arity));
        ArgumentNullException.ThrowIfNull(function);

        lock (_gate)
        {
            _scalarFunctions[(name.ToUpperInvariant(), arity)] = function;
        }
    }

    public bool UnregisterScalarFunction(string name, int arity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (arity < -1)
            throw new ArgumentOutOfRangeException(nameof(arity));

        lock (_gate)
        {
            return _scalarFunctions.Remove((name.ToUpperInvariant(), arity));
        }
    }

    public int UnregisterScalarFunctions(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            var normalizedName = name.ToUpperInvariant();
            var keys = _scalarFunctions.Keys
                .Where(key => key.Name == normalizedName)
                .ToArray();
            foreach (var key in keys)
                _scalarFunctions.Remove(key);

            return keys.Length;
        }
    }

    public void RegisterAggregateFunction(
        string name,
        int arity,
        SqlValue seed,
        Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
        Func<SqlValue, SqlValue> finalize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (arity < -1)
            throw new ArgumentOutOfRangeException(nameof(arity));
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(finalize);

        lock (_gate)
        {
            _aggregateFunctions[(name.ToUpperInvariant(), arity)] = new ManagedAggregateFunction(seed, step, finalize);
        }
    }

    public int UnregisterAggregateFunctions(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            var normalizedName = name.ToUpperInvariant();
            var keys = _aggregateFunctions.Keys
                .Where(key => key.Name == normalizedName)
                .ToArray();
            foreach (var key in keys)
                _aggregateFunctions.Remove(key);

            return keys.Length;
        }
    }

    public void RegisterCollation(string name, Func<string, string, int> compare)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(compare);

        lock (_gate)
        {
            _collations[name.ToUpperInvariant()] = compare;
        }
    }

    public bool UnregisterCollation(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            return _collations.Remove(name.ToUpperInvariant());
        }
    }

    internal ExecutionResult Execute(
        ParsedStatement statement,
        SqlValue[] parameters,
        long lastInsertRowId = 0,
        bool foreignKeysEnabled = false,
        bool recursiveTriggersEnabled = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (cancellationToken.CanBeCanceled && MayMutate(statement))
            {
                var cancellableWorking = new SchemaCatalog(_tables, _views, _triggers).Clone();
                ExecutionResult cancellableResult;
                try
                {
                    cancellableResult = Execute(
                        statement,
                        parameters,
                        cancellableWorking,
                        lastInsertRowId,
                        foreignKeysEnabled,
                        recursiveTriggersEnabled,
                        cancellationToken);
                }
                catch (EmbeddedConflictFailException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_fileStore is null)
                        PublishCatalog(cancellableWorking);
                    else
                        PersistFileCatalog(cancellableWorking);
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!cancellableResult.Changed)
                    return cancellableResult;

                if (_fileStore is null)
                {
                    if (MayChangeSchema(statement))
                    {
                        _inMemoryPragmaHeader = _inMemoryPragmaHeader with
                        {
                            SchemaVersion = unchecked(_inMemoryPragmaHeader.SchemaVersion + 1),
                        };
                    }
                    PublishCatalog(cancellableWorking);
                }
                else
                {
                    PersistFileCatalog(cancellableWorking);
                }

                return cancellableResult;
            }

            if (_fileStore is null)
            {
                ExecutionResult inMemoryResult;
                try
                {
                    inMemoryResult = Execute(
                        statement,
                        parameters,
                        new SchemaCatalog(_tables, _views, _triggers),
                        lastInsertRowId,
                        foreignKeysEnabled,
                        recursiveTriggersEnabled,
                        cancellationToken);
                }
                catch (EmbeddedConflictFailException)
                {
                    _version++;
                    throw;
                }
                if (inMemoryResult.Changed)
                {
                    if (MayChangeSchema(statement))
                        _inMemoryPragmaHeader = _inMemoryPragmaHeader with
                        {
                            SchemaVersion = unchecked(_inMemoryPragmaHeader.SchemaVersion + 1),
                        };
                    _version++;
                }

                return inMemoryResult;
            }

            // For file-backed databases a mutating autocommit statement runs against
            // a working clone so that a rejected pre-commit persist rolls back
            // cleanly. A reported post-commit maintenance failure instead publishes
            // the catalog because the WAL mutation is already durable.
            if (!MayMutate(statement))
                return Execute(
                    statement,
                    parameters,
                    new SchemaCatalog(_tables, _views, _triggers),
                    lastInsertRowId,
                    foreignKeysEnabled,
                    recursiveTriggersEnabled,
                    cancellationToken);

            var working = new SchemaCatalog(_tables, _views, _triggers).Clone();
            ExecutionResult result;
            try
            {
                result = Execute(
                    statement,
                    parameters,
                    working,
                    lastInsertRowId,
                    foreignKeysEnabled,
                    recursiveTriggersEnabled,
                    cancellationToken);
            }
            catch (EmbeddedConflictFailException)
            {
                PersistFileCatalog(working);
                throw;
            }
            if (result.Changed)
                PersistFileCatalog(working);

            return result;
        }
    }

    internal static bool MayMutate(ParsedStatement statement) => statement is
        CreateTableStatement or DropTableStatement or CreateIndexStatement or DropIndexStatement
        or CreateViewStatement or DropViewStatement or CreateTriggerStatement or DropTriggerStatement
        or AlterTableAddColumnStatement or AlterTableRenameStatement or AlterTableRenameColumnStatement
        or InsertStatement or UpdateStatement or DeleteStatement or WithDmlStatement;

    internal static bool MayChangeSchema(ParsedStatement statement) => statement is
        CreateTableStatement or DropTableStatement or CreateIndexStatement or DropIndexStatement
        or CreateViewStatement or DropViewStatement or CreateTriggerStatement or DropTriggerStatement
        or AlterTableAddColumnStatement or AlterTableRenameStatement or AlterTableRenameColumnStatement;

    internal TransactionSnapshot CreateTransactionSnapshot()
    {
        lock (_gate)
        {
            if (_fileStore is not null)
            {
                if (_fileCatalogWriteLock is null)
                    throw new InvalidOperationException("The managed file catalog persistence state is not initialized.");

                lock (_fileCatalogWriteLock)
                    EnsureFileCatalogVersionCurrent();
            }

            var pragmaHeader = _fileStore is null
                ? _inMemoryPragmaHeader
                : new PragmaHeaderMetadata(
                    unchecked((int)_fileCatalogVersion.SchemaCookie),
                    _fileCatalogVersion.UserVersion,
                    _fileCatalogVersion.ApplicationId);
            var catalog = new SchemaCatalog(_tables, _views, _triggers).Clone();
            _activeTransactions = checked(_activeTransactions + 1);
            return new TransactionSnapshot(
                catalog,
                _version,
                pragmaHeader);
        }
    }

    internal SqlValue EvaluateConstant(Expression expression, SqlValue[] parameters, long lastInsertRowId)
    {
        lock (_gate)
        {
            return Evaluate(
                expression,
                parameters,
                row: null,
                new QueryContext(
                    _tables,
                    new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
                    _views,
                    _triggers,
                    LastInsertRowId: lastInsertRowId));
        }
    }

    internal void EndTransaction()
    {
        lock (_gate)
        {
            if (_activeTransactions == 0)
                throw new InvalidOperationException("Managed transaction count underflow.");
            _activeTransactions--;
        }
    }

    internal string[] DescribeColumns(QueryStatement statement)
    {
        lock (_gate)
        {
            return DescribeQuery(statement, new QueryContext(
                _tables,
                new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
                _views,
                _triggers));
        }
    }

    internal bool ContainsTableOrView(string name)
    {
        lock (_gate)
            return _tables.ContainsKey(name) || _views.ContainsKey(name);
    }

    internal bool ContainsSchemaObject(string name, ManagedSchemaObjectKind kind)
    {
        lock (_gate)
        {
            return kind switch
            {
                ManagedSchemaObjectKind.Table => _tables.ContainsKey(name),
                ManagedSchemaObjectKind.View => _views.ContainsKey(name),
                ManagedSchemaObjectKind.Trigger => _triggers.ContainsKey(name),
                ManagedSchemaObjectKind.Index => _tables.Values.Any(table =>
                    table.Indexes.Any(index =>
                        string.Equals(index.Name, name, StringComparison.OrdinalIgnoreCase))),
                _ => throw new InvalidOperationException($"Unknown managed schema object kind {kind}."),
            };
        }
    }

    // Extracts the target table and RETURNING projections from a DML statement so callers
    // can treat INSERT/UPDATE/DELETE ... RETURNING as row-producing.
    internal static bool TryGetReturning(
        ParsedStatement statement,
        out string tableName,
        out IReadOnlyList<Projection> returning)
    {
        switch (statement)
        {
            case WithDmlStatement with:
                return TryGetReturning(with.Dml, out tableName, out returning);
            case InsertStatement { Returning: { } insertReturning } insert:
                tableName = insert.TableName;
                returning = insertReturning;
                return true;
            case UpdateStatement { Returning: { } updateReturning } update:
                tableName = update.TableName;
                returning = updateReturning;
                return true;
            case DeleteStatement { Returning: { } deleteReturning } delete:
                tableName = delete.TableName;
                returning = deleteReturning;
                return true;
            default:
                tableName = string.Empty;
                returning = null!;
                return false;
        }
    }

    internal string[] DescribeReturning(string tableName, IReadOnlyList<Projection> returning)
    {
        lock (_gate)
        {
            return DescribeReturning(tableName, returning, new SchemaCatalog(_tables, _views, _triggers));
        }
    }

    internal string[] DescribeReturning(
        string tableName,
        IReadOnlyList<Projection> returning,
        SchemaCatalog catalog)
    {
        if (!catalog.Tables.TryGetValue(tableName, out var table))
            throw new EmbeddedSqlException($"no such table: {tableName}");

        var outputColumns = BuildOutputColumns(tableName, table.Columns);
        return GetColumnNames(returning, outputColumns, outputColumns);
    }

    internal void CommitTransaction(
        SchemaCatalog catalog,
        long version,
        PragmaHeaderMetadata? pragmaHeader = null)
    {
        lock (_gate)
        {
            if (_readOnly)
                throw new EmbeddedSqlException("attempt to write a readonly database");

            if (_version != version)
                throw new EmbeddedSqlException("database is locked");

            if (_fileStore is null)
            {
                if (pragmaHeader is { } metadata)
                    _inMemoryPragmaHeader = metadata;
                PublishCatalog(catalog);
                return;
            }

            PersistFileCatalog(catalog, pragmaHeader);
        }
    }

    internal PragmaHeaderMetadata GetPragmaHeaderMetadata()
    {
        lock (_gate)
        {
            return _fileStore is null
                ? _inMemoryPragmaHeader
                : new PragmaHeaderMetadata(
                    unchecked((int)_fileCatalogVersion.SchemaCookie),
                    _fileCatalogVersion.UserVersion,
                    _fileCatalogVersion.ApplicationId);
        }
    }

    internal IDisposable OpenBlobMutationLease(string tableName, long rowId)
    {
        lock (_gate)
        {
            var identity = new BlobMutationIdentity(tableName, rowId);
            _activeBlobMutations.TryGetValue(identity, out var handles);
            _activeBlobMutations[identity] = checked(handles + 1);
            return new BlobMutationLease(this, identity);
        }
    }

    internal long GetBlobMutationGeneration(string tableName, long rowId)
    {
        lock (_gate)
        {
            return _blobMutationGenerations.GetValueOrDefault(new BlobMutationIdentity(tableName, rowId));
        }
    }

    internal bool HasOpenBlobHandles
    {
        get
        {
            lock (_gate)
                return _activeBlobMutations.Count > 0;
        }
    }

    internal bool HasUpdateTrigger(string tableName)
    {
        lock (_gate)
        {
            return _triggers.Values.Any(trigger =>
                trigger.Event == TriggerEvent.Update
                && string.Equals(trigger.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal void RecordBlobMutation(string tableName, long rowId)
    {
        lock (_gate)
        {
            var identity = new BlobMutationIdentity(tableName, rowId);
            if (_activeBlobMutations.ContainsKey(identity))
                _blobMutationGenerations[identity] = checked(++_nextBlobMutationGeneration);
        }
    }

    internal void ReleaseBlobMutationLease(BlobMutationIdentity identity)
    {
        lock (_gate)
        {
            var handles = _activeBlobMutations[identity] - 1;
            if (handles == 0)
            {
                _activeBlobMutations.Remove(identity);
                _blobMutationGenerations.Remove(identity);
                return;
            }

            _activeBlobMutations[identity] = handles;
        }
    }

    internal int GetPageSize()
    {
        lock (_gate)
            return _fileStore is null ? SqlitePageSize.Default : _fileCatalogVersion.PageSize;
    }

    internal SqliteJournalMode GetJournalMode()
    {
        lock (_gate)
            return _fileStore is null ? SqliteJournalMode.Delete : _fileStore.JournalMode;
    }

    internal SqliteJournalMode SwitchJournalMode(SqliteJournalMode journalMode)
    {
        lock (_gate)
        {
            if (_fileStore is null)
                throw new EmbeddedSqlException("In-memory databases support only MEMORY journal mode.");
            if (_readOnly)
                throw new EmbeddedSqlException("attempt to write a readonly database");
            if (_activeTransactions != 0)
                throw new EmbeddedSqlException("cannot change journal mode while a transaction is active");
            if (_activeBlobMutations.Count != 0)
                throw new EmbeddedSqlException("cannot change journal mode while a blob handle is active");
            if (_fileSystem is null || _fileCatalogWriteLock is null)
                throw new InvalidOperationException("The managed file catalog persistence state is not initialized.");

            lock (_fileCatalogWriteLock)
            {
                using var catalogWriteLease = EnterPhysicalFileCatalogWriteLock(_fileSystem, _databasePath);
                EnsureFileCatalogVersionCurrent();
                var result = _fileStore.SwitchJournalMode(journalMode);
                _fileCatalogVersion = ReadFileCatalogVersion(_fileSystem, _databasePath);
                _version++;
                return result;
            }
        }
    }

    internal void MigratePageSize(int pageSize)
    {
        lock (_gate)
        {
            if (_fileStore is null)
                throw new EmbeddedSqlException("In-memory databases have a fixed managed page size.");
            if (_readOnly)
                throw new EmbeddedSqlException("attempt to write a readonly database");
            if (_activeTransactions != 0)
                throw new EmbeddedSqlException("cannot VACUUM while a transaction is active");
            if (_activeBlobMutations.Count != 0)
                throw new EmbeddedSqlException("cannot VACUUM while a blob handle is active");
            if (_fileSystem is null || _fileCatalogWriteLock is null)
                throw new InvalidOperationException("The managed file catalog persistence state is not initialized.");

            lock (_fileCatalogWriteLock)
            {
                using var catalogWriteLease = EnterPhysicalFileCatalogWriteLock(_fileSystem, _databasePath);
                EnsureFileCatalogVersionCurrent();
                if (pageSize == _fileCatalogVersion.PageSize)
                    _fileStore.Compact();
                else
                    _fileStore.MigratePageSize(pageSize, _tables, _views, _triggers);
                _fileCatalogVersion = ReadFileCatalogVersion(_fileSystem, _databasePath);
                _version++;
            }
        }
    }

    internal void SetInMemoryPragmaHeaderMetadata(PragmaHeaderMetadata metadata)
    {
        lock (_gate)
        {
            if (_fileStore is not null)
            {
                if (_readOnly)
                    throw new EmbeddedSqlException("attempt to write a readonly database");
                throw new EmbeddedSqlException(
                    "Managed file-backed databases do not support writes to schema_version, user_version, or application_id.");
            }

            if (_inMemoryPragmaHeader == metadata)
                return;

            _inMemoryPragmaHeader = metadata;
            _version++;
        }
    }

    private void PersistFileCatalog(
        SchemaCatalog catalog,
        PragmaHeaderMetadata? pragmaHeader = null)
    {
        if (_fileStore is null || _fileSystem is null || _fileCatalogWriteLock is null)
            throw new InvalidOperationException("The managed file catalog persistence state is not initialized.");

        lock (_fileCatalogWriteLock)
        {
            using var catalogWriteLease = EnterPhysicalFileCatalogWriteLock(_fileSystem, _databasePath);
            EnsureFileCatalogVersionCurrent();
            try
            {
                var committedVersion = _fileStore.Persist(
                    catalog.Tables,
                    catalog.Views,
                    catalog.Triggers,
                    pragmaHeader);
                PublishCatalog(catalog, committedVersion);
            }
            catch (EmbeddedPostCommitMaintenanceException)
            {
                PublishCatalog(catalog, _fileStore.CommittedCatalogVersion);
                throw;
            }
        }
    }

    private void EnsureFileCatalogVersionCurrent()
    {
        if (_fileSystem is null)
            throw new InvalidOperationException("The managed file catalog persistence state is not initialized.");

        var durableVersion = ReadFileCatalogVersion(_fileSystem, _databasePath);
        if (durableVersion != _fileCatalogVersion)
        {
            throw new EmbeddedSqlException(
                "database is busy: the managed file catalog changed since this connection's snapshot; "
                + "dispose and reopen before retrying the write.");
        }
    }

    internal void RefreshFileCatalogForPooling()
    {
        lock (_gate)
        {
            if (_fileStore is null)
                return;
            if (_fileSystem is null || _fileCatalogWriteLock is null)
                throw new InvalidOperationException("The managed file catalog persistence state is not initialized.");

            lock (_fileCatalogWriteLock)
            {
                using var catalogWriteLease = EnterPhysicalFileCatalogWriteLock(_fileSystem, _databasePath);
                var durableVersion = ReadFileCatalogVersion(_fileSystem, _databasePath);
                if (durableVersion == _fileCatalogVersion)
                    return;

                var replacement = EmbeddedFileStore.Open(
                    _databasePath,
                    _fileSystem,
                    out var catalog,
                    readOnly: _readOnly);
                try
                {
                    var loadedVersion = ReadFileCatalogVersion(_fileSystem, _databasePath);
                    if (loadedVersion != durableVersion)
                    {
                        throw new EmbeddedSqlException(
                            "database is busy: the managed file catalog changed while refreshing a pooled connection.");
                    }

                    var previous = _fileStore;
                    _fileStore = replacement;
                    replacement = null;
                    PublishCatalog(
                        new SchemaCatalog(catalog.Tables, catalog.Views, catalog.Triggers),
                        loadedVersion);
                    previous.Dispose();
                }
                finally
                {
                    replacement?.Dispose();
                }
            }
        }
    }

    private void PublishCatalog(SchemaCatalog catalog, FileCatalogVersion? fileCatalogVersion = null)
    {
        _tables = catalog.Tables;
        _views = catalog.Views;
        _triggers = catalog.Triggers;
        if (fileCatalogVersion is { } version)
            _fileCatalogVersion = version;
        _version++;
    }

    private static FileCatalogVersion ReadFileCatalogVersion(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        if (header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != pager.CommittedPageCount)
        {
            throw new InvalidDataException(
                "The managed file catalog does not have an authoritative committed SQLite header.");
        }

        return new FileCatalogVersion(
            header.ChangeCounter,
            header.SchemaCookie,
            header.DatabaseSizeInPages,
            header.UserVersion,
            header.ApplicationId,
            header.PageSize);
    }

    private static object GetFileCatalogWriteLock(IFileSystem fileSystem, string path)
    {
        var unwrappedFileSystem = TursoEncryptionFileSystem.Unwrap(fileSystem);
        var lockPath = unwrappedFileSystem is PhysicalFileSystem
            ? Path.GetFullPath(path)
            : path;
        return FileCatalogWriteLocks
            .GetValue(unwrappedFileSystem, static _ => new FileCatalogWriteLockScope())
            .Get(lockPath);
    }

    private static IDisposable? EnterPhysicalFileCatalogWriteLock(IFileSystem fileSystem, string path)
    {
        if (TursoEncryptionFileSystem.Unwrap(fileSystem) is not PhysicalFileSystem)
            return null;

        var lockPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            lockPath = lockPath.ToUpperInvariant();
        var name = "Turso.ManagedCatalog."
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockPath)));
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The prior owner terminated before publishing or releasing. The
                // durable catalog version check below decides whether this handle
                // can safely write.
            }

            return new FileCatalogMutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class FileCatalogWriteLockScope
    {
        private readonly Dictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

        public object Get(string path)
        {
            lock (_locks)
            {
                if (!_locks.TryGetValue(path, out var fileCatalogWriteLock))
                {
                    fileCatalogWriteLock = new object();
                    _locks.Add(path, fileCatalogWriteLock);
                }

                return fileCatalogWriteLock;
            }
        }
    }

    private sealed class FileCatalogMutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is null)
                return;

            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    internal ExecutionResult Execute(
        ParsedStatement statement,
        SqlValue[] parameters,
        SchemaCatalog catalog,
        long lastInsertRowId = 0,
        bool foreignKeysEnabled = false,
        bool recursiveTriggersEnabled = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_readOnly && MayMutate(statement))
            throw new EmbeddedSqlException("attempt to write a readonly database");

        var tables = catalog.Tables;
        var context = new QueryContext(
            tables,
            new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
            catalog.Views,
            catalog.Triggers,
            LastInsertRowId: lastInsertRowId,
            ForeignKeysEnabled: foreignKeysEnabled,
            RecursiveTriggersEnabled: recursiveTriggersEnabled,
            CancellationToken: cancellationToken);
        return statement switch
        {
            CreateTableStatement create => ExecuteCreateTable(create, catalog),
            DropTableStatement drop => ExecuteDropTable(drop, catalog),
            CreateIndexStatement createIndex => ExecuteCreateIndex(createIndex, catalog),
            DropIndexStatement dropIndex => ExecuteDropIndex(dropIndex, tables),
            CreateViewStatement createView => ExecuteCreateView(createView, catalog),
            DropViewStatement dropView => ExecuteDropView(dropView, catalog),
            CreateTriggerStatement createTrigger => ExecuteCreateTrigger(createTrigger, catalog),
            DropTriggerStatement dropTrigger => ExecuteDropTrigger(dropTrigger, catalog),
            AlterTableAddColumnStatement addColumn => ExecuteAlterTableAddColumn(addColumn, parameters, context),
            AlterTableRenameStatement rename => ExecuteAlterTableRename(rename, tables),
            AlterTableRenameColumnStatement renameColumn => ExecuteAlterTableRenameColumn(renameColumn, tables),
            InsertStatement insert => ExecuteInsert(insert, parameters, context),
            UpdateStatement update => ExecuteUpdate(update, parameters, context),
            DeleteStatement delete => ExecuteDelete(delete, parameters, context),
            WithDmlStatement with => ExecuteWithDml(with, parameters, context),
            ValuesClause values => ExecuteValuesStatement(values, parameters, context, null),
            QueryStatement query => ExecuteQuery(query, parameters, context, null),
            PragmaTableInfoStatement tableInfo => ExecutePragmaTableInfo(tableInfo, tables),
            PragmaTableXInfoStatement tableXInfo => ExecutePragmaTableXInfo(tableXInfo, tables),
            PragmaIndexListStatement indexList => ExecutePragmaIndexList(indexList, tables),
            PragmaIndexInfoStatement indexInfo => ExecutePragmaIndexInfo(indexInfo, tables),
            PragmaTableListStatement => ExecutePragmaTableList(catalog),
            PragmaDatabaseListStatement => ExecutePragmaDatabaseList(),
            PragmaEncodingStatement => ExecutePragmaEncoding(),
            ExplainStatement explain => ExecuteExplain(explain, parameters, context),
            ExplainQueryPlanStatement explainQueryPlan => ExecuteExplainQueryPlan(explainQueryPlan, parameters, context),
            BeginStatement => ExecutionResult.Empty,
            CommitStatement => ExecutionResult.Empty,
            RollbackStatement => ExecutionResult.Empty,
            SavepointStatement => ExecutionResult.Empty,
            ReleaseSavepointStatement => ExecutionResult.Empty,
            RollbackToSavepointStatement => ExecutionResult.Empty,
            _ => throw new EmbeddedSqlException($"Unsupported statement type {statement.GetType().Name}."),
        };
    }

    private static ExecutionResult ExecutePragmaTableInfo(
        PragmaTableInfoStatement statement,
        Dictionary<string, EmbeddedTable> tables)
    {
        if (!tables.TryGetValue(statement.TableName, out var table))
            return new ExecutionResult(["cid", "name", "type", "notnull", "dflt_value", "pk"], [], 0);

        // PRAGMA table_info excludes generated columns (they appear only in table_xinfo),
        // reports the 1-based position of each primary-key column, and treats a WITHOUT
        // ROWID primary-key column as implicitly NOT NULL, matching SQLite.
        return new ExecutionResult(
            ["cid", "name", "type", "notnull", "dflt_value", "pk"],
            BuildPragmaColumnRows(table, includeGeneratedColumns: false),
            0);
    }

    private static ExecutionResult ExecutePragmaTableXInfo(
        PragmaTableXInfoStatement statement,
        Dictionary<string, EmbeddedTable> tables)
    {
        if (!tables.TryGetValue(statement.TableName, out var table))
            return new ExecutionResult(["cid", "name", "type", "notnull", "dflt_value", "pk", "hidden"], [], 0);

        return new ExecutionResult(
            ["cid", "name", "type", "notnull", "dflt_value", "pk", "hidden"],
            BuildPragmaColumnRows(table, includeGeneratedColumns: true),
            0);
    }

    private static SqlValue[][] BuildPragmaColumnRows(EmbeddedTable table, bool includeGeneratedColumns)
    {
        var rows = new List<SqlValue[]>();
        for (var index = 0; index < table.ColumnDefinitions.Length; index++)
        {
            var column = table.ColumnDefinitions[index];
            if (column.IsGenerated && !includeGeneratedColumns)
                continue;

            var primaryKeyPosition = table.PrimaryKeyPosition(index);
            var notNull = column.NotNull || (table.WithoutRowid && primaryKeyPosition > 0);
            var row = new List<SqlValue>
            {
                SqlValue.Integer(index),
                SqlValue.Text(column.Name),
                SqlValue.Text(column.DeclaredType ?? string.Empty),
                SqlValue.Integer(notNull ? 1 : 0),
                GetPragmaDefaultValue(column),
                SqlValue.Integer(primaryKeyPosition),
            };
            if (includeGeneratedColumns)
            {
                row.Add(SqlValue.Integer(column.IsGenerated ? (column.GeneratedStored ? 3 : 2) : 0));
            }

            rows.Add(row.ToArray());
        }

        return rows.ToArray();
    }

    private static SqlValue GetPragmaDefaultValue(EmbeddedColumn column)
    {
        if (column.DefaultSql is { } sql)
        {
            if (sql.Length >= 2 && sql[0] == '(' && sql[^1] == ')')
                sql = sql[1..^1].Trim();
            return SqlValue.Text(sql);
        }
        if (column.DefaultValue is { } defaultValue)
            return SqlValue.Text(FormatSqlLiteral(defaultValue));

        return SqlValue.Null;
    }

    private static ExecutionResult ExecutePragmaTableList(SchemaCatalog catalog)
    {
        var rows = new List<SqlValue[]>
        {
            new[]
            {
                SqlValue.Text("main"),
                SqlValue.Text("sqlite_schema"),
                SqlValue.Text("table"),
                SqlValue.Integer(5),
                SqlValue.Integer(0),
                SqlValue.Integer(0),
            },
        };

        foreach (var (name, table) in catalog.Tables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(
            [
                SqlValue.Text("main"),
                SqlValue.Text(name),
                SqlValue.Text("table"),
                SqlValue.Integer(table.ColumnDefinitions.Length),
                SqlValue.Integer(table.WithoutRowid ? 1 : 0),
                SqlValue.Integer(0),
            ]);
        }

        foreach (var (name, view) in catalog.Views.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var columns = view.Columns
                ?? DescribeQuery(
                    view.Query,
                    new QueryContext(
                        catalog.Tables,
                        new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
                        catalog.Views,
                        catalog.Triggers));
            rows.Add(
            [
                SqlValue.Text("main"),
                SqlValue.Text(name),
                SqlValue.Text("view"),
                SqlValue.Integer(columns.Count),
                SqlValue.Integer(0),
                SqlValue.Integer(0),
            ]);
        }

        return new ExecutionResult(["schema", "name", "type", "ncol", "wr", "strict"], rows.ToArray(), 0);
    }

    private ExecutionResult ExecutePragmaDatabaseList()
        => new(
            ["seq", "name", "file"],
            [[SqlValue.Integer(0), SqlValue.Text("main"), SqlValue.Text(_databasePath)]],
            0);

    private static ExecutionResult ExecutePragmaEncoding()
        => new(["encoding"], [[SqlValue.Text("UTF-8")]], 0);

    private static ExecutionResult ExecutePragmaIndexList(
        PragmaIndexListStatement statement,
        Dictionary<string, EmbeddedTable> tables)
    {
        string[] columns = ["seq", "name", "unique", "origin", "partial"];
        if (!tables.TryGetValue(statement.TableName, out var table))
            return new ExecutionResult(columns, [], 0);

        var rows = new SqlValue[table.Indexes.Count][];
        for (var seq = 0; seq < table.Indexes.Count; seq++)
        {
            var index = table.Indexes[table.Indexes.Count - 1 - seq];
            rows[seq] =
            [
                SqlValue.Integer(seq),
                SqlValue.Text(index.Name),
                SqlValue.Integer(index.Unique ? 1 : 0),
                SqlValue.Text(index.Origin switch
                {
                    EmbeddedIndexOrigin.Explicit => "c",
                    EmbeddedIndexOrigin.PrimaryKey => "pk",
                    _ => "u",
                }),
                SqlValue.Integer(0),
            ];
        }

        return new ExecutionResult(columns, rows, 0);
    }

    private static ExecutionResult ExecutePragmaIndexInfo(
        PragmaIndexInfoStatement statement,
        Dictionary<string, EmbeddedTable> tables)
    {
        string[] columns = ["seqno", "cid", "name"];
        if (!TryFindIndex(tables, statement.IndexName, out _, out var index))
            return new ExecutionResult(columns, [], 0);

        var rows = new SqlValue[index.Columns.Count][];
        for (var seqno = 0; seqno < index.Columns.Count; seqno++)
        {
            var column = index.Columns[seqno];
            rows[seqno] =
            [
                SqlValue.Integer(seqno),
                SqlValue.Integer(column.ColumnIndex),
                SqlValue.Text(column.Name),
            ];
        }

        return new ExecutionResult(columns, rows, 0);
    }

    private ExecutionResult ExecuteCreateTable(CreateTableStatement statement, SchemaCatalog catalog)
    {
        var tables = catalog.Tables;
        if (tables.ContainsKey(statement.Name))
        {
            if (statement.IfNotExists)
                return ExecutionResult.Empty;

            throw new EmbeddedSqlException($"table {statement.Name} already exists");
        }
        if (catalog.Views.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a view named {statement.Name}");
        if (catalog.Triggers.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a trigger named {statement.Name}");
        if (TryFindIndex(tables, statement.Name, out _, out _))
            throw new EmbeddedSqlException($"there is already an index named {statement.Name}");

        // A WITHOUT ROWID table has no hidden rowid to fall back on, so SQLite requires a
        // PRIMARY KEY; reject the table before it is registered when none is declared.
        if (statement.WithoutRowid
            && statement.PrimaryKeyColumns is null
            && !statement.Columns.Any(column => column.PrimaryKey))
        {
            throw new EmbeddedSqlException($"PRIMARY KEY missing on table {statement.Name}");
        }

        tables.Add(
            statement.Name,
            new EmbeddedTable(
                statement.Name,
                statement.Columns,
                statement.WithoutRowid,
                statement.PrimaryKeyColumns,
                statement.UniqueConstraints,
                statement.CheckConstraints,
                statement.PrimaryKeyConflictAlgorithm,
                statement.PrimaryKeyConstraintName,
                statement.PrimaryKeyDeclarationOrder));
        return new ExecutionResult([], [], 0, true);
    }

    private static ExecutionResult ExecuteDropTable(DropTableStatement statement, SchemaCatalog catalog)
    {
        var tables = catalog.Tables;
        if (catalog.Views.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"use DROP VIEW to delete view {statement.Name}");

        if (tables.Remove(statement.Name))
        {
            RemoveTriggersForTable(catalog, statement.Name);
            return new ExecutionResult([], [], 0, true);
        }

        if (statement.IfExists)
            return ExecutionResult.Empty;

        throw new EmbeddedSqlException($"no such table: {statement.Name}");
    }

    private static void RemoveTriggersForTable(SchemaCatalog catalog, string tableName)
    {
        var orphaned = catalog.Triggers
            .Where(entry => string.Equals(entry.Value.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var trigger in orphaned)
            catalog.Triggers.Remove(trigger);
    }

    private ExecutionResult ExecuteCreateIndex(CreateIndexStatement statement, SchemaCatalog catalog)
    {
        var tables = catalog.Tables;
        if (tables.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a table named {statement.Name}");
        if (catalog.Views.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a view named {statement.Name}");
        if (catalog.Triggers.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a trigger named {statement.Name}");

        if (TryFindIndex(tables, statement.Name, out _, out _))
        {
            if (statement.IfNotExists)
                return ExecutionResult.Empty;

            throw new EmbeddedSqlException($"index {statement.Name} already exists");
        }

        if (IsSchemaTable(statement.TableName))
            throw new EmbeddedSqlException($"table {statement.TableName} may not be indexed");
        if (catalog.Views.ContainsKey(statement.TableName))
            throw new EmbeddedSqlException($"views may not be indexed");
        if (!tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        var columns = new EmbeddedIndexColumn[statement.Columns.Count];
        for (var index = 0; index < statement.Columns.Count; index++)
        {
            var column = statement.Columns[index];
            var columnIndex = Array.FindIndex(
                table.Columns,
                name => string.Equals(name, column.Name, StringComparison.OrdinalIgnoreCase));
            if (columnIndex < 0)
                throw new EmbeddedSqlException($"no such column: {column.Name}");

            columns[index] = new EmbeddedIndexColumn(
                table.Columns[columnIndex],
                columnIndex,
                column.Collation ?? table.ColumnDefinitions[columnIndex].Collation,
                column.Descending);
        }

        var definition = new EmbeddedIndex(statement.Name, statement.Unique, columns);
        if (definition.Unique)
            ValidateUniqueIndex(statement.TableName, definition, table.Rows);

        table.Indexes.Add(definition);
        return new ExecutionResult([], [], 0, true);
    }

    private static ExecutionResult ExecuteDropIndex(DropIndexStatement statement, Dictionary<string, EmbeddedTable> tables)
    {
        if (TryFindIndex(tables, statement.Name, out var table, out var index))
        {
            if (index.Origin != EmbeddedIndexOrigin.Explicit)
            {
                throw new EmbeddedSqlException(
                    $"index associated with UNIQUE or PRIMARY KEY constraint cannot be dropped: {statement.Name}");
            }

            table.Indexes.Remove(index);
            return new ExecutionResult([], [], 0, true);
        }

        if (statement.IfExists)
            return ExecutionResult.Empty;

        throw new EmbeddedSqlException($"no such index: {statement.Name}");
    }

    private static bool TryFindIndex(
        Dictionary<string, EmbeddedTable> tables,
        string indexName,
        out EmbeddedTable table,
        out EmbeddedIndex index)
    {
        foreach (var entry in tables)
        {
            foreach (var candidate in entry.Value.Indexes)
            {
                if (string.Equals(candidate.Name, indexName, StringComparison.OrdinalIgnoreCase))
                {
                    table = entry.Value;
                    index = candidate;
                    return true;
                }
            }
        }

        table = null!;
        index = null!;
        return false;
    }

    private static ExecutionResult ExecuteCreateView(CreateViewStatement statement, SchemaCatalog catalog)
    {
        var tables = catalog.Tables;
        if (catalog.Views.ContainsKey(statement.Name))
        {
            if (statement.IfNotExists)
                return ExecutionResult.Empty;

            throw new EmbeddedSqlException($"view {statement.Name} already exists");
        }
        if (tables.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a table named {statement.Name}");
        if (catalog.Triggers.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a trigger named {statement.Name}");
        if (TryFindIndex(tables, statement.Name, out _, out _))
            throw new EmbeddedSqlException($"there is already an index named {statement.Name}");

        var view = new ViewDefinition(statement.Name, statement.Columns, statement.Query, statement.Sql);

        // SQLite validates the view body when it is created: base tables must exist and,
        // when an explicit column list is supplied, its arity must match the query output.
        // Register the view before validating so a self-referential body is reported as a
        // circular definition; roll the registration back if validation fails.
        var context = new QueryContext(
            tables,
            new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
            catalog.Views,
            catalog.Triggers);
        catalog.Views.Add(statement.Name, view);
        try
        {
            _ = ResolveViewColumns(view, EnterView(context, view.Name));
        }
        catch
        {
            catalog.Views.Remove(statement.Name);
            throw;
        }

        return new ExecutionResult([], [], 0, true);
    }

    private static ExecutionResult ExecuteDropView(DropViewStatement statement, SchemaCatalog catalog)
    {
        if (catalog.Views.Remove(statement.Name))
            return new ExecutionResult([], [], 0, true);
        if (catalog.Tables.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"use DROP TABLE to delete table {statement.Name}");
        if (statement.IfExists)
            return ExecutionResult.Empty;

        throw new EmbeddedSqlException($"no such view: {statement.Name}");
    }

    private static ExecutionResult ExecuteCreateTrigger(CreateTriggerStatement statement, SchemaCatalog catalog)
    {
        var tables = catalog.Tables;
        if (catalog.Triggers.ContainsKey(statement.Name))
        {
            if (statement.IfNotExists)
                return ExecutionResult.Empty;

            throw new EmbeddedSqlException($"trigger {statement.Name} already exists");
        }
        if (tables.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a table named {statement.Name}");
        if (catalog.Views.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a view named {statement.Name}");
        if (TryFindIndex(tables, statement.Name, out _, out _))
            throw new EmbeddedSqlException($"there is already an index named {statement.Name}");

        if (catalog.Views.ContainsKey(statement.TableName))
            throw new EmbeddedSqlException($"cannot create trigger on view: {statement.TableName}");
        if (!tables.ContainsKey(statement.TableName))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        catalog.Triggers.Add(
            statement.Name,
            new TriggerDefinition(statement.Name, statement.Event, statement.TableName, statement.Body, statement.Sql));
        return new ExecutionResult([], [], 0, true);
    }

    private static ExecutionResult ExecuteDropTrigger(DropTriggerStatement statement, SchemaCatalog catalog)
    {
        if (catalog.Triggers.Remove(statement.Name))
            return new ExecutionResult([], [], 0, true);
        if (statement.IfExists)
            return ExecutionResult.Empty;

        throw new EmbeddedSqlException($"no such trigger: {statement.Name}");
    }

    private ExecutionResult ExecuteAlterTableAddColumn(
        AlterTableAddColumnStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        var candidate = table.Clone();
        candidate.AddColumn(statement.Column);
        for (var position = 0; position < candidate.Rows.Count; position++)
        {
            var rowid = position < candidate.RowIds.Count ? candidate.RowIds[position] : position + 1;
            ValidateCheckConstraints(
                statement.TableName,
                candidate,
                candidate.Rows[position],
                rowid,
                parameters,
                context);
        }
        table.AddColumn(statement.Column);
        return new ExecutionResult([], [], 0, true);
    }

    private static ExecutionResult ExecuteAlterTableRename(
        AlterTableRenameStatement statement,
        Dictionary<string, EmbeddedTable> tables)
    {
        if (tables.ContainsKey(statement.NewName))
            throw new EmbeddedSqlException($"table {statement.NewName} already exists");
        if (TryFindIndex(tables, statement.NewName, out _, out _))
            throw new EmbeddedSqlException($"there is already an index named {statement.NewName}");
        if (!tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");
        if (table.HasQualifiedCheckReferences())
        {
            throw new EmbeddedSqlException(
                "ALTER TABLE RENAME cannot rewrite table-qualified CHECK expressions until "
                + "managed schema token rewriting is implemented.");
        }

        if (!tables.Remove(statement.TableName))
            throw new InvalidOperationException($"Table '{statement.TableName}' disappeared during rename.");
        table.Rename(statement.NewName);
        tables.Add(statement.NewName, table);
        return new ExecutionResult([], [], 0, true);
    }

    private static ExecutionResult ExecuteAlterTableRenameColumn(
        AlterTableRenameColumnStatement statement,
        Dictionary<string, EmbeddedTable> tables)
    {
        if (!tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        table.RenameColumn(statement.ColumnName, statement.NewName);
        return new ExecutionResult([], [], 0, true);
    }

    private ExecutionResult ExecuteInsert(InsertStatement statement, SqlValue[] parameters, QueryContext context)
    {
        if (statement.ConflictAlgorithm is { } algorithm)
            return ExecuteConflictResolvedInsert(statement, algorithm, parameters, context);
        if (statement.Upsert is not null)
            return ExecuteUpsert(statement, parameters, context);
        if (context.Tables.TryGetValue(statement.TableName, out var table)
            && table.HasNonDefaultConflictAlgorithms)
        {
            return ExecuteWithTriggers(
                statement.TableName,
                TriggerEvent.Insert,
                context,
                () => ExecuteConstraintResolvedInsert(statement, table, parameters, context));
        }

        return ExecuteWithTriggers(
            statement.TableName,
            TriggerEvent.Insert,
            context,
            () => PerformInsert(statement, parameters, context));
    }

    private ExecutionResult ExecuteConstraintResolvedInsert(
        InsertStatement statement,
        EmbeddedTable table,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (context.CommonTableExpressions.Count != 0)
        {
            throw new EmbeddedSqlException(
                "Managed constraint-level conflict resolution does not support CTE sources.");
        }

        var sourceRows = statement.Source is null
            ? null
            : ExecuteQuery(statement.Source, parameters, context, outerRow: null).Rows;
        var backup = CloneTables(context.Tables);
        var insertedRows = new List<SqlValue[]>();
        var insertedRowIds = new List<long>();
        try
        {
            if (sourceRows is not null)
            {
                foreach (var values in sourceRows)
                    InsertValues(values);
            }
            else
            {
                foreach (var values in statement.Rows)
                    InsertExpressions(values);
            }

            var lastInsertRowId = table.HasRowid && insertedRowIds.Count > 0
                ? insertedRowIds[^1]
                : (long?)null;
            return BuildConflictInsertResult(
                statement,
                table,
                insertedRows,
                insertedRowIds,
                parameters,
                context,
                lastInsertRowId);
        }
        catch (EmbeddedConflictFailException)
        {
            throw;
        }
        catch (EmbeddedConflictRollbackException)
        {
            RestoreTables(context.Tables, backup);
            throw;
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }

        void InsertExpressions(Expression[] values)
        {
            var plan = PrepareInsert(statement, table);
            BuildAndCommitCandidate(() => BuildInsertRow(
                statement,
                table,
                plan,
                values,
                parameters,
                context,
                allowExistingRowid: true));
        }

        void InsertValues(IReadOnlyList<SqlValue> values)
        {
            var plan = PrepareInsert(statement, table);
            BuildAndCommitCandidate(() => BuildInsertRow(
                statement,
                table,
                plan,
                values,
                parameters,
                context,
                allowExistingRowid: true));
        }

        void BuildAndCommitCandidate(Func<(SqlValue[] Row, long RowId)> build)
        {
            SqlValue[]? row = null;
            var rowId = 0L;
            try
            {
                (row, rowId) = build();
                CommitInserts(context, statement.TableName, table, [row], [rowId]);
                insertedRows.Add(row);
                insertedRowIds.Add(rowId);
            }
            catch (EmbeddedSqlException exception)
            {
                switch (exception.ConflictAlgorithm)
                {
                    case InsertConflictAlgorithm.Ignore:
                        return;
                    case InsertConflictAlgorithm.Fail:
                        if (insertedRows.Count > 0)
                        {
                            throw new EmbeddedConflictFailException(
                                exception,
                                table.HasRowid ? insertedRowIds[^1] : context.LastInsertRowId);
                        }
                        throw;
                    case InsertConflictAlgorithm.Rollback:
                        throw new EmbeddedConflictRollbackException(exception);
                    case InsertConflictAlgorithm.Replace
                        when row is not null
                            && exception.Message.StartsWith("UNIQUE constraint failed:", StringComparison.Ordinal):
                        if (HasForeignKeyParticipation(context, statement.TableName, table))
                        {
                            throw new EmbeddedSqlException(
                                "Managed constraint-level ON CONFLICT REPLACE does not support tables participating in FOREIGN KEY constraints when foreign_keys is enabled.");
                        }

                        CommitReplacement(
                            context,
                            statement.TableName,
                            table,
                            row,
                            rowId,
                            deleteTriggers: [],
                            insertTriggers: []);
                        insertedRows.Add(row);
                        insertedRowIds.Add(rowId);
                        return;
                    default:
                        throw;
                }
            }
        }
    }

    private ExecutionResult ExecuteConflictResolvedInsert(
        InsertStatement statement,
        InsertConflictAlgorithm algorithm,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (statement.Upsert is not null)
        {
            throw new EmbeddedSqlException(
                "Managed INSERT OR conflict resolution cannot be combined with an ON CONFLICT UPSERT clause.");
        }
        if (context.InsideTrigger)
        {
            throw new EmbeddedSqlException(
                "Managed INSERT OR conflict resolution is not supported inside trigger bodies.");
        }
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");
        if (algorithm != InsertConflictAlgorithm.Replace
            && GetMatchingTriggers(context, statement.TableName, TriggerEvent.Insert).Count > 0)
        {
            throw new EmbeddedSqlException(
                "Managed INSERT OR conflict resolution does not support target tables with INSERT triggers.");
        }

        return algorithm switch
        {
            InsertConflictAlgorithm.Abort => ExecuteAbortInsert(statement, parameters, context),
            InsertConflictAlgorithm.Rollback => ExecuteRollbackInsert(statement, parameters, context),
            InsertConflictAlgorithm.Ignore or InsertConflictAlgorithm.Fail => ExecuteRowwiseConflictInsert(
                statement,
                algorithm,
                table,
                parameters,
                context),
            InsertConflictAlgorithm.Replace => ExecuteReplaceInsert(statement, table, parameters, context),
            _ => throw new InvalidOperationException("Unknown INSERT conflict algorithm."),
        };
    }

    private ExecutionResult ExecuteAbortInsert(
        InsertStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        var backup = CloneTables(context.Tables);
        try
        {
            return PerformInsertEvaluated(statement, parameters, context);
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }
    }

    private ExecutionResult ExecuteRollbackInsert(
        InsertStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        var backup = CloneTables(context.Tables);
        try
        {
            return PerformInsertEvaluated(statement, parameters, context);
        }
        catch (EmbeddedSqlException exception)
        {
            RestoreTables(context.Tables, backup);
            if (IsConflictAlgorithmConstraint(exception))
                throw new EmbeddedConflictRollbackException(exception);

            throw;
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }
    }

    private ExecutionResult ExecuteRowwiseConflictInsert(
        InsertStatement statement,
        InsertConflictAlgorithm algorithm,
        EmbeddedTable table,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (context.CommonTableExpressions.Count != 0)
        {
            throw new EmbeddedSqlException(
                $"Managed INSERT OR {algorithm.ToString().ToUpperInvariant()} does not support CTE sources.");
        }
        if (context.ForeignKeysEnabled && table.ForeignKeys.Count > 0)
        {
            throw new EmbeddedSqlException(
                $"Managed INSERT OR {algorithm.ToString().ToUpperInvariant()} does not support tables with FOREIGN KEY constraints when foreign_keys is enabled.");
        }

        var sourceRows = statement.Source is null
            ? null
            : ExecuteQuery(statement.Source, parameters, context, outerRow: null).Rows;
        var backup = CloneTables(context.Tables);
        var insertedRows = new List<SqlValue[]>();
        var insertedRowIds = new List<long>();
        try
        {
            if (sourceRows is not null)
            {
                foreach (var sourceRow in sourceRows)
                    InsertConflictValues(sourceRow);
            }
            else
            {
                foreach (var values in statement.Rows)
                    InsertConflictExpressions(values);
            }

            var lastInsertRowId = table.HasRowid && insertedRowIds.Count > 0
                ? insertedRowIds[^1]
                : (long?)null;
            return BuildConflictInsertResult(
                statement,
                table,
                insertedRows,
                insertedRowIds,
                parameters,
                context,
                lastInsertRowId);
        }
        catch (EmbeddedSqlException exception) when (
            algorithm == InsertConflictAlgorithm.Fail && IsConflictAlgorithmConstraint(exception))
        {
            if (insertedRows.Count > 0)
            {
                throw new EmbeddedConflictFailException(
                    exception,
                    table.HasRowid ? insertedRowIds[^1] : context.LastInsertRowId);
            }

            throw;
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }

        void InsertConflictExpressions(Expression[] values)
        {
            var plan = PrepareInsert(statement, table);
            try
            {
                var (row, rowId) = BuildInsertRow(statement, table, plan, values, parameters, context);
                CommitInserts(context, statement.TableName, table, [row], [rowId]);
                insertedRows.Add(row);
                insertedRowIds.Add(rowId);
            }
            catch (EmbeddedSqlException exception)
                when (algorithm == InsertConflictAlgorithm.Ignore && IsConflictAlgorithmConstraint(exception))
            {
            }
        }

        void InsertConflictValues(IReadOnlyList<SqlValue> values)
        {
            var plan = PrepareInsert(statement, table);
            try
            {
                var (row, rowId) = BuildInsertRow(statement, table, plan, values, parameters, context);
                CommitInserts(context, statement.TableName, table, [row], [rowId]);
                insertedRows.Add(row);
                insertedRowIds.Add(rowId);
            }
            catch (EmbeddedSqlException exception)
                when (algorithm == InsertConflictAlgorithm.Ignore && IsConflictAlgorithmConstraint(exception))
            {
            }
        }
    }

    private ExecutionResult ExecuteReplaceInsert(
        InsertStatement statement,
        EmbeddedTable table,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (context.CommonTableExpressions.Count != 0)
            throw new EmbeddedSqlException("Managed INSERT OR REPLACE does not support CTE sources.");
        if (HasForeignKeyParticipation(context, statement.TableName, table))
        {
            throw new EmbeddedSqlException(
                "Managed INSERT OR REPLACE does not support tables participating in FOREIGN KEY constraints when foreign_keys is enabled.");
        }

        var sourceRows = statement.Source is null
            ? null
            : ExecuteQuery(statement.Source, parameters, context, outerRow: null).Rows;
        var backup = CloneTables(context.Tables);
        var insertedRows = new List<SqlValue[]>();
        var insertedRowIds = new List<long>();
        var deleteTriggers = context.RecursiveTriggersEnabled
            ? GetMatchingTriggers(context, statement.TableName, TriggerEvent.Delete)
            : Array.Empty<TriggerDefinition>();
        var insertTriggers = GetMatchingTriggers(context, statement.TableName, TriggerEvent.Insert);
        try
        {
            if (sourceRows is not null)
            {
                foreach (var sourceRow in sourceRows)
                    ReplaceValues(sourceRow);
            }
            else
            {
                foreach (var values in statement.Rows)
                    ReplaceExpressions(values);
            }

            var lastInsertRowId = table.HasRowid && insertedRowIds.Count > 0
                ? insertedRowIds[^1]
                : (long?)null;
            var result = BuildConflictInsertResult(
                statement,
                table,
                insertedRows,
                insertedRowIds,
                parameters,
                context,
                lastInsertRowId);

            return result;
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }

        void ReplaceExpressions(Expression[] values)
        {
            var plan = PrepareInsert(statement, table);
            var (row, rowId) = BuildInsertRow(
                statement,
                table,
                plan,
                values,
                parameters,
                context,
                allowExistingRowid: true);
            CommitReplacement(
                context,
                statement.TableName,
                table,
                row,
                rowId,
                deleteTriggers,
                insertTriggers);
            insertedRows.Add(row);
            insertedRowIds.Add(rowId);
        }

        void ReplaceValues(IReadOnlyList<SqlValue> values)
        {
            var plan = PrepareInsert(statement, table);
            var (row, rowId) = BuildInsertRow(
                statement,
                table,
                plan,
                values,
                parameters,
                context,
                allowExistingRowid: true);
            CommitReplacement(
                context,
                statement.TableName,
                table,
                row,
                rowId,
                deleteTriggers,
                insertTriggers);
            insertedRows.Add(row);
            insertedRowIds.Add(rowId);
        }
    }

    private ExecutionResult BuildConflictInsertResult(
        InsertStatement statement,
        EmbeddedTable table,
        IReadOnlyList<SqlValue[]> insertedRows,
        IReadOnlyList<long> insertedRowIds,
        SqlValue[] parameters,
        QueryContext context,
        long? lastInsertRowId)
    {
        if (statement.Returning is not null)
        {
            return BuildReturningResult(
                statement.Returning,
                statement.TableName,
                table,
                insertedRows,
                insertedRowIds,
                insertedRows.Count,
                insertedRows.Count > 0,
                parameters,
                context,
                lastInsertRowId);
        }

        return new ExecutionResult([], [], insertedRows.Count, insertedRows.Count > 0)
        {
            LastInsertRowId = lastInsertRowId,
        };
    }

    private void CommitReplacement(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        SqlValue[] candidate,
        long candidateRowId,
        IReadOnlyList<TriggerDefinition> deleteTriggers,
        IReadOnlyList<TriggerDefinition> insertTriggers)
    {
        var conflicts = FindReplacementConflicts(table, candidate, candidateRowId);
        var conflictedRowIds = conflicts
            .OrderBy(index => index)
            .Select(index => table.RowIds[index])
            .ToArray();
        var rows = new List<SqlValue[]>(table.Rows.Count - conflicts.Count + 1);
        var rowIds = new List<long>(table.RowIds.Count - conflicts.Count + 1);
        for (var index = 0; index < table.Rows.Count; index++)
        {
            if (conflicts.Contains(index))
                continue;

            rows.Add(table.Rows[index]);
            rowIds.Add(table.RowIds[index]);
        }

        rows.Add(candidate);
        rowIds.Add(candidateRowId);
        ValidateRowIdsUnique(tableName, table, rowIds, table.RowidAliasColumnIndex);
        table.ValidateRows(tableName, rows);
        ValidateColumnUniqueConstraints(table, rows);
        ValidatePrimaryKey(tableName, table, rows);
        ValidateUniqueIndexes(tableName, table, rows);
        ValidateForeignKeysAfterInsert(context, tableName, table, [candidate], rows);

        foreach (var conflictedRowId in conflictedRowIds)
        {
            var conflictIndex = table.RowIds.IndexOf(conflictedRowId);
            if (conflictIndex < 0)
                continue;

            table.Rows.RemoveAt(conflictIndex);
            table.RowIds.RemoveAt(conflictIndex);
            if (table.HasRowid)
                RecordBlobMutation(tableName, conflictedRowId);
            if (deleteTriggers.Count > 0)
                FireTriggers(deleteTriggers, context);
        }

        CommitInserts(context, tableName, table, [candidate], [candidateRowId]);
        if (insertTriggers.Count > 0)
            FireTriggers(insertTriggers, context);
    }

    private HashSet<int> FindReplacementConflicts(
        EmbeddedTable table,
        SqlValue[] candidate,
        long candidateRowId)
    {
        var constraints = new List<IReadOnlyList<UpsertConflictColumn>>();
        for (var columnIndex = 0; columnIndex < table.ColumnDefinitions.Length; columnIndex++)
        {
            var column = table.ColumnDefinitions[columnIndex];
            if (column.PrimaryKey)
                constraints.Add([new UpsertConflictColumn(column.Name, columnIndex, column.Collation)]);
        }

        if (table.TableLevelPrimaryKey is not null)
        {
            constraints.Add(table.PrimaryKeyColumns
                .Select((entry, position) => new UpsertConflictColumn(
                    table.Columns[entry.Index],
                    entry.Index,
                    table.TableLevelPrimaryKey[position].Collation
                        ?? table.ColumnDefinitions[entry.Index].Collation))
                .ToArray());
        }

        foreach (var index in table.Indexes.Where(index => index.Unique))
        {
            constraints.Add(index.Columns
                .Select(column => new UpsertConflictColumn(column.Name, column.ColumnIndex, column.Collation))
                .ToArray());
        }

        var conflicts = new HashSet<int>();
        for (var position = 0; position < table.Rows.Count; position++)
        {
            if (table.RowIds[position] == candidateRowId
                || constraints.Any(constraint => RowsConflictOnConstraint(table.Rows[position], candidate, constraint)))
            {
                conflicts.Add(position);
            }
        }

        return conflicts;
    }

    private bool RowsConflictOnConstraint(
        SqlValue[] existing,
        SqlValue[] candidate,
        IReadOnlyList<UpsertConflictColumn> constraint)
    {
        foreach (var column in constraint)
        {
            if (existing[column.Index].Kind == SqlValueKind.Null
                || candidate[column.Index].Kind == SqlValueKind.Null
                || Compare(existing[column.Index], candidate[column.Index], column.Collation) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsConflictAlgorithmConstraint(EmbeddedSqlException exception)
        => exception.Message.StartsWith("UNIQUE constraint failed:", StringComparison.Ordinal)
            || exception.Message.StartsWith("NOT NULL constraint failed:", StringComparison.Ordinal)
            || exception.Message.StartsWith("CHECK constraint failed:", StringComparison.Ordinal);

    private static bool HasForeignKeyParticipation(
        QueryContext context,
        string tableName,
        EmbeddedTable table)
    {
        if (!context.ForeignKeysEnabled)
            return false;
        if (table.ForeignKeys.Count > 0)
            return true;

        return context.Tables.Values.Any(candidate =>
            candidate.ForeignKeys.Any(foreignKey =>
                string.Equals(foreignKey.ParentTable, tableName, StringComparison.OrdinalIgnoreCase)));
    }

    // Trigger bodies can fail after their row mutations are published. Keep the complete
    // statement under one backup so every VALUES row and statement-level trigger is atomic.
    private ExecutionResult ExecuteUpsert(InsertStatement statement, SqlValue[] parameters, QueryContext context)
    {
        var backup = CloneTables(context.Tables);
        try
        {
            var (result, mutationEvents) = PerformUpsertEvaluated(statement, parameters, context);
            foreach (var triggerEvent in mutationEvents)
            {
                var triggers = GetMatchingTriggers(context, statement.TableName, triggerEvent);
                if (triggers.Count == 0)
                    continue;

                if (context.InsideTrigger)
                {
                    throw new EmbeddedSqlException(
                        $"cannot modify {statement.TableName} within a trigger body: recursive triggers are not supported");
                }

                FireTriggers(triggers, context);
            }

            return result;
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }
    }

    private (ExecutionResult Result, IReadOnlyList<TriggerEvent> MutationEvents) PerformUpsertEvaluated(
        InsertStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (statement.Upsert is null)
            throw new InvalidOperationException("UPSERT execution requires an UPSERT clause.");
        if (statement.Source is not null || context.CommonTableExpressions.Count != 0)
        {
            throw new EmbeddedSqlException(
                "Managed UPSERT supports VALUES rows only and does not support INSERT ... SELECT or CTE sources.");
        }
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        var conflictTarget = ResolveUpsertConflictTarget(statement.TableName, table, statement.Upsert.Target);
        var insertPlan = PrepareInsert(statement, table);
        var updateAction = statement.Upsert.Action as DoUpdateUpsertAction;
        UpdatePlan? updatePlan = null;
        if (updateAction is null && statement.Upsert.Action is not DoNothingUpsertAction)
            throw new InvalidOperationException("Unknown UPSERT action.");

        if (updateAction is not null)
        {
            var updateStatement = new UpdateStatement(statement.TableName, updateAction.Assignments, Where: null);
            updatePlan = PrepareUpdate(updateStatement, table);
            if (updatePlan.RowidAssignment is not null || updatePlan.ColumnAssignments.Any(
                    assignment => assignment.Index == table.RowidAliasColumnIndex))
            {
                throw new EmbeddedSqlException(
                    "Managed UPSERT DO UPDATE does not support assignments to rowid or an INTEGER PRIMARY KEY alias.");
            }

            ValidateUpsertUpdateExpressions(statement.TableName, updateAction.Assignments, updateAction.Where);
        }

        var affectedRows = new List<SqlValue[]>();
        var affectedRowIds = new List<long>();
        var mutationEvents = new List<TriggerEvent>();
        long? lastInsertRowId = null;
        foreach (var values in statement.Rows)
        {
            var (candidate, candidateRowId) = BuildInsertRow(
                statement,
                table,
                insertPlan,
                values,
                parameters,
                context,
                allowExistingRowid: true);
            var conflictPosition = FindUpsertConflictPosition(conflictTarget, candidate, table.Rows);

            if (conflictPosition < 0)
            {
                var rows = new List<SqlValue[]>(table.Rows.Count + 1);
                rows.AddRange(table.Rows);
                rows.Add(candidate);
                var rowIds = new List<long>(table.RowIds.Count + 1);
                rowIds.AddRange(table.RowIds);
                rowIds.Add(candidateRowId);

                ValidateRowIdsUnique(statement.TableName, table, rowIds, table.RowidAliasColumnIndex);
                table.ValidateRows(statement.TableName, rows);
                ValidateColumnUniqueConstraints(table, rows);
                ValidatePrimaryKey(statement.TableName, table, rows);
                ValidateUniqueIndexes(statement.TableName, table, rows);
                ValidateForeignKeysAfterInsert(context, statement.TableName, table, [candidate], rows);
                ApplyUpsertRows(table, rows, rowIds);

                affectedRows.Add(candidate);
                affectedRowIds.Add(candidateRowId);
                if (table.HasRowid)
                    lastInsertRowId = candidateRowId;
                if (!mutationEvents.Contains(TriggerEvent.Insert))
                    mutationEvents.Add(TriggerEvent.Insert);
                continue;
            }

            if (updateAction is null)
                continue;

            var original = table.Rows[conflictPosition];
            var originalRowId = table.RowIds[conflictPosition];
            var source = CreateUpsertSourceRow(
                statement.TableName,
                table,
                original,
                originalRowId,
                candidate);
            if (updateAction.Where is not null
                && !IsTrue(Evaluate(updateAction.Where, parameters, source, context)))
            {
                continue;
            }

            var updated = BuildUpsertUpdatedRow(
                statement.TableName,
                table,
                updatePlan!,
                original,
                originalRowId,
                source,
                parameters,
                context);
            var updatedRows = new List<SqlValue[]>(table.Rows);
            updatedRows[conflictPosition] = updated;
            var updatedRowIds = new List<long>(table.RowIds);

            ValidateRowIdsUnique(statement.TableName, table, updatedRowIds, updatePlan!.AliasIndex);
            table.ValidateRows(statement.TableName, updatedRows);
            ValidateColumnUniqueConstraints(table, updatedRows);
            ValidatePrimaryKey(statement.TableName, table, updatedRows);
            ValidateUniqueIndexes(statement.TableName, table, updatedRows);
            ValidateForeignKeysAfterUpdate(
                context,
                statement.TableName,
                table,
                table.Rows,
                updatedRows,
                updatePlan!,
                [conflictPosition]);
            ApplyUpsertRows(table, updatedRows, updatedRowIds);

            affectedRows.Add(updated);
            affectedRowIds.Add(originalRowId);
            if (!mutationEvents.Contains(TriggerEvent.Update))
                mutationEvents.Add(TriggerEvent.Update);
        }

        return (
            BuildUpsertReturningResult(
                statement,
                table,
                affectedRows,
                affectedRowIds,
                parameters,
                context,
                rowsAffected: affectedRows.Count,
                changed: affectedRows.Count > 0,
                lastInsertRowId: lastInsertRowId),
            mutationEvents);
    }

    private void ApplyUpsertRows(EmbeddedTable table, List<SqlValue[]> rows, List<long> rowIds)
    {
        table.Rows.Clear();
        table.Rows.AddRange(rows);
        table.RowIds.Clear();
        table.RowIds.AddRange(rowIds);
        SortWithoutRowid(table);
    }

    private ExecutionResult BuildUpsertReturningResult(
        InsertStatement statement,
        EmbeddedTable table,
        IReadOnlyList<SqlValue[]> affectedRows,
        IReadOnlyList<long> affectedRowIds,
        SqlValue[] parameters,
        QueryContext context,
        int rowsAffected,
        bool changed,
        long? lastInsertRowId)
    {
        if (statement.Returning is null)
        {
            return new ExecutionResult([], [], rowsAffected, changed)
            {
                LastInsertRowId = lastInsertRowId,
            };
        }

        return BuildReturningResult(
            statement.Returning,
            statement.TableName,
            table,
            affectedRows,
            affectedRowIds,
            rowsAffected,
            changed,
            parameters,
            context,
            lastInsertRowId);
    }

    private UpsertConflictTarget ResolveUpsertConflictTarget(
        string tableName,
        EmbeddedTable table,
        IReadOnlyList<UpsertTargetColumn> target)
    {
        if (target.Count == 0)
            throw new InvalidOperationException("UPSERT conflict target cannot be empty.");

        var matches = new List<UpsertConflictTarget>();
        foreach (var index in table.Indexes)
        {
            if (!index.Unique || !UpsertTargetMatches(
                    target,
                    index.Columns.Select(column => new UpsertConflictColumn(
                        column.Name,
                        column.ColumnIndex,
                        column.Collation)).ToArray()))
            {
                continue;
            }

            matches.Add(new UpsertConflictTarget(
                index.Columns.Select(column => new UpsertConflictColumn(
                    column.Name,
                    column.ColumnIndex,
                    column.Collation)).ToArray()));
        }

        if (table.PrimaryKeyColumns.Count > 0)
        {
            var columns = table.PrimaryKeyColumns
                .Select((entry, position) => new UpsertConflictColumn(
                    table.Columns[entry.Index],
                    entry.Index,
                    table.TableLevelPrimaryKey?[position].Collation
                        ?? table.ColumnDefinitions[entry.Index].Collation))
                .ToArray();
            if (UpsertTargetMatches(target, columns))
                matches.Add(new UpsertConflictTarget(columns));
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new EmbeddedSqlException(
                $"ON CONFLICT clause does not match any PRIMARY KEY or UNIQUE constraint on table {tableName}."),
            _ => throw new EmbeddedSqlException(
                "Managed UPSERT does not support a conflict target that matches multiple PRIMARY KEY or UNIQUE constraints."),
        };
    }

    private static bool UpsertTargetMatches(
        IReadOnlyList<UpsertTargetColumn> target,
        IReadOnlyList<UpsertConflictColumn> constraint)
    {
        if (target.Count != constraint.Count)
            return false;

        for (var index = 0; index < target.Count; index++)
        {
            if (!string.Equals(target[index].Name, constraint[index].Name, StringComparison.OrdinalIgnoreCase)
                || (target[index].Collation is not null
                    && !string.Equals(
                        target[index].Collation,
                        constraint[index].Collation ?? "BINARY",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private int FindUpsertConflictPosition(
        UpsertConflictTarget target,
        SqlValue[] candidate,
        IReadOnlyList<SqlValue[]> rows)
    {
        foreach (var column in target.Columns)
        {
            if (candidate[column.Index].Kind == SqlValueKind.Null)
                return -1;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var matches = true;
            foreach (var column in target.Columns)
            {
                if (Compare(rows[rowIndex][column.Index], candidate[column.Index], column.Collation) != 0)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return rowIndex;
        }

        return -1;
    }

    private SqlValue[] BuildUpsertUpdatedRow(
        string tableName,
        EmbeddedTable table,
        UpdatePlan plan,
        SqlValue[] original,
        long rowId,
        SourceRow source,
        SqlValue[] parameters,
        QueryContext context)
    {
        var updated = original.ToArray();
        foreach (var (index, value) in plan.ColumnAssignments)
            updated[index] = Evaluate(value, parameters, source, context);

        table.ApplyAffinities(updated);
        if (plan.AliasIndex >= 0)
            updated[plan.AliasIndex] = SqlValue.Integer(rowId);
        ComputeGeneratedColumns(table, tableName, updated, parameters, context);
        return updated;
    }

    private static SourceRow CreateUpsertSourceRow(
        string tableName,
        EmbeddedTable table,
        SqlValue[] target,
        long targetRowId,
        SqlValue[] excluded)
    {
        var values = new SqlValue[target.Length + excluded.Length];
        Array.Copy(target, values, target.Length);
        Array.Copy(excluded, 0, values, target.Length, excluded.Length);
        var qualified = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < table.Columns.Length; index++)
        {
            qualified.Add($"{tableName}.{table.Columns[index]}", index);
            qualified.Add($"excluded.{table.Columns[index]}", table.Columns.Length + index);
        }

        return new SourceRow(
            table.Columns,
            values,
            qualified,
            RowId: table.HasRowid ? targetRowId : null,
            RowIdQualifier: tableName);
    }

    private void ValidateUpsertUpdateExpressions(
        string tableName,
        IReadOnlyList<ColumnAssignment> assignments,
        Expression? where)
    {
        foreach (var assignment in assignments)
            ValidateUpsertUpdateExpression(tableName, assignment.Value);

        if (where is not null)
            ValidateUpsertUpdateExpression(tableName, where);
    }

    private void ValidateUpsertUpdateExpression(string tableName, Expression expression)
    {
        if (ContainsAggregate(expression)
            || ContainsWindowFunction(expression))
        {
            throw new EmbeddedSqlException(
                "Managed UPSERT DO UPDATE does not support aggregate or window expressions.");
        }

        switch (expression)
        {
            case LiteralExpression:
            case ParameterExpression:
                return;
            case ColumnExpression column:
                {
                    var separator = column.Name.IndexOf('.');
                    if (separator < 0)
                        return;

                    var qualifier = column.Name[..separator];
                    var name = column.Name[(separator + 1)..];
                    if (qualifier.Equals("excluded", StringComparison.OrdinalIgnoreCase)
                        && EmbeddedTable.IsRowidAliasName(name))
                    {
                        throw new EmbeddedSqlException(
                            "Managed UPSERT DO UPDATE does not support excluded.rowid references.");
                    }

                    if (!qualifier.Equals("excluded", StringComparison.OrdinalIgnoreCase)
                        && !qualifier.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new EmbeddedSqlException(
                            $"no such table: {qualifier}");
                    }

                    return;
                }
            case ScalarSubqueryExpression or ExistsExpression or InSubqueryExpression:
                throw new EmbeddedSqlException(
                    "Managed UPSERT DO UPDATE does not support subquery expressions.");
            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                    ValidateUpsertUpdateExpression(tableName, argument);
                return;
            case CollationExpression collation:
                ValidateUpsertUpdateExpression(tableName, collation.Expression);
                return;
            case CastExpression cast:
                ValidateUpsertUpdateExpression(tableName, cast.Expression);
                return;
            case CaseExpression @case:
                if (@case.Operand is not null)
                    ValidateUpsertUpdateExpression(tableName, @case.Operand);
                foreach (var clause in @case.Clauses)
                {
                    ValidateUpsertUpdateExpression(tableName, clause.When);
                    ValidateUpsertUpdateExpression(tableName, clause.Then);
                }
                if (@case.Else is not null)
                    ValidateUpsertUpdateExpression(tableName, @case.Else);
                return;
            case LikeExpression like:
                ValidateUpsertUpdateExpression(tableName, like.Value);
                ValidateUpsertUpdateExpression(tableName, like.Pattern);
                if (like.Escape is not null)
                    ValidateUpsertUpdateExpression(tableName, like.Escape);
                return;
            case GlobExpression glob:
                ValidateUpsertUpdateExpression(tableName, glob.Value);
                ValidateUpsertUpdateExpression(tableName, glob.Pattern);
                return;
            case InExpression @in:
                ValidateUpsertUpdateExpression(tableName, @in.Value);
                foreach (var value in @in.Values)
                    ValidateUpsertUpdateExpression(tableName, value);
                return;
            case BetweenExpression between:
                ValidateUpsertUpdateExpression(tableName, between.Value);
                ValidateUpsertUpdateExpression(tableName, between.Lower);
                ValidateUpsertUpdateExpression(tableName, between.Upper);
                return;
            case UnaryExpression unary:
                ValidateUpsertUpdateExpression(tableName, unary.Operand);
                return;
            case BinaryExpression binary:
                ValidateUpsertUpdateExpression(tableName, binary.Left);
                ValidateUpsertUpdateExpression(tableName, binary.Right);
                return;
            case StarExpression or QualifiedStarExpression:
                throw new EmbeddedSqlException("row value misused");
            default:
                throw new EmbeddedSqlException(
                    $"Managed UPSERT DO UPDATE does not support {expression.GetType().Name} expressions.");
        }
    }

    private sealed record UpsertConflictColumn(string Name, int Index, string? Collation);

    private sealed record UpsertConflictTarget(IReadOnlyList<UpsertConflictColumn> Columns);

    // Materializes every generated column into the row in dependency order, applying the
    // declared-type affinity and enforcing NOT NULL with a table-qualified message. The
    // generation expression sees the row's stored values but never a rowid pseudo-column.
    private void ComputeGeneratedColumns(
        EmbeddedTable table,
        string tableName,
        SqlValue[] row,
        SqlValue[] parameters,
        QueryContext context,
        bool virtualOnly = false)
    {
        if (!table.HasGeneratedColumns)
            return;

        var source = new SourceRow(table.Columns, row);
        foreach (var columnIndex in table.GeneratedColumnOrder)
        {
            var column = table.ColumnDefinitions[columnIndex];
            if (virtualOnly && column.GeneratedStored)
                continue;

            var value = EmbeddedTable.ApplyColumnAffinity(
                column,
                Evaluate(column.GenerationExpression!, parameters, source, context));
            row[columnIndex] = value;
            if (column.NotNull && value.Kind == SqlValueKind.Null)
            {
                throw new EmbeddedSqlException(
                    $"NOT NULL constraint failed: {tableName}.{column.Name}",
                    column.NotNullConflictAlgorithm);
            }
        }
    }

    internal static void RecomputeVirtualGeneratedColumns(
        EmbeddedTable table,
        string tableName,
        SqlValue[] row)
    {
        if (!table.HasGeneratedColumns)
            return;

        var evaluator = new EmbeddedDatabase();
        evaluator.ComputeGeneratedColumns(
            table,
            tableName,
            row,
            [],
            new QueryContext(
                new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase)
                {
                    [tableName] = table,
                },
                new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase)),
            virtualOnly: true);
    }

    // Enforces primary-key integrity that a rowid alias cannot cover: WITHOUT ROWID keys are
    // implicitly NOT NULL and unique on the full tuple, while a table-level PRIMARY KEY on a
    // rowid table behaves like a UNIQUE index (NULLs distinct). Messages are table-qualified
    // and list key columns in declaration order, matching SQLite.
    private void ValidatePrimaryKey(string tableName, EmbeddedTable table, IReadOnlyList<SqlValue[]> rows)
    {
        if (!table.WithoutRowid && table.TableLevelPrimaryKey is null)
            return;

        var primaryKey = table.PrimaryKeyColumns;
        if (primaryKey.Count == 0)
            return;

        if (table.WithoutRowid)
        {
            foreach (var row in rows)
            {
                foreach (var (columnIndex, _) in primaryKey)
                {
                    if (row[columnIndex].Kind == SqlValueKind.Null)
                        throw new EmbeddedSqlException(
                            $"NOT NULL constraint failed: {tableName}.{table.Columns[columnIndex]}",
                            table.PrimaryKeyConflictAlgorithm);
                }
            }
        }

        var seenKeys = new List<SqlValue[]>();
        foreach (var row in rows)
        {
            var key = new SqlValue[primaryKey.Count];
            var hasNull = false;
            for (var index = 0; index < primaryKey.Count; index++)
            {
                key[index] = row[primaryKey[index].Index];
                if (key[index].Kind == SqlValueKind.Null)
                    hasNull = true;
            }

            // A rowid table's PRIMARY KEY is backed by a UNIQUE index whose NULLs are
            // distinct; WITHOUT ROWID has already rejected NULL key columns above.
            if (hasNull && !table.WithoutRowid)
                continue;

            foreach (var existing in seenKeys)
            {
                var conflict = true;
                for (var index = 0; index < primaryKey.Count; index++)
                {
                    var columnIndex = primaryKey[index].Index;
                    var collation = table.TableLevelPrimaryKey?[index].Collation
                        ?? table.ColumnDefinitions[columnIndex].Collation;
                    if (Compare(existing[index], key[index], collation) != 0)
                    {
                        conflict = false;
                        break;
                    }
                }

                if (conflict)
                {
                    var columns = primaryKey.Select(entry => $"{tableName}.{table.Columns[entry.Index]}");
                    throw new EmbeddedSqlException(
                        $"UNIQUE constraint failed: {string.Join(", ", columns)}",
                        table.PrimaryKeyConflictAlgorithm);
                }
            }

            seenKeys.Add(key);
        }
    }

    // Re-sorts a WITHOUT ROWID table's rows into primary-key order (honoring per-column
    // ASC/DESC) so scans observe the physical key order SQLite exposes. The parallel rowid
    // list is reordered in lock-step to stay index-aligned with the rows.
    private void SortWithoutRowid(EmbeddedTable table)
    {
        if (!table.WithoutRowid)
            return;

        var primaryKey = table.PrimaryKeyColumns;
        var primaryKeySchema = table.PrimaryKeySchema
            ?? throw new InvalidOperationException("WITHOUT ROWID table is missing primary-key metadata.");
        SqliteIndexRecordComparer? persistedComparer = null;
        if (primaryKeySchema.Terms.All(term => term.Collation.IsSupportedByManagedIndexWriter))
        {
            persistedComparer = new SqliteIndexRecordComparer(
                SqliteTextEncoding.Utf8,
                primaryKeySchema.Terms.Select(term =>
                    new SqliteIndexComparisonTerm(term.SortOrder, term.Collation)).ToArray());
        }
        var keys = persistedComparer is null
            ? null
            : table.Rows.Select(primaryKeySchema.ProjectKey).ToArray();
        var order = Enumerable.Range(0, table.Rows.Count).ToList();
        order.Sort((left, right) =>
        {
            if (persistedComparer is not null)
                return persistedComparer.Compare(keys![left], keys[right]);

            for (var position = 0; position < primaryKey.Count; position++)
            {
                var (columnIndex, descending) = primaryKey[position];
                var collation = table.TableLevelPrimaryKey?[position].Collation
                    ?? table.ColumnDefinitions[columnIndex].Collation;
                var comparison = Compare(
                    table.Rows[left][columnIndex],
                    table.Rows[right][columnIndex],
                    collation);
                if (comparison != 0)
                    return descending ? -comparison : comparison;
            }

            return 0;
        });

        var sortedRows = order.Select(index => table.Rows[index]).ToList();
        var sortedRowIds = order.Select(index => table.RowIds[index]).ToList();
        table.Rows.Clear();
        table.Rows.AddRange(sortedRows);
        table.RowIds.Clear();
        table.RowIds.AddRange(sortedRowIds);
    }

    // Routes an INSERT through the bytecode compiler when it falls inside the supported
    // subset (real base table, lowerable RETURNING), running the emitted program as a
    // real write path. Everything else keeps the tree-walking evaluator. The compiled
    // attempt sits inside the trigger-wrapped Perform* so atomicity and triggers apply
    // to both paths identically.
    private ExecutionResult PerformInsert(InsertStatement statement, SqlValue[] parameters, QueryContext context)
    {
        if (CanRouteInsertThroughCompiler(statement, context)
            && TryCompileInsert(statement, parameters, context, out var compiled, out var columns, out var hasReturning))
            return RunCompiledDml(compiled, columns, hasReturning, parameters);

        return PerformInsertEvaluated(statement, parameters, context);
    }

    private ExecutionResult PerformInsertEvaluated(InsertStatement statement, SqlValue[] parameters, QueryContext context)
    {
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        var plan = PrepareInsert(statement, table);
        var sourceRows = statement.Source is null
            ? null
            : ExecuteQuery(statement.Source, parameters, context, outerRow: null).Rows;
        var rowCount = sourceRows?.Count ?? statement.Rows.Count;
        var rowsToInsert = new List<SqlValue[]>(rowCount);
        var insertedRowIds = new List<long>(rowCount);
        if (sourceRows is not null)
        {
            foreach (var sourceRow in sourceRows)
            {
                var (row, rowid) = BuildInsertRow(statement, table, plan, sourceRow, parameters, context);
                rowsToInsert.Add(row);
                insertedRowIds.Add(rowid);
            }
        }
        else
        {
            foreach (var valueExpressions in statement.Rows)
            {
                var (row, rowid) = BuildInsertRow(statement, table, plan, valueExpressions, parameters, context);
                rowsToInsert.Add(row);
                insertedRowIds.Add(rowid);
            }
        }

        CommitInserts(context, statement.TableName, table, rowsToInsert, insertedRowIds);

        var lastInsertRowId = table.HasRowid && insertedRowIds.Count > 0
            ? insertedRowIds[^1]
            : (long?)null;
        if (statement.Returning is not null)
        {
            return BuildReturningResult(
                statement.Returning,
                statement.TableName,
                table,
                rowsToInsert,
                insertedRowIds,
                rowsToInsert.Count,
                rowsToInsert.Count > 0,
                parameters,
                context,
                lastInsertRowId);
        }

        return new ExecutionResult([], [], rowsToInsert.Count, rowsToInsert.Count > 0)
        {
            LastInsertRowId = lastInsertRowId,
        };
    }

    // Resolves the INSERT's target columns and the mutable rowid-allocation state shared
    // across every value row. Extracted so the evaluated loop and the compiled write
    // target build rows through identical logic.
    private InsertPlan PrepareInsert(InsertStatement statement, EmbeddedTable table)
    {
        // The default column list excludes generated columns, which are computed rather than
        // supplied, matching SQLite's INSERT ... VALUES arity for generated-column tables.
        var targetColumns = statement.Columns
            ?? table.ColumnDefinitions.Where(column => !column.IsGenerated).Select(column => column.Name).ToArray();

        // Resolve each target to a real column index, or to the rowid pseudo-column (-1)
        // when the name is rowid/_rowid_/oid and no declared column shadows it.
        var targetIndices = new int[targetColumns.Length];
        var rowidTargetPosition = -1;
        for (var index = 0; index < targetColumns.Length; index++)
        {
            if (table.TryGetColumnIndex(targetColumns[index], out var columnIndex))
            {
                if (table.ColumnDefinitions[columnIndex].IsGenerated)
                    throw new EmbeddedSqlException(
                        $"cannot INSERT into generated column \"{table.Columns[columnIndex]}\"");

                targetIndices[index] = columnIndex;
            }
            else if (table.HasRowid && EmbeddedTable.IsRowidAliasName(targetColumns[index]))
            {
                targetIndices[index] = -1;
                rowidTargetPosition = index;
            }
            else
            {
                throw new EmbeddedSqlException($"table {statement.TableName} has no column named {targetColumns[index]}");
            }
        }

        var anyRow = table.RowIds.Count > 0;
        return new InsertPlan
        {
            TargetIndices = targetIndices,
            RowidTargetPosition = rowidTargetPosition,
            AliasIndex = table.RowidAliasColumnIndex,
            Used = new HashSet<long>(table.RowIds),
            AnyRow = anyRow,
            LargestRowId = anyRow ? table.RowIds.Max() : long.MinValue,
        };
    }

    // Builds a single INSERT row and assigns its rowid, threading the shared allocation
    // state through <paramref name="plan"/> so successive rows see each other's rowids.
    private (SqlValue[] Row, long RowId) BuildInsertRow(
        InsertStatement statement,
        EmbeddedTable table,
        InsertPlan plan,
        Expression[] valueExpressions,
        SqlValue[] parameters,
        QueryContext context,
        bool allowExistingRowid = false)
    {
        var values = new SqlValue[valueExpressions.Length];
        for (var index = 0; index < valueExpressions.Length; index++)
            values[index] = Evaluate(valueExpressions[index], parameters, null, context);

        return BuildInsertRow(statement, table, plan, values, parameters, context, allowExistingRowid);
    }

    private (SqlValue[] Row, long RowId) BuildInsertRow(
        InsertStatement statement,
        EmbeddedTable table,
        InsertPlan plan,
        IReadOnlyList<SqlValue> values,
        SqlValue[] parameters,
        QueryContext context,
        bool allowExistingRowid = false)
    {
        if (values.Count != plan.TargetIndices.Length)
            throw new EmbeddedSqlException("table has a different number of columns");

        var row = table.CreateRowWithDefaults(
            expression => Evaluate(expression, EmptyParameters, row: null, context));
        var assignedColumns = new HashSet<int>();
        SqlValue explicitRowidValue = SqlValue.Null;
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (plan.TargetIndices[index] < 0)
                explicitRowidValue = value; // rowid pseudo-column: last write wins.
            else if (assignedColumns.Add(plan.TargetIndices[index]))
                row[plan.TargetIndices[index]] = value;
        }

        for (var columnIndex = 0; columnIndex < table.ColumnDefinitions.Length; columnIndex++)
        {
            var column = table.ColumnDefinitions[columnIndex];
            var conflictAlgorithm = statement.ConflictAlgorithm ?? column.NotNullConflictAlgorithm;
            if (!column.NotNull
                || row[columnIndex].Kind != SqlValueKind.Null
                || conflictAlgorithm != InsertConflictAlgorithm.Replace
                || !column.HasDefault)
            {
                continue;
            }

            row[columnIndex] = column.DefaultExpression is { } expression
                ? Evaluate(expression, EmptyParameters, row: null, context)
                : column.DefaultValue
                    ?? throw new InvalidOperationException("Default metadata is incomplete.");
        }

        table.ApplyAffinities(row);

        // The rowid pseudo-column overrides the alias column when both are supplied,
        // matching SQLite; otherwise a rowid-alias table takes the alias column value.
        var rowidSource = plan.RowidTargetPosition >= 0
            ? explicitRowidValue
            : plan.AliasIndex >= 0 ? row[plan.AliasIndex] : SqlValue.Null;

        long rowid;
        if (rowidSource.Kind == SqlValueKind.Null)
        {
            rowid = plan.AnyRow ? NextAutoRowId(plan.LargestRowId, plan.Used) : 1;
            plan.Used.Add(rowid);
        }
        else if (EmbeddedTable.TryCoerceRowid(rowidSource, out var explicitRowid))
        {
            rowid = explicitRowid;
            if (!plan.Used.Add(rowid))
            {
                if (!allowExistingRowid)
                {
                    var conflictColumn = plan.AliasIndex >= 0 ? table.Columns[plan.AliasIndex] : "rowid";
                    throw new EmbeddedSqlException(
                        $"UNIQUE constraint failed: {statement.TableName}.{conflictColumn}",
                        plan.AliasIndex >= 0
                            ? table.RowidAliasConflictAlgorithm
                            : null);
                }
            }
        }
        else
        {
            throw new EmbeddedSqlException("datatype mismatch");
        }

        if (!plan.AnyRow || rowid > plan.LargestRowId)
            plan.LargestRowId = rowid;
        plan.AnyRow = true;

        // A rowid-alias column stores exactly the rowid value.
        if (plan.AliasIndex >= 0)
            row[plan.AliasIndex] = SqlValue.Integer(rowid);

        // Generated columns are computed after the base columns (and any rowid alias)
        // are final, so they can reference every stored column value.
        ComputeGeneratedColumns(table, statement.TableName, row, parameters, context);
        ValidateCheckConstraints(statement.TableName, table, row, rowid, parameters, context);

        return (row, rowid);
    }

    // Validates the pending inserts against the whole table, then appends them and
    // restores WITHOUT ROWID key order. A validation failure leaves the table untouched.
    private void CommitInserts(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        List<SqlValue[]> rowsToInsert,
        List<long> insertedRowIds)
    {
        ValidateRowids(tableName, table, insertedRowIds);
        var allRows = new List<SqlValue[]>(table.Rows.Count + rowsToInsert.Count);
        allRows.AddRange(table.Rows);
        allRows.AddRange(rowsToInsert);
        table.ValidateRows(tableName, allRows);
        ValidateColumnUniqueConstraints(table, allRows);
        ValidatePrimaryKey(tableName, table, allRows);
        ValidateUniqueIndexes(tableName, table, allRows);
        ValidateForeignKeysAfterInsert(context, tableName, table, rowsToInsert, allRows);
        table.Rows.AddRange(rowsToInsert);
        table.RowIds.AddRange(insertedRowIds);
        SortWithoutRowid(table);
        if (table.HasRowid)
        {
            foreach (var rowId in insertedRowIds)
                RecordBlobMutation(tableName, rowId);
        }
    }

    private static void ValidateRowids(
        string tableName,
        EmbeddedTable table,
        IReadOnlyList<long> insertedRowIds)
    {
        if (!table.HasRowid)
            return;

        var used = new HashSet<long>(table.RowIds);
        foreach (var rowId in insertedRowIds)
        {
            if (used.Add(rowId))
                continue;

            var aliasIndex = table.RowidAliasColumnIndex;
            var conflictColumn = aliasIndex >= 0 ? table.Columns[aliasIndex] : "rowid";
            throw new EmbeddedSqlException(
                $"UNIQUE constraint failed: {tableName}.{conflictColumn}",
                aliasIndex >= 0
                    ? table.RowidAliasConflictAlgorithm
                    : null);
        }
    }

    // Mutable per-statement INSERT plan: the resolved column targets plus the rowid
    // allocation state threaded across value rows.
    private sealed class InsertPlan
    {
        public required int[] TargetIndices { get; init; }

        public required int RowidTargetPosition { get; init; }

        public required int AliasIndex { get; init; }

        public required HashSet<long> Used { get; init; }

        public bool AnyRow { get; set; }

        public long LargestRowId { get; set; }
    }

    // Computes the next autogenerated rowid: one greater than the largest rowid in use,
    // or a random unused positive value once the maximum integer rowid is reached, exactly
    // as SQLite does.
    private static long NextAutoRowId(long largestRowId, HashSet<long> used)
    {
        if (largestRowId != long.MaxValue)
            return largestRowId + 1;

        while (true)
        {
            var candidate = Random.Shared.NextInt64(1, long.MaxValue);
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    private ExecutionResult ExecuteUpdate(
        UpdateStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        return ExecuteWithTriggers(
            statement.TableName,
            TriggerEvent.Update,
            context,
            () => PerformUpdate(statement, parameters, context));
    }

    private ExecutionResult PerformUpdate(
        UpdateStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (CanRouteUpdateThroughCompiler(statement, context)
            && TryCompileUpdate(statement, parameters, context, out var compiled, out var columns, out var hasReturning))
            return RunCompiledDml(compiled, columns, hasReturning, parameters);

        return PerformUpdateEvaluated(statement, parameters, context);
    }

    private ExecutionResult PerformUpdateEvaluated(
        UpdateStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        var plan = PrepareUpdate(statement, table);
        var selectedPositions = statement.Limit is null
            ? null
            : SelectLimitedDmlPositions(
                statement.TableName,
                table,
                statement.Where,
                statement.EffectiveOrderBy,
                statement.Limit,
                statement.Offset,
                statement.Assignments.Select(assignment => assignment.Value),
                statement.Returning,
                parameters,
                context);
        var rows = table.Rows.Select(row => row.ToArray()).ToList();
        var rowIds = table.RowIds.Count == table.Rows.Count
            ? table.RowIds.ToList()
            : Enumerable.Range(1, table.Rows.Count).Select(position => (long)position).ToList();
        var updatedRows = statement.Returning is null ? null : new List<SqlValue[]>();
        var updatedRowIds = statement.Returning is null ? null : new List<long>();
        var updatedPositions = new List<int>();
        var rowsAffected = 0;
        var updateOrder = table.HasRowid
            ? Enumerable.Range(0, table.Rows.Count).OrderBy(position => rowIds[position]).ToArray()
            : Enumerable.Range(0, table.Rows.Count).ToArray();
        foreach (var position in updateOrder)
        {
            var row = table.Rows[position];
            var rowid = position < table.RowIds.Count ? table.RowIds[position] : position + 1;
            if (selectedPositions is not null)
            {
                if (!selectedPositions.Contains(position))
                    continue;
            }
            else if (statement.Where is not null)
            {
                var source = new SourceRow(
                    table.Columns,
                    row,
                    RowId: table.HasRowid ? rowid : null,
                    RowIdQualifier: statement.TableName);
                if (!IsTrue(Evaluate(statement.Where, parameters, source, context)))
                    continue;
            }

            SqlValue[]? updated = null;
            var newRowid = rowid;
            try
            {
                (updated, newRowid) = BuildUpdatedRow(
                    statement,
                    table,
                    plan,
                    row,
                    rowid,
                    parameters,
                    context);
                rows[position] = updated;
                rowIds[position] = newRowid;
                ValidateRowIdsUnique(statement.TableName, table, rowIds, plan.AliasIndex);
                table.ValidateRows(statement.TableName, rows);
                ValidateColumnUniqueConstraints(table, rows);
                ValidatePrimaryKey(statement.TableName, table, rows);
                ValidateUniqueIndexes(statement.TableName, table, rows);
            }
            catch (EmbeddedSqlException exception)
            {
                rows[position] = row;
                rowIds[position] = rowid;
                if (exception.ConflictAlgorithm == InsertConflictAlgorithm.Ignore)
                    continue;
                if (exception.ConflictAlgorithm is InsertConflictAlgorithm.Fail
                    or InsertConflictAlgorithm.Rollback
                    or InsertConflictAlgorithm.Replace)
                {
                    throw new EmbeddedSqlException(
                        "Managed UPDATE cannot apply schema-level ON CONFLICT "
                        + $"{exception.ConflictAlgorithm.Value.ToString().ToUpperInvariant()} until the pending "
                        + "row-update engine supports partial publication, transaction rollback, and replacement.");
                }
                throw;
            }
            updatedRows?.Add(updated!);
            updatedRowIds?.Add(newRowid);
            updatedPositions.Add(position);
            rowsAffected++;
        }

        ExecutionResult? returningResult = null;
        CommitUpdates(
            context,
            statement.TableName,
            table,
            table.Rows,
            rows,
            rowIds,
            plan,
            updatedPositions,
            statement.Returning is null
                ? null
                : () => returningResult = BuildReturningResult(
                    statement.Returning,
                    statement.TableName,
                    table,
                    updatedRows!,
                    updatedRowIds!,
                    rowsAffected,
                    rowsAffected > 0,
                    parameters,
                    context));
        if (statement.Returning is not null)
            return returningResult!;

        return new ExecutionResult([], [], rowsAffected, rowsAffected > 0);
    }

    private HashSet<int> SelectLimitedDmlPositions(
        string tableName,
        EmbeddedTable table,
        Expression? where,
        IReadOnlyList<OrderByTerm> orderBy,
        Expression limitExpression,
        Expression? offsetExpression,
        IEnumerable<Expression> mutationExpressions,
        IReadOnlyList<Projection>? returning,
        SqlValue[] parameters,
        QueryContext context)
    {
        ValidateOrderByCollations(orderBy);
        var validationRow = new SourceRow(
            table.Columns,
            Enumerable.Repeat(SqlValue.Null, table.Columns.Length).ToArray(),
            RowId: table.HasRowid ? 1 : null,
            RowIdQualifier: tableName);
        ValidateColumnReferences(where, validationRow);
        foreach (var expression in mutationExpressions)
            ValidateColumnReferences(expression, validationRow);
        foreach (var term in orderBy)
            ValidateColumnReferences(term.Expression, validationRow);
        if (returning is not null)
        {
            foreach (var projection in returning)
            {
                if (projection.Expression is not (StarExpression or QualifiedStarExpression))
                    ValidateColumnReferences(projection.Expression, validationRow);
            }
        }

        var limit = RequireLimitInteger(Evaluate(limitExpression, parameters, null, context));
        var offset = offsetExpression is null
            ? 0
            : Math.Max(0, RequireLimitInteger(Evaluate(offsetExpression, parameters, null, context)));
        if (limit == 0)
            return [];

        var candidates = new List<LimitedDmlCandidate>();
        for (var position = 0; position < table.Rows.Count; position++)
        {
            var rowid = position < table.RowIds.Count ? table.RowIds[position] : position + 1;
            var source = new SourceRow(
                table.Columns,
                table.Rows[position],
                RowId: table.HasRowid ? rowid : null,
                RowIdQualifier: tableName);
            if (where is not null && !IsTrue(Evaluate(where, parameters, source, context)))
                continue;

            var orderValues = new SqlValue[orderBy.Count];
            for (var index = 0; index < orderBy.Count; index++)
                orderValues[index] = Evaluate(orderBy[index].Expression, parameters, source, context);
            candidates.Add(new LimitedDmlCandidate(position, orderValues));
        }

        if (orderBy.Count > 0)
        {
            candidates.Sort((left, right) =>
            {
                for (var index = 0; index < orderBy.Count; index++)
                {
                    var term = orderBy[index];
                    var comparison = CompareForOrdering(
                        left.OrderValues[index],
                        right.OrderValues[index],
                        term,
                        GetCollation(term.Expression));
                    if (comparison == 0)
                        continue;
                    return comparison;
                }

                return left.Position.CompareTo(right.Position);
            });
        }

        var selected = new HashSet<int>();
        long skipped = 0;
        long taken = 0;
        foreach (var candidate in candidates)
        {
            if (skipped < offset)
            {
                skipped++;
                continue;
            }
            if (limit >= 0 && taken >= limit)
                break;

            selected.Add(candidate.Position);
            taken++;
        }

        return selected;
    }

    // Resolves the UPDATE's column and rowid assignments. Extracted so the evaluated loop
    // and the compiled write target update rows through identical logic.
    private UpdatePlan PrepareUpdate(UpdateStatement statement, EmbeddedTable table)
    {
        var columnAssignments = new List<(int Index, Expression Value)>();
        Expression? rowidAssignment = null;
        foreach (var assignment in statement.Assignments)
        {
            if (table.TryGetColumnIndex(assignment.Column, out var index))
            {
                if (table.ColumnDefinitions[index].IsGenerated)
                    throw new EmbeddedSqlException(
                        $"cannot UPDATE generated column \"{table.Columns[index]}\"");

                columnAssignments.Add((index, assignment.Value));
            }
            else if (table.HasRowid && EmbeddedTable.IsRowidAliasName(assignment.Column))
            {
                rowidAssignment = assignment.Value; // rowid pseudo-column: last write wins.
            }
            else
            {
                throw new EmbeddedSqlException($"no such column: {assignment.Column}");
            }
        }

        return new UpdatePlan
        {
            AliasIndex = table.RowidAliasColumnIndex,
            ColumnAssignments = columnAssignments,
            RowidAssignment = rowidAssignment,
        };
    }

    // Applies the UPDATE assignments to one matched row, computing its new rowid and
    // regenerated columns. The original row and its rowid drive the value expressions.
    private (SqlValue[] Row, long RowId) BuildUpdatedRow(
        UpdateStatement statement,
        EmbeddedTable table,
        UpdatePlan plan,
        SqlValue[] originalRow,
        long rowid,
        SqlValue[] parameters,
        QueryContext context)
    {
        var source = new SourceRow(
            table.Columns,
            originalRow,
            RowId: table.HasRowid ? rowid : null,
            RowIdQualifier: statement.TableName);

        var updated = originalRow.ToArray();
        foreach (var (index, value) in plan.ColumnAssignments)
            updated[index] = Evaluate(value, parameters, source, context);

        table.ApplyAffinities(updated);

        // The rowid pseudo-column overrides a reassigned alias column; when neither is
        // touched the alias column still equals the unchanged rowid.
        var newRowid = rowid;
        if (plan.RowidAssignment is not null)
            newRowid = CoerceRowidOrThrow(Evaluate(plan.RowidAssignment, parameters, source, context));
        else if (plan.AliasIndex >= 0)
            newRowid = CoerceRowidOrThrow(updated[plan.AliasIndex]);

        if (plan.AliasIndex >= 0)
            updated[plan.AliasIndex] = SqlValue.Integer(newRowid);

        // Recompute generated columns from the freshly updated base values so a change
        // to any source column is reflected in the stored generated value.
        ComputeGeneratedColumns(table, statement.TableName, updated, parameters, context);
        ValidateCheckConstraints(statement.TableName, table, updated, newRowid, parameters, context);

        return (updated, newRowid);
    }

    private void ValidateCheckConstraints(
        string tableName,
        EmbeddedTable table,
        SqlValue[] row,
        long rowid,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (!table.HasCheckConstraints)
            return;

        var source = new SourceRow(
            table.Columns,
            row,
            BuildQualifiedColumns(tableName, table.Columns),
            RowId: table.HasRowid ? rowid : null,
            RowIdQualifier: tableName);
        foreach (var column in table.ColumnDefinitions)
        {
            foreach (var check in column.CheckConstraints)
                Validate(check);
        }
        foreach (var check in table.CheckConstraints)
            Validate(check);

        void Validate(CheckConstraint check)
        {
            var value = Evaluate(check.Expression, parameters, source, context);
            if (value.Kind != SqlValueKind.Null && !IsTrue(value))
            {
                throw new EmbeddedSqlException(
                    $"CHECK constraint failed: {check.Name ?? check.Sql}",
                    InsertConflictAlgorithm.Abort);
            }
        }
    }

    // Validates the fully assembled post-update rows, then swaps them in and restores
    // WITHOUT ROWID key order. A validation failure leaves the table untouched.
    private void CommitUpdates(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        IReadOnlyList<SqlValue[]> originalRows,
        List<SqlValue[]> rows,
        List<long> rowIds,
        UpdatePlan plan,
        IReadOnlyList<int> updatedPositions,
        Action? beforeMutation = null)
    {
        ValidateRowIdsUnique(tableName, table, rowIds, plan.AliasIndex);
        table.ValidateRows(tableName, rows);
        ValidateColumnUniqueConstraints(table, rows);
        ValidatePrimaryKey(tableName, table, rows);
        ValidateUniqueIndexes(tableName, table, rows);
        ValidateForeignKeysAfterUpdate(
            context,
            tableName,
            table,
            originalRows,
            rows,
            plan,
            updatedPositions);
        beforeMutation?.Invoke();
        var originalRowIds = table.HasRowid
            ? updatedPositions.Select(position => table.RowIds[position]).ToArray()
            : [];
        table.Rows.Clear();
        table.Rows.AddRange(rows);
        table.RowIds.Clear();
        table.RowIds.AddRange(rowIds);
        SortWithoutRowid(table);
        if (table.HasRowid)
        {
            for (var index = 0; index < updatedPositions.Count; index++)
            {
                var position = updatedPositions[index];
                RecordBlobMutation(tableName, originalRowIds[index]);
                RecordBlobMutation(tableName, rowIds[position]);
            }
        }
    }

    // Foreign-key checks run against the complete post-statement image, but only inspect
    // rows or parent keys touched by this statement. That preserves SQLite's behavior when
    // enforcement is enabled after legacy violations were inserted with foreign_keys OFF.
    private void ValidateForeignKeysAfterInsert(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        IReadOnlyList<SqlValue[]> insertedRows,
        IReadOnlyList<SqlValue[]> postInsertRows)
    {
        if (!context.ForeignKeysEnabled || insertedRows.Count == 0)
            return;

        ValidateChildForeignKeys(context, tableName, table, insertedRows, tableName, postInsertRows);
    }

    private void ValidateForeignKeysAfterUpdate(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        IReadOnlyList<SqlValue[]> originalRows,
        IReadOnlyList<SqlValue[]> postUpdateRows,
        UpdatePlan plan,
        IReadOnlyList<int> updatedPositions)
    {
        if (!context.ForeignKeysEnabled || updatedPositions.Count == 0)
            return;

        var assignedColumns = plan.ColumnAssignments
            .Select(assignment => assignment.Index)
            .ToHashSet();
        var changedRows = updatedPositions.Select(position => postUpdateRows[position]).ToArray();
        foreach (var foreignKey in table.ForeignKeys)
        {
            if (table.TryGetColumnIndex(foreignKey.ChildColumn, out var childColumn)
                && assignedColumns.Contains(childColumn))
            {
                ValidateChildForeignKeys(context, tableName, table, changedRows, tableName, postUpdateRows, foreignKey);
            }
        }

        ValidateChildrenAfterParentUpdate(
            context,
            tableName,
            originalRows,
            postUpdateRows,
            assignedColumns,
            updatedPositions);
    }

    private void ValidateForeignKeysAfterDelete(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        IReadOnlyList<SqlValue[]> originalRows,
        IReadOnlyList<SqlValue[]> postDeleteRows,
        IReadOnlyList<SqlValue[]> deletedRows)
    {
        if (!context.ForeignKeysEnabled || deletedRows.Count == 0)
            return;

        foreach (var (childTableName, childTable) in context.Tables)
        {
            foreach (var foreignKey in childTable.ForeignKeys)
            {
                if (!string.Equals(foreignKey.ParentTable, tableName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var parent = ResolveForeignKeyParent(context.Tables, childTableName, foreignKey);
                foreach (var deletedRow in deletedRows)
                {
                    ValidateChildrenReferencingParentValue(
                        context,
                        childTableName,
                        childTable,
                        foreignKey,
                        parent,
                        deletedRow[parent.ColumnIndex],
                        tableName,
                        postDeleteRows);
                }
            }
        }
    }

    private void ValidateChildrenAfterParentUpdate(
        QueryContext context,
        string tableName,
        IReadOnlyList<SqlValue[]> originalRows,
        IReadOnlyList<SqlValue[]> postUpdateRows,
        IReadOnlySet<int> assignedColumns,
        IReadOnlyList<int> updatedPositions)
    {
        foreach (var (childTableName, childTable) in context.Tables)
        {
            foreach (var foreignKey in childTable.ForeignKeys)
            {
                if (!string.Equals(foreignKey.ParentTable, tableName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var parent = ResolveForeignKeyParent(context.Tables, childTableName, foreignKey);
                if (!assignedColumns.Contains(parent.ColumnIndex))
                    continue;

                foreach (var position in updatedPositions)
                {
                    var oldValue = originalRows[position][parent.ColumnIndex];
                    var newValue = postUpdateRows[position][parent.ColumnIndex];
                    if (Compare(oldValue, newValue, parent.Collation) == 0)
                        continue;

                    ValidateChildrenReferencingParentValue(
                        context,
                        childTableName,
                        childTable,
                        foreignKey,
                        parent,
                        oldValue,
                        tableName,
                        postUpdateRows);
                }
            }
        }
    }

    private void ValidateChildForeignKeys(
        QueryContext context,
        string childTableName,
        EmbeddedTable childTable,
        IReadOnlyList<SqlValue[]> childRows,
        string targetTableName,
        IReadOnlyList<SqlValue[]> targetRows,
        ForeignKeyDefinition? onlyForeignKey = null)
    {
        foreach (var foreignKey in childTable.ForeignKeys)
        {
            if (onlyForeignKey is not null && !ReferenceEquals(foreignKey, onlyForeignKey))
                continue;

            if (!childTable.TryGetColumnIndex(foreignKey.ChildColumn, out var childColumn))
                throw ForeignKeyMismatch(childTableName, foreignKey.ParentTable);

            var parent = ResolveForeignKeyParent(context.Tables, childTableName, foreignKey);
            var parentRows = string.Equals(parent.TableName, targetTableName, StringComparison.OrdinalIgnoreCase)
                ? targetRows
                : parent.Table.Rows;
            foreach (var childRow in childRows)
            {
                var childValue = childRow[childColumn];
                if (childValue.Kind == SqlValueKind.Null)
                    continue;

                if (!ParentContains(parent, parentRows, childValue))
                    throw new EmbeddedSqlException("FOREIGN KEY constraint failed");
            }
        }
    }

    private void ValidateChildrenReferencingParentValue(
        QueryContext context,
        string childTableName,
        EmbeddedTable childTable,
        ForeignKeyDefinition foreignKey,
        ForeignKeyParent parent,
        SqlValue oldParentValue,
        string targetTableName,
        IReadOnlyList<SqlValue[]> targetRows)
    {
        if (!childTable.TryGetColumnIndex(foreignKey.ChildColumn, out var childColumn))
            throw ForeignKeyMismatch(childTableName, foreignKey.ParentTable);

        var childRows = string.Equals(childTableName, targetTableName, StringComparison.OrdinalIgnoreCase)
            ? targetRows
            : childTable.Rows;
        var parentRows = string.Equals(parent.TableName, targetTableName, StringComparison.OrdinalIgnoreCase)
            ? targetRows
            : parent.Table.Rows;
        foreach (var childRow in childRows)
        {
            var childValue = childRow[childColumn];
            if (childValue.Kind == SqlValueKind.Null
                || !ValuesMatchParent(parent, oldParentValue, childValue))
            {
                continue;
            }

            if (!ParentContains(parent, parentRows, childValue))
                throw new EmbeddedSqlException("FOREIGN KEY constraint failed");
        }
    }

    private ForeignKeyParent ResolveForeignKeyParent(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        string childTableName,
        ForeignKeyDefinition foreignKey)
    {
        if (!tables.TryGetValue(foreignKey.ParentTable, out var parent))
            throw new EmbeddedSqlException($"no such table: main.{foreignKey.ParentTable}");
        if (!parent.TryGetColumnIndex(foreignKey.ParentColumn, out var parentColumn))
            throw ForeignKeyMismatch(childTableName, foreignKey.ParentTable);

        var column = parent.ColumnDefinitions[parentColumn];
        if (column.IsGenerated)
            throw ForeignKeyMismatch(childTableName, foreignKey.ParentTable);

        if (parent.PrimaryKeyColumns.Count == 1 && parent.PrimaryKeyColumns[0].Index == parentColumn)
        {
            var collation = parent.TableLevelPrimaryKey?[0].Collation ?? column.Collation;
            if (IsBinaryCollation(collation))
                return new ForeignKeyParent(parent, foreignKey.ParentTable, parentColumn, collation);
        }

        if (column.Unique && IsBinaryCollation(column.Collation))
            return new ForeignKeyParent(parent, foreignKey.ParentTable, parentColumn, column.Collation);

        var uniqueIndex = parent.Indexes.FirstOrDefault(index =>
            index.Unique
            && index.Columns.Count == 1
            && index.Columns[0].ColumnIndex == parentColumn
            && IsBinaryCollation(index.Columns[0].Collation));
        if (uniqueIndex is not null)
        {
            return new ForeignKeyParent(
                parent,
                foreignKey.ParentTable,
                parentColumn,
                uniqueIndex.Columns[0].Collation);
        }

        throw ForeignKeyMismatch(childTableName, foreignKey.ParentTable);
    }

    private bool ParentContains(
        ForeignKeyParent parent,
        IReadOnlyList<SqlValue[]> parentRows,
        SqlValue childValue)
        => parentRows.Any(row => ValuesMatchParent(parent, row[parent.ColumnIndex], childValue));

    private bool ValuesMatchParent(ForeignKeyParent parent, SqlValue parentValue, SqlValue childValue)
    {
        var comparableChildValue = EmbeddedTable.ApplyColumnAffinity(
            parent.Table.ColumnDefinitions[parent.ColumnIndex],
            childValue);
        return Compare(parentValue, comparableChildValue, parent.Collation) == 0;
    }

    private static bool IsBinaryCollation(string? collation)
        => collation is null || string.Equals(collation, "BINARY", StringComparison.OrdinalIgnoreCase);

    private static bool IsStreamingSafeDistinctCollation(string? collation)
        => IsBinaryCollation(collation)
            || string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collation, "RTRIM", StringComparison.OrdinalIgnoreCase);

    private static EmbeddedSqlException ForeignKeyMismatch(string childTable, string parentTable)
        => new($"foreign key mismatch - \"{childTable}\" referencing \"{parentTable}\"");

    private sealed record ForeignKeyParent(
        EmbeddedTable Table,
        string TableName,
        int ColumnIndex,
        string? Collation);

    // Mutable per-statement UPDATE plan: the resolved column assignments and any rowid
    // (or rowid-alias) reassignment.
    private sealed class UpdatePlan
    {
        public required int AliasIndex { get; init; }

        public required List<(int Index, Expression Value)> ColumnAssignments { get; init; }

        public required Expression? RowidAssignment { get; init; }
    }

    // Coerces a value assigned to a rowid (or rowid-alias column) into an integer, applying
    // SQLite INTEGER affinity, and rejects anything that cannot be an integer rowid.
    private static long CoerceRowidOrThrow(SqlValue value)
        => EmbeddedTable.TryCoerceRowid(value, out var rowid)
            ? rowid
            : throw new EmbeddedSqlException("datatype mismatch");

    // Rejects duplicate rowids produced by an UPDATE, using the same qualified message
    // SQLite reports for a rowid-alias or hidden-rowid collision.
    private static void ValidateRowIdsUnique(string tableName, EmbeddedTable table, List<long> rowIds, int aliasIndex)
    {
        var seen = new HashSet<long>(rowIds.Count);
        foreach (var rowid in rowIds)
        {
            if (!seen.Add(rowid))
            {
                var column = aliasIndex >= 0 ? table.Columns[aliasIndex] : "rowid";
                var conflictAlgorithm = aliasIndex >= 0
                    ? table.RowidAliasConflictAlgorithm
                    : null;
                throw new EmbeddedSqlException(
                    $"UNIQUE constraint failed: {tableName}.{column}",
                    conflictAlgorithm);
            }
        }
    }

    // Column-level UNIQUE and PRIMARY KEY constraints are not represented in Indexes. They
    // still use the column's declared collation, so validate them with the same comparer as
    // explicit UNIQUE indexes rather than SqlValue's binary HashSet equality.
    private void ValidateColumnUniqueConstraints(EmbeddedTable table, IReadOnlyList<SqlValue[]> rows)
    {
        for (var columnIndex = 0; columnIndex < table.ColumnDefinitions.Length; columnIndex++)
        {
            if ((table.WithoutRowid || table.TableLevelPrimaryKey is not null)
                && table.IsPrimaryKeyColumn(columnIndex))
            {
                continue;
            }

            var column = table.ColumnDefinitions[columnIndex];
            if (!column.PrimaryKey)
                continue;

            var values = new List<SqlValue>();
            foreach (var row in rows)
            {
                var value = row[columnIndex];
                if (value.Kind == SqlValueKind.Null)
                    continue;

                if (values.Any(existing => Compare(existing, value, column.Collation) == 0))
                    throw new EmbeddedSqlException(
                        $"UNIQUE constraint failed: {table.Name}.{column.Name}",
                        column.PrimaryKeyConflictAlgorithm);

                values.Add(value);
            }
        }
    }

    private void ValidateUniqueIndexes(string tableName, EmbeddedTable table, IReadOnlyList<SqlValue[]> rows)
    {
        foreach (var index in table.Indexes)
        {
            if (index.Unique)
                ValidateUniqueIndex(tableName, index, rows);
        }
    }

    private void ValidateUniqueIndex(string tableName, EmbeddedIndex index, IReadOnlyList<SqlValue[]> rows)
    {
        var seenKeys = new List<SqlValue[]>();
        foreach (var row in rows)
        {
            var key = new SqlValue[index.Columns.Count];
            var hasNull = false;
            for (var column = 0; column < index.Columns.Count; column++)
            {
                key[column] = row[index.Columns[column].ColumnIndex];
                if (key[column].Kind == SqlValueKind.Null)
                {
                    hasNull = true;
                    break;
                }
            }

            // SQLite treats NULLs in a UNIQUE index as distinct, so such rows never conflict.
            if (hasNull)
                continue;

            foreach (var existing in seenKeys)
            {
                var conflict = true;
                for (var column = 0; column < index.Columns.Count; column++)
                {
                    if (Compare(existing[column], key[column], index.Columns[column].Collation) != 0)
                    {
                        conflict = false;
                        break;
                    }
                }

                if (conflict)
                {
                    var qualified = index.Columns.Select(column => $"{tableName}.{column.Name}");
                    throw new EmbeddedSqlException(
                        $"UNIQUE constraint failed: {string.Join(", ", qualified)}",
                        index.ConflictAlgorithm);
                }
            }

            seenKeys.Add(key);
        }
    }

    private ExecutionResult ExecuteDelete(
        DeleteStatement statement,
        SqlValue[] parameters,
        QueryContext context)
        => ExecuteWithTriggers(
            statement.TableName,
            TriggerEvent.Delete,
            context,
            () => PerformDelete(statement, parameters, context));

    private ExecutionResult PerformDelete(
        DeleteStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (CanCompileDml(context)
            && TryCompileDelete(statement, parameters, context, out var compiled, out var columns, out var hasReturning))
            return RunCompiledDml(compiled, columns, hasReturning, parameters);

        return PerformDeleteEvaluated(statement, parameters, context);
    }

    // Compiled DML reports only an aggregate affected-row count; live blob handles need
    // the evaluator's matched rowids to expire only when their own row is mutated. A cancelable
    // execution also stays evaluator-owned because the current VDBE loop has no cancellation opcode.
    private bool CanCompileDml(QueryContext context)
        => !context.CancellationToken.CanBeCanceled
            && !context.ForeignKeysEnabled
            && !HasOpenBlobHandles;

    private bool CanRouteInsertThroughCompiler(InsertStatement statement, QueryContext context)
        => CanCompileDml(context)
            && statement.ConflictAlgorithm is null
            && statement.Upsert is null
            && (!context.Tables.TryGetValue(statement.TableName, out var table)
                || !table.HasNonDefaultConflictAlgorithms);

    private bool CanRouteUpdateThroughCompiler(UpdateStatement statement, QueryContext context)
        => CanCompileDml(context)
            && (!context.Tables.TryGetValue(statement.TableName, out var table)
                || (table.PrimaryKeyColumns.Count == 0
                    && !table.Indexes.Any(index => index.Unique)
                    && !table.HasNonDefaultConflictAlgorithms));

    private ExecutionResult PerformDeleteEvaluated(
        DeleteStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        var selectedPositions = statement.Limit is null
            ? null
            : SelectLimitedDmlPositions(
                statement.TableName,
                table,
                statement.Where,
                statement.EffectiveOrderBy,
                statement.Limit,
                statement.Offset,
                [],
                statement.Returning,
                parameters,
                context);
        var rows = new List<SqlValue[]>(table.Rows.Count);
        var rowIds = new List<long>(table.Rows.Count);
        var deletedRows = new List<SqlValue[]>();
        var deletedRowIds = new List<long>();
        var rowsAffected = 0;
        for (var position = 0; position < table.Rows.Count; position++)
        {
            var row = table.Rows[position];
            var rowid = position < table.RowIds.Count ? table.RowIds[position] : position + 1;
            var source = new SourceRow(
                table.Columns,
                row,
                RowId: table.HasRowid ? rowid : null,
                RowIdQualifier: statement.TableName);
            var shouldDelete = selectedPositions is not null
                ? selectedPositions.Contains(position)
                : statement.Where is null || IsTrue(Evaluate(statement.Where, parameters, source, context));
            if (shouldDelete)
            {
                rowsAffected++;
                deletedRows.Add(row);
                deletedRowIds.Add(rowid);
                continue;
            }

            rows.Add(row);
            rowIds.Add(rowid);
        }

        if (rowsAffected > 0)
            ValidateForeignKeysAfterDelete(context, statement.TableName, table, table.Rows, rows, deletedRows);

        var returningResult = statement.Returning is null
            ? null
            : BuildReturningResult(
                statement.Returning,
                statement.TableName,
                table,
                deletedRows,
                deletedRowIds,
                rowsAffected,
                rowsAffected > 0,
                parameters,
                context);
        table.Rows.Clear();
        table.Rows.AddRange(rows);
        table.RowIds.Clear();
        table.RowIds.AddRange(rowIds);
        if (table.HasRowid)
        {
            foreach (var rowId in deletedRowIds)
                RecordBlobMutation(statement.TableName, rowId);
        }
        if (statement.Returning is not null)
            return returningResult!;

        return new ExecutionResult([], [], rowsAffected, rowsAffected > 0);
    }

    // Builds the result set produced by a RETURNING clause. Each projection is evaluated
    // against the affected row (new values for INSERT/UPDATE, old values for DELETE), the
    // same rows the statement committed, so RETURNING observes exactly what changed.
    private ExecutionResult BuildReturningResult(
        IReadOnlyList<Projection> returning,
        string tableName,
        EmbeddedTable table,
        IReadOnlyList<SqlValue[]> affectedRows,
        IReadOnlyList<long> affectedRowIds,
        int rowsAffected,
        bool changed,
        SqlValue[] parameters,
        QueryContext context,
        long? lastInsertRowId = null)
    {
        var outputColumns = BuildOutputColumns(tableName, table.Columns);

        foreach (var projection in returning)
        {
            if (projection.Expression is StarExpression or QualifiedStarExpression)
                continue;
            if (ContainsAggregate(projection.Expression) || ContainsWindowFunction(projection.Expression))
                throw new EmbeddedSqlException("aggregate and window functions are not allowed in RETURNING");
        }

        var columnNames = GetColumnNames(returning, outputColumns, outputColumns);
        var resultRows = new List<SqlValue[]>(affectedRows.Count);
        for (var rowIndex = 0; rowIndex < affectedRows.Count; rowIndex++)
        {
            var rowValues = affectedRows[rowIndex];
            var source = new SourceRow(
                table.Columns,
                rowValues,
                RowId: table.HasRowid && rowIndex < affectedRowIds.Count ? affectedRowIds[rowIndex] : null,
                RowIdQualifier: tableName);
            var output = new List<SqlValue>();
            foreach (var projection in returning)
            {
                switch (projection.Expression)
                {
                    case StarExpression:
                        for (var index = 0; index < table.Columns.Length; index++)
                            output.Add(rowValues[index]);
                        break;
                    case QualifiedStarExpression qualifiedStar:
                        if (!string.Equals(qualifiedStar.Qualifier, tableName, StringComparison.OrdinalIgnoreCase))
                            throw new EmbeddedSqlException($"no such table: {qualifiedStar.Qualifier}");
                        for (var index = 0; index < table.Columns.Length; index++)
                            output.Add(rowValues[index]);
                        break;
                    default:
                        output.Add(Evaluate(projection.Expression, parameters, source, context));
                        break;
                }
            }

            resultRows.Add(output.ToArray());
        }

        return new ExecutionResult(columnNames, resultRows, rowsAffected, changed)
        {
            LastInsertRowId = lastInsertRowId,
        };
    }

    // Runs a base INSERT/UPDATE/DELETE and, if the statement affects at least one row,
    // fires any statement-level AFTER triggers. The triggering statement plus every
    // trigger it fires form a single atomic unit: a failure anywhere restores the tables.
    private ExecutionResult ExecuteWithTriggers(
        string tableName,
        TriggerEvent triggerEvent,
        QueryContext context,
        Func<ExecutionResult> performBase)
    {
        var triggers = GetMatchingTriggers(context, tableName, triggerEvent);
        if (context.InsideTrigger && !context.RecursiveTriggersEnabled && context.ActiveTriggers is not null)
        {
            triggers = triggers
                .Where(trigger => !context.ActiveTriggers.Contains(trigger.Name))
                .ToArray();
        }
        if (context.RecursiveTriggersEnabled
            && context.InsideTrigger
            && context.ActiveTriggers is not null
            && triggers.Any(trigger => context.ActiveTriggers.Contains(trigger.Name)))
        {
            throw new EmbeddedSqlException("too many levels of trigger recursion");
        }
        if (triggers.Count == 0)
            return performBase();

        if (context.TriggerDepth >= MaximumTriggerDepth)
            throw new EmbeddedSqlException("too many levels of trigger recursion");

        var backup = CloneTables(context.Tables);
        try
        {
            var result = performBase();
            if (result.RowsAffected > 0)
                FireTriggers(triggers, context);

            return result;
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }
    }

    private void FireTriggers(IReadOnlyList<TriggerDefinition> triggers, QueryContext context)
    {
        // Trigger bodies are independently parsed statements, so the outer statement's CTE
        // scope is not visible to them.
        foreach (var trigger in triggers)
        {
            var activeTriggers = context.ActiveTriggers is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(context.ActiveTriggers, StringComparer.OrdinalIgnoreCase);
            activeTriggers.Add(trigger.Name);
            var triggerContext = context with
            {
                CommonTableExpressions = new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
                InsideTrigger = true,
                ActiveTriggers = activeTriggers,
                TriggerDepth = context.TriggerDepth + 1,
            };
            foreach (var bodyStatement in trigger.Body)
            {
                switch (bodyStatement)
                {
                    case InsertStatement insert:
                        ExecuteInsert(insert, EmptyParameters, triggerContext);
                        break;
                    case UpdateStatement update:
                        ExecuteUpdate(update, EmptyParameters, triggerContext);
                        break;
                    case DeleteStatement delete:
                        ExecuteDelete(delete, EmptyParameters, triggerContext);
                        break;
                    default:
                        throw new EmbeddedSqlException(
                            $"unsupported trigger body statement {bodyStatement.GetType().Name}");
                }
            }
        }
    }

    private static IReadOnlyList<TriggerDefinition> GetMatchingTriggers(
        QueryContext context,
        string tableName,
        TriggerEvent triggerEvent)
    {
        if (context.Triggers is null || context.Triggers.Count == 0)
            return [];

        return context.Triggers.Values
            .Where(trigger => trigger.Event == triggerEvent
                && string.Equals(trigger.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(trigger => trigger.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void RestoreTables(
        Dictionary<string, EmbeddedTable> target,
        Dictionary<string, EmbeddedTable> backup)
    {
        target.Clear();
        foreach (var pair in backup)
            target.Add(pair.Key, pair.Value.Clone());
    }

    // Trigger bodies never reference bind parameters (rejected at parse time); the slot-0
    // placeholder mirrors the 1-indexed parameter convention used elsewhere.
    private static readonly SqlValue[] EmptyParameters = new SqlValue[1];

    private ExecutionResult ExecuteQuery(
        QueryStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        return statement switch
        {
            SelectStatement select => ExecuteSelectStatement(select, parameters, context, outerRow),
            CompoundSelectStatement compound => ExecuteCompoundSelect(compound, parameters, context, outerRow),
            WithSelectStatement with => ExecuteWithSelect(with, parameters, context, outerRow),
            ValuesClause values => ExecuteValues(values, parameters, context, outerRow),
            _ => throw new EmbeddedSqlException($"Unsupported query type {statement.GetType().Name}."),
        };
    }

    private static SqlValue[] MaterializeQueryRow(IReadOnlyList<SqlValue> row)
    {
        var materialized = new SqlValue[row.Count];
        for (var index = 0; index < row.Count; index++)
            materialized[index] = row[index].WithoutJsonSubtype();

        return materialized;
    }

    private static ExecutionResult MaterializeQueryResult(ExecutionResult result)
    {
        if (!result.Rows.Any(row => row.Any(value => value.IsJson)))
            return result;

        return new ExecutionResult(
            result.Columns,
            result.Rows.Select(MaterializeQueryRow).ToArray(),
            result.RowsAffected,
            result.Changed)
        {
            LastInsertRowId = result.LastInsertRowId,
        };
    }

    // Routes a SELECT through the bytecode compiler when its source and expression
    // shapes are representable, running the emitted program as a real execution path.
    // Deliberately unsupported semantic families keep the tree-walking evaluator.
    private ExecutionResult ExecuteSelectStatement(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!context.CancellationToken.CanBeCanceled
            && TryCompileSelect(select, parameters, context, outerRow, out var compiled))
        {
            var columns = GetColumnNames(
                select.Projections,
                GetOutputColumns(select.Source, context),
                GetRawOutputColumns(select.Source, context));
            return RunCompiledProgram(
                compiled,
                columns,
                BuildValuesBinding(compiled.ParameterIndices ?? [], parameters));
        }

        return ExecuteSelect(select, parameters, context, outerRow);
    }

    private bool TryCompileSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        // A SELECT carrying LIMIT/OFFSET lowers only when its LIMIT/OFFSET-free base is a
        // gate-able route. Direct scans, constant projections, source-less scalar Function programs,
        // aggregates, the deliberately narrow bounded sorted-scan subset, and a strictly-gated
        // equi-join subset route through the dedicated path that layers LimitOffsetProgramBuilder
        // gates onto that base. DISTINCT, computed shapes, outer joins, and compounds keep
        // LIMIT/OFFSET on the evaluator.
        if (select.Limit is not null || select.Offset is not null)
            return TryCompileLimitedSelect(select, parameters, context, outerRow, out compiled);

        // The direct scan / source-less constant projection subset.
        if (TryCompileScanOrConstant(select, parameters, context, outerRow, out compiled))
            return true;

        // The unordered scan/constant compiler declines aggregation. Try the aggregate
        // route so whole-table and GROUP BY aggregations over a single base table lower to
        // the real AggReset/AggStep/AggFinalize opcode family instead of the evaluator,
        // reusing the evaluator's accumulation and grouping helpers so results stay exact.
        if (TryCompileAggregateSelect(select, parameters, context, outerRow, out compiled))
            return true;

        // The aggregate route declines window calls (ContainsAggregate is false for them). Try the
        // running-frame window route so a single-table SELECT whose window functions all share one
        // ROWS UNBOUNDED PRECEDING -> CURRENT ROW frame lowers to the real sorter + AggReset/AggStep/
        // AggFinalize opcode family, reusing the evaluator's accumulation, partition equality, and
        // ordering so routed rows stay byte-identical; every other window shape stays on the evaluator.
        if (TryCompileWindowSelect(select, parameters, context, outerRow, out compiled))
            return true;

        // The unordered scan/constant compiler declines ORDER BY. Try the sorter-backed
        // route so single-table ordered scans also lower to real bytecode instead of the
        // evaluator, keeping ordering semantics identical by reusing the evaluator's
        // comparison logic through the emitted VdbeRowComparer.
        if (TryCompileSortedSelect(select, parameters, context, outerRow, out compiled))
            return true;

        // The single-table routes all decline a join source. Try the nested-loop join route
        // so a two-table INNER/LEFT OUTER join over base tables lowers to the real
        // OpenReadCursor/Rewind/Column/FilterRegisters/JumpIf/ResultRow opcode family instead
        // of the evaluator, delegating the ON/WHERE predicate to the evaluator's own
        // comparison semantics so routed rows stay byte-identical.
        return TryCompileJoinSelect(
            select,
            parameters,
            context,
            outerRow,
            allowPostJoinWhere: false,
            compiled: out compiled);
    }

    // Generic source-less and direct-scan projections, delegated to the shared
    // SelectStatementCompiler. Constant folding, scalar functions, numeric affinity,
    // table resolution, and predicate compilation reuse evaluator helpers so the emitted
    // program matches evaluator value semantics.
    private bool TryCompileScanOrConstant(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        var compiler = new SelectStatementCompiler(
            IsConstantScalarExpression,
            expression => Evaluate(expression, parameters, null, context),
            source => ResolveScanTarget(source, context),
            (where, target) => IsStreamingSafeScalarScanPredicate(where, target, context)
                ? CompileRowPredicate(where, target, parameters, context, outerRow)
                : null,
            (where, target) => CompileSimpleRowIdPredicate(where, target, parameters, context, outerRow),
            (select, target) => CompileDistinctScanEquality(select, target, context),
            function => TryGetRoutableBuiltinScalarCall(function, out var routable)
                ? BuildBuiltinScalarFunction(routable, parameters, context)
                : null,
            ArithmeticNumericAffinity,
            ModuloNumericAffinity);
        return compiler.TryCompile(select, out compiled);
    }

    // DISTINCT runs as rows stream from the cursor, whereas the evaluator first materializes every filtered
    // source row before it evaluates projections and de-duplicates. Restrict the direct route to direct declared
    // columns with built-in declared collations plus an optional single declared-column comparison against a literal
    // or parameter: neither stage can introduce a deferred expression failure after an earlier DISTINCT result has
    // been yielded. Custom collations remain evaluator-owned because their callbacks can throw while de-duplicating.
    // More complex predicates also remain evaluator-owned so their value and error timing are preserved.
    private VdbeRowEquality? CompileDistinctScanEquality(
        SelectStatement select,
        ScanTarget target,
        QueryContext context)
    {
        if (!select.Distinct
            || select.Projections.Count == 0
            || (select.Where is not null && !IsExactDistinctScanWherePredicate(select.Where, target)))
            return null;

        var table = context.Tables[target.TableName];
        var collations = new List<string?>(select.Projections.Count);
        foreach (var projection in select.Projections)
        {
            if (projection.Expression is not ColumnExpression column)
            {
                return null;
            }

            var index = target.ResolveColumnIndex(column.Name);
            if (index is null)
                return null;

            var collation = table.ColumnDefinitions[index.Value].Collation;
            if (!IsStreamingSafeDistinctCollation(collation))
                return null;

            collations.Add(collation);
        }

        return (left, right) => RowsEqual(left, right, collations);
    }

    private static bool IsExactDistinctScanWherePredicate(Expression expression, ScanTarget target)
    {
        if (expression is not BinaryExpression binary || !IsComparisonOperator(binary.Operator))
            return false;

        return (IsDeclaredScanColumnReference(binary.Left, target) && IsLiteralOrParameter(binary.Right))
            || (IsDeclaredScanColumnReference(binary.Right, target) && IsLiteralOrParameter(binary.Left));
    }

    private static bool IsDeclaredScanColumnReference(Expression expression, ScanTarget target)
        => expression is ColumnExpression column && target.ResolveColumnIndex(column.Name) is not null;

    // Builtin scalar functions whose evaluator implementation reads ONLY the already-evaluated argument
    // values — never the argument AST, the current row, the collation, or the query context — so applying
    // the function through the direct Function opcode over the same argument values is byte-identical to the
    // evaluator. Names are compared upper-cased (the evaluator's own normalization). Deliberately excluded:
    // NULLIF/MIN/MAX (collation/comparison via the argument AST), LIKE/GLOB (collation/pattern), all JSON_*
    // (read the argument AST for error text), LAST_INSERT_ROWID (reads the context), and the date/time and
    // UUID families (non-deterministic). See TryGetRoutableBuiltinScalarCall for the per-call gates.
    private static readonly HashSet<string> RoutableBuiltinScalarFunctions = new(StringComparer.Ordinal)
    {
        "ABS",
        "COALESCE",
        "HEX",
        "IFNULL",
        "INSTR",
        "LENGTH",
        "LOWER",
        "TYPEOF",
        "UPPER",
    };

    private static readonly VdbeNumericAffinity ArithmeticNumericAffinity = new()
    {
        Name = "numeric",
        Apply = value => value.Kind == SqlValueKind.Null ? value : ApplyNumericAffinity(value),
    };

    private static readonly VdbeNumericAffinity ModuloNumericAffinity = new()
    {
        Name = "integer-numeric",
        Apply = value => value.Kind == SqlValueKind.Null ? value : ApplyModuloNumericAffinity(value),
    };

    private static bool IsStreamingSafeScalarScanPredicate(
        Expression expression,
        ScanTarget target,
        QueryContext context)
    {
        if (expression is not BinaryExpression binary
            || !IsComparisonOperator(binary.Operator)
            || !context.Tables.TryGetValue(target.TableName, out var table))
        {
            return false;
        }

        return (IsStreamingSafeScalarScanColumn(binary.Left, target, table) && IsLiteralOrParameter(binary.Right))
            || (IsStreamingSafeScalarScanColumn(binary.Right, target, table) && IsLiteralOrParameter(binary.Left));
    }

    private static bool IsStreamingSafeScalarScanColumn(
        Expression expression,
        ScanTarget target,
        EmbeddedTable table)
    {
        return expression switch
        {
            ColumnExpression column when target.ResolveColumnIndex(column.Name) is { } ordinal
                => IsStreamingSafeDistinctCollation(table.ColumnDefinitions[ordinal].Collation),
            CollationExpression
            {
                Expression: ColumnExpression column,
                Name: var collation,
            } when target.ResolveColumnIndex(column.Name) is not null
                => IsStreamingSafeDistinctCollation(collation),
            _ => false,
        };
    }

    // Recognizes a plain builtin scalar call eligible for the Function opcode: an allow-listed name, no
    // OVER/FILTER/DISTINCT/COUNT(*) decoration, and no user-defined function registered under the name (for
    // this arity or variadically) that would shadow the builtin in the evaluator's own dispatch.
    private bool TryGetRoutableBuiltinScalarCall(Expression expression, out FunctionExpression function)
    {
        function = null!;
        if (expression is not FunctionExpression candidate)
            return false;

        if (candidate.Window is not null
            || candidate.Filter is not null
            || candidate.Distinct
            || candidate.CountStar)
        {
            return false;
        }

        var name = candidate.Name.ToUpperInvariant();
        if (!RoutableBuiltinScalarFunctions.Contains(name))
            return false;

        if (_scalarFunctions.ContainsKey((name, candidate.Arguments.Count))
            || _scalarFunctions.ContainsKey((name, -1)))
        {
            return false;
        }

        function = candidate;
        return true;
    }

    // Maps the arithmetic BinaryOperators to their ArithmeticOperator opcode. Only the numeric family the
    // Arithmetic opcode implements is routable; Concatenate, the comparison operators, And/Or, and Is/IsNot
    // carry text/comparison/collation/logical semantics VdbeArithmetic does not model, so they decline.
    private static bool TryMapArithmeticOperator(BinaryOperator op, out ArithmeticOperator arithmetic)
    {
        switch (op)
        {
            case BinaryOperator.Add:
                arithmetic = ArithmeticOperator.Add;
                return true;
            case BinaryOperator.Subtract:
                arithmetic = ArithmeticOperator.Subtract;
                return true;
            case BinaryOperator.Multiply:
                arithmetic = ArithmeticOperator.Multiply;
                return true;
            case BinaryOperator.Divide:
                arithmetic = ArithmeticOperator.Divide;
                return true;
            case BinaryOperator.Modulo:
                arithmetic = ArithmeticOperator.Modulo;
                return true;
            default:
                arithmetic = default;
                return false;
        }
    }

    // Wraps the evaluator's own EvaluateScalarFunction as a Function-opcode delegate: at execution the opcode
    // hands over the already-computed argument values, which are re-wrapped as literals and dispatched through
    // the exact same builtin switch — so values, NULL propagation, and thrown EmbeddedSqlExceptions (including
    // arity errors, raised here rather than by the builder) are byte-identical to the evaluator. Arity is left
    // null so the builder never pre-validates the count and the evaluator owns arity checking as it always does.
    private VdbeScalarFunction BuildBuiltinScalarFunction(
        FunctionExpression function,
        SqlValue[] parameters,
        QueryContext context)
    {
        var name = function.Name;
        return new VdbeScalarFunction
        {
            Name = name.ToLowerInvariant(),
            Arity = null,
            Invoke = arguments =>
            {
                var literalArguments = new Expression[arguments.Length];
                for (var index = 0; index < arguments.Length; index++)
                    literalArguments[index] = new LiteralExpression(arguments[index]);

                return EvaluateScalarFunction(
                    new FunctionExpression(name, literalArguments, CountStar: false),
                    parameters,
                    row: null,
                    context);
            },
        };
    }

    // Lowers a LIMIT/OFFSET SELECT by compiling its LIMIT/OFFSET-free base and layering the
    // LimitOffsetProgramBuilder gates onto it, or returns false so the evaluator keeps
    // ownership of every shape below.
    //
    // Routed entirely through the VDBE:
    //  - the base (the same SELECT with LIMIT/OFFSET stripped) lowers via the direct-scan /
    //    constant-projection compiler, aggregate route, bounded sorted-scan route, or the
    //    deliberately small direct join route. Each emits through unconditional ResultRow
    //    instructions, which is exactly what the gate can bound: OFFSET skips leading candidates
    //    without charging LIMIT, then LIMIT caps the survivors, matching the evaluator's
    //    OFFSET-then-LIMIT ApplyDistinctLimit order.
    //  - LIMIT/OFFSET resolve to integers exactly as ExecuteSelect resolves them
    //    (RequireLimitInteger, with a negative OFFSET clamped to zero and a null/negative
    //    LIMIT treated as unbounded), so the gated program yields the identical row window.
    //
    // Deliberately kept on the evaluator (fallback):
    //  - ORDER BY outside the bounded sorted-scan subset; joins outside the narrow direct
    //    equi-join or cross-join shape; DISTINCT, aggregate/window shapes, non-base sources, computed
    //    projections, and non-column order keys all need semantics this pipeline does not
    //    represent exactly.
    //  - DISTINCT: the base carries DISTINCT, which every gate-able route rejects, so the
    //    evaluator applies de-duplication before trimming.
    //  - LIMIT 0: the evaluator validates every projection/WHERE/GROUP BY/HAVING/ORDER BY
    //    expression against a synthetic row and returns empty WITHOUT scanning, so its
    //    validation-and-evaluation timing differs from a gate that would scan first; keep it
    //    on the evaluator so that timing is preserved.
    //  - a LIMIT/OFFSET expression that does not resolve to an integer (e.g. LIMIT 'x' or
    //    LIMIT NULL -> "datatype mismatch"): fall back so the evaluator raises the identical
    //    error at its identical (pre-scan) point rather than the router surfacing it.
    private bool TryCompileLimitedSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        compiled = null!;

        // DISTINCT de-duplicates before applying the row window. Its direct scan emits
        // DistinctResultRow, which LimitOffsetProgramBuilder deliberately cannot gate.
        if (select.Distinct)
            return false;

        // ORDER BY needs a sorter before row gates run. Its bounded subset has an explicit
        // preflight below; every other ordered shape remains evaluator-owned.
        if (select.OrderBy.Count != 0)
            return TryCompileLimitedSortedSelect(select, parameters, context, outerRow, out compiled);

        // Resolve bounds before compiling the base. This keeps a bad bound's error ahead of
        // any projection folding or source work, as in ExecuteSelect.
        if (!TryResolveLimitOffset(select, parameters, context, outerRow, out var limit, out var offset))
            return false;

        // LIMIT 0 has evaluator-specific validate-and-skip-the-scan semantics the gate cannot
        // reproduce; keep it on the evaluator (which also returns empty, but only after its
        // LIMIT 0 expression validation).
        if (limit == 0)
            return false;

        // Gate only the row-count-preserving routes whose unconditional ResultRows the builder
        // can bound exactly. Any generic source-less expression is safe because it has one
        // candidate row; scan projections remain direct-only so an early gate cannot change
        // expression error timing. Bounded joins claim only direct INNER/LEFT equi-joins or
        // INNER cross joins over two base tables with direct projections.
        var baseSelect = select with { Limit = null, Offset = null };
        var directProjectionIsGateSafe = baseSelect.Source is null
            || baseSelect.Projections.All(projection =>
                projection.Expression is StarExpression
                    or QualifiedStarExpression
                    or ColumnExpression
                || IsConstantScalarExpression(projection.Expression));
        if (!(directProjectionIsGateSafe
                && TryCompileScanOrConstant(baseSelect, parameters, context, outerRow, out var compiledBase))
            && !TryCompileAggregateSelect(baseSelect, parameters, context, outerRow, out compiledBase)
            && !TryCompileLimitedJoinSelect(baseSelect, parameters, context, outerRow, out compiledBase))
        {
            return false;
        }

        // The gate returns the program unchanged when neither bound is needed (offset <= 0 and
        // an unbounded limit), so an unbounded LIMIT -1 still routes as the plain base scan.
        var gated = LimitOffsetProgramBuilder.Apply(compiledBase.Program, offset, limit);
        compiled = ReferenceEquals(gated, compiledBase.Program)
            ? compiledBase
            : new CompiledSelect(gated, compiledBase.CursorSources, compiledBase.ParameterIndices);
        return true;
    }

    // Bounded joins are deliberately a smaller contract than TryCompileJoinSelect. A LIMIT/OFFSET gate is
    // mechanically valid over every unconditional ResultRow that JoinProgramBuilder emits, including the
    // mutually-exclusive matched and null-extension emission sites of a LEFT join. This route claims two base
    // tables, direct column/literal projections, and either an explicit equality between one direct column from
    // each side or an INNER cross join with no ON condition. INNER joins may add a direct post-join WHERE.
    // LEFT joins may add only a direct, collation-free comparison over the combined input row, which the
    // nested-loop program applies after null extension.
    private bool TryCompileLimitedJoinSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        compiled = null!;

        if (select.Source is not JoinTableSource
            {
                Natural: false,
                UsingColumns: null,
            } join
            || join.Kind is not (JoinKind.Inner or JoinKind.Left))
        {
            return false;
        }

        var leftTarget = ResolveScanTarget(join.Left, context);
        var rightTarget = ResolveScanTarget(join.Right, context);
        if (leftTarget is null
            || rightTarget is null
            || select.Projections.Any(projection =>
                !IsDirectJoinProjection(projection.Expression, leftTarget, rightTarget))
            || (select.Where is not null
                && (join.Kind == JoinKind.Left
                    ? !IsExactLeftOuterJoinWherePredicate(select.Where, leftTarget, rightTarget)
                    : !IsDirectJoinWherePredicate(select.Where, leftTarget, rightTarget))))
        {
            return false;
        }

        // A conditionless INNER join is the parser representation of both CROSS JOIN and the
        // comma operator. The reusable nested-loop builder emits its pair stream without a
        // predicate, so applying a direct WHERE before its unconditional ResultRow has the same
        // pair order and LIMIT/OFFSET window as the evaluator. A conditionless LEFT join has no
        // useful SQL spelling in this route; keep it on the evaluator rather than broadening the
        // supported surface accidentally.
        if (join.Condition is null)
        {
            if (join.Kind != JoinKind.Inner)
                return false;
        }
        else if (!IsDirectEquiJoinEquality(join.Condition, leftTarget, rightTarget))
        {
            return false;
        }

        return TryCompileJoinSelect(
            select,
            parameters,
            context,
            outerRow,
            allowPostJoinWhere: join.Kind == JoinKind.Left,
            compiled: out compiled);
    }

    // The ON comparison must use one direct column from each input. Requiring a qualifier keeps duplicate
    // names and self-joins from accidentally binding to the first matching combined-row column.
    private static bool IsDirectEquiJoinEquality(
        Expression expression,
        ScanTarget leftTarget,
        ScanTarget rightTarget)
    {
        if (expression is not BinaryExpression { Operator: BinaryOperator.Equal } equality)
            return false;

        return IsDirectColumnFrom(equality.Left, leftTarget, rightTarget)
               && IsDirectColumnFrom(equality.Right, rightTarget, leftTarget)
            || IsDirectColumnFrom(equality.Left, rightTarget, leftTarget)
               && IsDirectColumnFrom(equality.Right, leftTarget, rightTarget);
    }

    private static bool IsDirectJoinProjection(
        Expression expression,
        ScanTarget leftTarget,
        ScanTarget rightTarget)
        => expression is LiteralExpression
            || IsDirectColumnFrom(expression, leftTarget, rightTarget)
            || IsDirectColumnFrom(expression, rightTarget, leftTarget);

    private static bool IsDirectJoinWherePredicate(
        Expression expression,
        ScanTarget leftTarget,
        ScanTarget rightTarget)
    {
        if (expression is not BinaryExpression comparison
            || comparison.Operator is not (BinaryOperator.Equal
                or BinaryOperator.NotEqual
                or BinaryOperator.LessThan
                or BinaryOperator.LessThanOrEqual
                or BinaryOperator.GreaterThan
                or BinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        return IsDirectJoinPredicateOperand(comparison.Left, leftTarget, rightTarget)
            && IsDirectJoinPredicateOperand(comparison.Right, leftTarget, rightTarget)
            && (IsDirectColumnFrom(comparison.Left, leftTarget, rightTarget)
                || IsDirectColumnFrom(comparison.Left, rightTarget, leftTarget)
                || IsDirectColumnFrom(comparison.Right, leftTarget, rightTarget)
                || IsDirectColumnFrom(comparison.Right, rightTarget, leftTarget));
    }

    private static bool IsDirectJoinPredicateOperand(
        Expression expression,
        ScanTarget leftTarget,
        ScanTarget rightTarget)
        => IsDirectColumnFrom(expression, leftTarget, rightTarget)
            || IsDirectColumnFrom(expression, rightTarget, leftTarget)
            || UnwrapCollation(expression) is LiteralExpression or ParameterExpression;

    // An explicit WHERE collation can raise a late "no such collation sequence" error. The evaluator
    // materializes the complete join before it evaluates WHERE, while the VDBE checks per row, so retain
    // those shapes on the evaluator. Direct comparison/IS predicates otherwise cannot fail after parameter
    // binding and use the evaluator itself for all SQL NULL truth semantics.
    private static bool IsExactLeftOuterJoinWherePredicate(
        Expression expression,
        ScanTarget leftTarget,
        ScanTarget rightTarget)
    {
        if (ContainsExplicitCollation(expression))
            return false;

        if (expression is BinaryExpression { Operator: BinaryOperator.Is or BinaryOperator.IsNot } comparison)
        {
            return IsDirectJoinPredicateOperand(comparison.Left, leftTarget, rightTarget)
                && IsDirectJoinPredicateOperand(comparison.Right, leftTarget, rightTarget)
                && (IsDirectColumnFrom(comparison.Left, leftTarget, rightTarget)
                    || IsDirectColumnFrom(comparison.Left, rightTarget, leftTarget)
                    || IsDirectColumnFrom(comparison.Right, leftTarget, rightTarget)
                    || IsDirectColumnFrom(comparison.Right, rightTarget, leftTarget));
        }

        return IsDirectJoinWherePredicate(expression, leftTarget, rightTarget);
    }

    private static bool ContainsExplicitCollation(Expression expression)
    {
        return expression switch
        {
            CollationExpression => true,
            BinaryExpression binary => ContainsExplicitCollation(binary.Left)
                || ContainsExplicitCollation(binary.Right),
            _ => false,
        };
    }

    private static bool IsDirectColumnFrom(
        Expression expression,
        ScanTarget target,
        ScanTarget otherTarget)
    {
        if (UnwrapCollation(expression) is not ColumnExpression { Name: var name }
            || !name.Contains('.'))
        {
            return false;
        }

        return target.ResolveColumnIndex(name) is not null
            && otherTarget.ResolveColumnIndex(name) is null;
    }

    private static Expression UnwrapCollation(Expression expression)
    {
        while (expression is CollationExpression collation)
            expression = collation.Expression;

        return expression;
    }

    // Lowers the intentionally small ORDER BY + LIMIT/OFFSET family. The base sorter consumes
    // all qualifying rows before its unconditional ResultRow reaches the offset/limit gates, so
    // the gates trim the already ordered stream exactly. The preflight is deliberately stricter
    // than the unbounded sorter route: only a single base table, non-DISTINCT non-aggregate
    // projections made from bare columns, "*" / a resolved qualified star, or literals, and
    // resolved column (or COLLATE-column) ORDER BY keys are admitted. This prevents
    // aliases/ordinals that resolve to computed expressions from being mistaken for row-backed sort keys.
    private bool TryCompileLimitedSortedSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        compiled = null!;

        // ExecuteSelect validates collations before it resolves LIMIT/OFFSET. Do that before
        // any lowering work so a missing collation keeps its evaluator error precedence.
        ValidateOrderByCollations(ResolveOrderBy(select.OrderBy, select.Projections));
        if (!TryResolveLimitOffset(select, parameters, context, outerRow, out var limit, out var offset))
            return false;

        // LIMIT 0 validates all expressions but deliberately avoids scanning. A sorter would
        // materialize rows before its zero gate, so retain evaluator ownership.
        if (limit == 0)
            return false;

        var baseSelect = select with { Limit = null, Offset = null };
        if (!TryCompileBoundedSortedSelect(baseSelect, parameters, context, outerRow, out var compiledBase))
            return false;

        var gated = LimitOffsetProgramBuilder.Apply(compiledBase.Program, offset, limit);
        compiled = ReferenceEquals(gated, compiledBase.Program)
            ? compiledBase
            : new CompiledSelect(gated, compiledBase.CursorSources);
        return true;
    }

    private bool TryResolveLimitOffset(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out long? limit,
        out long offset)
    {
        limit = null;
        offset = 0;
        try
        {
            limit = select.Limit is null
                ? null
                : RequireLimitInteger(Evaluate(select.Limit, parameters, outerRow, context));
            offset = select.Offset is null
                ? 0
                : Math.Max(0, RequireLimitInteger(Evaluate(select.Offset, parameters, outerRow, context)));
            return true;
        }
        catch (EmbeddedSqlException)
        {
            // The evaluator owns diagnostics for non-integral bounds and any expression
            // failure, preserving its exact error text and timing.
            return false;
        }
    }

    // Lowers a compound SELECT whose terms are all lowerable SELECTs sequenced by a single uniform
    // UNION ALL, UNION/DISTINCT, INTERSECT, or EXCEPT operator into one VdbeProgram via
    // CompoundProgramBuilder, or returns false so the tree-walking evaluator keeps ownership of every
    // shape below.
    //
    // Routed entirely through the VDBE:
    //  - the operator chain is a single operator repeated (a same-operator chain), and that operator
    //    is UNION ALL, UNION/DISTINCT, INTERSECT, or EXCEPT. Such a chain flattens into one builder
    //    call because the evaluator folds it left-associatively into the identical result:
    //      * UNION ALL concatenates every term in order.
    //      * UNION/DISTINCT keeps each row's first occurrence across all terms in arrival order.
    //      * INTERSECT emits each distinct first-term row that also appears in every other term, in
    //        first-term first-occurrence order. A ∩ B ∩ C is associative and commutative, so the
    //        builder's "present in all probe sets" test reproduces the evaluator's step-by-step fold
    //        (each step intersects the running distinct set with the next term) exactly.
    //      * EXCEPT emits each distinct first-term row absent from every other term, in first-term
    //        first-occurrence order. A EXCEPT B EXCEPT C is left-associative and equals A minus
    //        (B ∪ C), so the builder's "absent from all probe sets" test reproduces the evaluator's
    //        fold exactly.
    //    These are precisely what BuildUnionAll/BuildUnionDistinct/BuildIntersect/BuildExcept emit,
    //    so flattening is order-, duplicate-, and membership-identical.
    //  - every term is a SELECT that TryCompileSelect already lowers (constant projection, table
    //    scan, aggregate, sorted scan, or join), and every term projects the same number of result
    //    columns. Because every lowering route declines DISTINCT, no term carries its own row sets,
    //    which is what the set-operation builders require.
    //  - for UNION/DISTINCT, INTERSECT, and EXCEPT (every operator that de-duplicates or probes rows),
    //    the first term's projection list maps one-to-one onto the output columns so the evaluator's
    //    per-output-column collation vector aligns with the row width; a star-expanded first term
    //    (whose projection count differs from the column count) stays on the evaluator, whose own
    //    row-equality would fault on that same too-short vector. UNION ALL needs no collation vector,
    //    so a star-expanded UNION ALL still routes.
    //
    // Deliberately kept on the evaluator (fallback):
    //  - ORDER BY / LIMIT / OFFSET on the compound, which reshape the sequenced stream the builder
    //    only concatenates, de-duplicates, or probes.
    //  - mixed operators (e.g. UNION ALL ... UNION ..., or INTERSECT ... EXCEPT ...); the builder
    //    sequences one uniform operator per call and the evaluator's left-associative mixed fold is
    //    not reproduced here.
    //  - any term that is not a SELECT (VALUES, a nested compound) or that TryCompileSelect declines
    //    (CTE/view/derived sources it cannot scan, DISTINCT terms, subquery WHEREs, ...).
    //  - terms whose result-column counts differ, so the evaluator raises its exact "SELECTs to the
    //    left and right of a compound operator do not have the same number of result columns" error.
    private bool TryCompileCompoundSelect(
        CompoundSelectStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        compiled = null!;

        if (statement.Operators.Count == 0
            || statement.OrderBy.Count != 0
            || statement.Limit is not null
            || statement.Offset is not null)
        {
            return false;
        }

        // Only a single uniform UNION ALL, UNION/DISTINCT, INTERSECT, or EXCEPT operator repeated
        // across the whole chain routes; any mixed chain stays on the evaluator.
        var compoundOperator = statement.Operators[0];
        if (compoundOperator is not (CompoundOperator.Union or CompoundOperator.UnionAll
            or CompoundOperator.Intersect or CompoundOperator.Except))
        {
            return false;
        }

        for (var index = 1; index < statement.Operators.Count; index++)
        {
            if (statement.Operators[index] != compoundOperator)
                return false;
        }

        // Every term must be a SELECT that lowers on its own and projects the same number of result
        // columns; otherwise fall back so the evaluator produces the value or its exact error.
        var terms = new List<CompoundTerm>(statement.Terms.Count);
        var columnCount = -1;
        foreach (var term in statement.Terms)
        {
            if (term is not SelectStatement { Distinct: false } select
                || !TryCompileSelect(select, parameters, context, outerRow, out var compiledTerm)
                || compiledTerm.ParameterIndices is { Count: > 0 })
            {
                return false;
            }

            var width = ResultRowWidth(compiledTerm.Program);
            if (columnCount < 0)
                columnCount = width;
            else if (columnCount != width)
                return false;

            terms.Add(new CompoundTerm(compiledTerm.Program, compiledTerm.CursorSources));
        }

        if (compoundOperator is CompoundOperator.Intersect or CompoundOperator.Except
            && terms.Any(term => !IsReorderSafeSetOperationTerm(term.Program)))
        {
            return false;
        }

        CompoundTerm compound;
        if (compoundOperator == CompoundOperator.UnionAll)
        {
            compound = CompoundProgramBuilder.BuildUnionAll(terms);
        }
        else
        {
            // UNION/DISTINCT, INTERSECT, and EXCEPT all de-duplicate their output and (for the set
            // operations) probe membership through the evaluator's row-equality: per-output-column
            // collations from the first term drive RowsEqual (NULL==NULL, otherwise a collation-aware
            // comparison), so the emitted DistinctResultRow / RowSetInsert / CompoundResultRow opcodes
            // de-duplicate and test membership exactly as ApplyUnion / ApplyIntersect / ApplyExcept do.
            // The collation vector only aligns with the row width when the first term projects one
            // column per output, so a star-expanded first term declines rather than index a too-short
            // vector (the same range the evaluator's own RowsEqual would fault on).
            var collations = GetCompoundCollations(statement.Terms[0], columnCount);
            if (collations.Count != columnCount
                || collations.Any(collation => !IsStreamingSafeDistinctCollation(collation)))
                return false;

            bool RowEquality(SqlValue[] left, SqlValue[] right) => RowsEqual(left, right, collations);

            compound = compoundOperator switch
            {
                CompoundOperator.Union => CompoundProgramBuilder.BuildUnionDistinct(terms, RowEquality),
                CompoundOperator.Intersect => CompoundProgramBuilder.BuildIntersect(terms, RowEquality),
                CompoundOperator.Except => CompoundProgramBuilder.BuildExcept(terms, RowEquality),
                _ => throw new EmbeddedSqlException($"Unsupported compound operator {compoundOperator}."),
            };
        }

        compiled = new CompiledSelect(compound.Program, compound.CursorSources);
        return true;
    }

    // INTERSECT/EXCEPT currently build probe terms before the primary term. Only programs whose
    // execution cannot raise or invoke user code may be reordered this way; computed arithmetic,
    // functions, aggregates, joins, and sorters remain on the evaluator, which evaluates terms
    // left-to-right.
    private static bool IsReorderSafeSetOperationTerm(VdbeProgram program)
        => program.Instructions.All(instruction => instruction is
            LoadConstantInstruction
            or LoadParameterInstruction
            or CopyInstruction
            or OpenReadCursorInstruction
            or CloseCursorInstruction
            or RewindCursorInstruction
            or ColumnInstruction
            or RowIdInstruction
            or FilterInstruction
            or FilterRowIdInstruction
            or NextInstruction
            or ResultRowInstruction
            or HaltInstruction);

    // The number of result columns a compiled term projects, read from its first result-row emission.
    // Every lowered SELECT emits one, and CompoundProgramBuilder validates the widths agree, so this
    // mirrors the builder's own width check to keep the routing decision and the builder in lock-step.
    private static int ResultRowWidth(VdbeProgram program)
    {
        foreach (var instruction in program.Instructions)
        {
            switch (instruction)
            {
                case ResultRowInstruction result:
                    return result.Values.Count;
                case DistinctResultRowInstruction distinct:
                    return distinct.Values.Count;
            }
        }

        throw new EmbeddedSqlException("A compiled SELECT term produced no result row.");
    }

    // Lowers a single base-table ORDER BY pipeline into a sorter-backed VdbeProgram, or
    // returns false so the evaluator keeps ownership of shapes the sorted route cannot
    // preserve exactly. Supported: a single real base table, one or more projections that
    // are bare columns, "*" / a resolved qualified star, or folded constants, an optional
    // row-at-a-time WHERE, and one or more ORDER BY keys evaluable against the scanned row.
    // LIMIT/OFFSET are accepted only through TryCompileLimitedSortedSelect, which strips the
    // bounds and asks the bounded overload below for its stricter column-key subset.
    // Deliberately excluded (kept on the evaluator): DISTINCT, GROUP BY/HAVING, an unresolved
    // qualified star, and any ORDER BY key that needs a subquery/aggregate/window or an
    // unbacked rowid.
    private bool TryCompileSortedSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
        => TryCompileSortedSelect(select, parameters, context, outerRow, bounded: false, out compiled);

    private bool TryCompileBoundedSortedSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
        => TryCompileSortedSelect(select, parameters, context, outerRow, bounded: true, out compiled);

    private bool TryCompileSortedSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        bool bounded,
        out CompiledSelect compiled)
    {
        compiled = null!;

        if (select.OrderBy.Count == 0
            || select.Distinct
            || select.Having is not null
            || select.GroupBy.Count != 0
            || select.Limit is not null
            || select.Offset is not null
            || select.Projections.Count == 0)
        {
            return false;
        }

        var resolvedOrderBy = ResolveOrderBy(select.OrderBy, select.Projections);
        ValidateOrderByCollations(resolvedOrderBy);

        var target = ResolveScanTarget(select.Source, context);
        if (target is null)
            return false;

        if (bounded && select.Projections.Any(
                projection => !IsBoundedSortedProjection(projection.Expression, target)))
            return false;

        // Lower every projection to a column read or folded constant, exactly as the
        // unordered scan compiler does; anything else (e.g. computed expressions or an
        // unresolved qualified star) declines the route.
        var projections = new List<SortedScanColumn>();
        foreach (var projection in select.Projections)
        {
            if (!TryLowerSortedProjection(projection.Expression, target, parameters, context, projections))
                return false;
        }

        if (projections.Count == 0)
            return false;

        // ORDER BY keys are resolved (ordinal/alias) exactly like the evaluator, then must
        // be evaluable against a single scanned row and must not read an unbacked rowid,
        // which the materialized declared-column row cannot supply.
        foreach (var term in resolvedOrderBy)
        {
            if ((!IsScanPredicate(term.Expression)
                    || ReferencesUnbackedRowid(term.Expression, target))
                || (bounded && !IsBoundedSortedOrderKey(term, target)))
                return false;
        }

        VdbeRowPredicate? predicate = null;
        if (select.Where is not null)
        {
            predicate = CompileRowPredicate(select.Where, target, parameters, context, outerRow);
            if (predicate is null)
                return false;
        }

        // The comparer wraps each scanned row and defers to the evaluator's CompareRows so
        // direction (ASC/DESC), NULL ordering, collation, and multi-key precedence match the
        // evaluator byte-for-byte; the sorter itself guarantees stable (scan-order) ties.
        var columns = target.Columns;
        var qualifiedColumns = BuildQualifiedColumns(target.Qualifier, columns);
        VdbeRowComparer comparer = (leftRow, rightRow) => CompareRows(
            new SourceRow(columns, leftRow, qualifiedColumns, outerRow),
            new SourceRow(columns, rightRow, qualifiedColumns, outerRow),
            resolvedOrderBy,
            parameters,
            context);

        var program = SortedScanProgramBuilder.Build(
            target.TableName,
            columns.Length,
            projections,
            comparer,
            predicate);
        compiled = new CompiledSelect(program, [new VdbeCursorSource(target.Rows)]);
        return true;
    }

    private static bool IsBoundedSortedProjection(Expression expression, ScanTarget target)
        => expression is StarExpression or ColumnExpression or LiteralExpression
            || (expression is QualifiedStarExpression qualifiedStar
                && string.Equals(
                    qualifiedStar.Qualifier,
                    target.Qualifier,
                    StringComparison.OrdinalIgnoreCase));

    private static bool IsBoundedSortedOrderKey(OrderByTerm term, ScanTarget target)
    {
        var expression = term.Expression;
        while (expression is CollationExpression collation)
            expression = collation.Expression;

        return expression is ColumnExpression column
            && target.ResolveColumnIndex(column.Name) is not null;
    }

    // Mirrors SelectStatementCompiler's projection lowering: bare columns and a star whose
    // qualifier resolves to this sole source become column reads, constant scalars fold to a
    // literal, and everything else declines.
    private bool TryLowerSortedProjection(
        Expression expression,
        ScanTarget target,
        SqlValue[] parameters,
        QueryContext context,
        List<SortedScanColumn> projections)
    {
        switch (expression)
        {
            case StarExpression:
                if (target.Columns.Length == 0)
                    return false;

                for (var index = 0; index < target.Columns.Length; index++)
                    projections.Add(SortedScanColumn.ForColumn(index));
                return true;
            case ColumnExpression column when target.ResolveColumnIndex(column.Name) is { } columnIndex:
                projections.Add(SortedScanColumn.ForColumn(columnIndex));
                return true;
            case QualifiedStarExpression qualifiedStar
                when string.Equals(
                    qualifiedStar.Qualifier,
                    target.Qualifier,
                    StringComparison.OrdinalIgnoreCase):
                if (target.Columns.Length == 0)
                    return false;

                for (var index = 0; index < target.Columns.Length; index++)
                    projections.Add(SortedScanColumn.ForColumn(index));
                return true;
            case QualifiedStarExpression:
                // Preserve evaluator ownership of an unresolved qualifier and its diagnostic.
                return false;
            default:
                if (IsConstantScalarExpression(expression))
                {
                    projections.Add(SortedScanColumn.ForConstant(Evaluate(expression, parameters, null, context)));
                    return true;
                }

                return false;
        }
    }

    // Lowers a two-table INNER or LEFT OUTER join over base tables into a nested-loop
    // VdbeProgram driven by JoinProgramBuilder, or returns false so the evaluator keeps
    // ownership of shapes the join route cannot preserve exactly.
    //
    // Supported (routed to the VDBE):
    //  - select.Source is a single JoinTableSource whose Left and Right are both real base
    //    tables (each resolvable through ResolveScanTarget). A nested JoinTableSource never
    //    resolves as a base table, so three-or-more-table (N-way) joins decline here.
    //  - join kind INNER or LEFT OUTER only (RIGHT/FULL stay on the evaluator).
    //  - an explicit ON condition, or no condition at all (comma/CROSS join). USING/NATURAL
    //    joins coalesce shared output columns, which the raw-concatenating builder cannot
    //    reproduce, so they stay on the evaluator.
    //  - projections that are each "*", a qualified "t.*", a bare column resolvable to a
    //    combined-row ordinal, or a folded constant scalar. Anything the builder cannot
    //    project as a column read or a constant (computed expressions, aggregates, windows)
    //    declines the route.
    //  - the ON condition and, for INNER joins only, a post-join WHERE, each a row-at-a-time
    //    scan predicate that reads no unbacked rowid over the combined row. Because an INNER
    //    join emits exactly the ON-matching pairs the evaluator then filters by WHERE,
    //    testing "ON AND WHERE" per pair is identical. A bounded caller may additionally opt
    //    into the exact direct LEFT WHERE subset, which runs after null extension.
    //
    // Deliberately excluded (kept on the evaluator): DISTINCT, GROUP BY/HAVING, ORDER BY,
    // LIMIT/OFFSET, aggregate/window projections, computed-expression projections, USING/
    // NATURAL joins, RIGHT/FULL joins, joins of more than two base tables, and LEFT OUTER
    // joins carrying a WHERE clause outside the bounded exact subset.
    private bool TryCompileJoinSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        bool allowPostJoinWhere,
        out CompiledSelect compiled)
    {
        compiled = null!;

        // Clauses that reshape or reorder the result set stay with the evaluator, matching
        // the single-table scan route's boundary exactly.
        if (select.Distinct
            || select.Having is not null
            || select.GroupBy.Count != 0
            || select.OrderBy.Count != 0
            || select.Limit is not null
            || select.Offset is not null
            || select.Projections.Count == 0)
        {
            return false;
        }

        if (select.Source is not JoinTableSource join)
            return false;

        // Only the two shapes JoinProgramBuilder lowers; RIGHT/FULL stay on the evaluator.
        JoinType joinType;
        if (join.Kind == JoinKind.Inner)
            joinType = JoinType.Inner;
        else if (join.Kind == JoinKind.Left)
            joinType = JoinType.LeftOuter;
        else
            return false;

        // USING/NATURAL joins coalesce their shared columns into a single output column; the
        // builder concatenates the two rows verbatim, so those joins stay on the evaluator.
        if (join.UsingColumns is not null || join.Natural)
            return false;

        // Both sides must be real base tables. A nested join, derived table, CTE, view,
        // series, or schema table declines, which is what keeps N-way joins on the evaluator.
        var leftTarget = ResolveScanTarget(join.Left, context);
        var rightTarget = ResolveScanTarget(join.Right, context);
        if (leftTarget is null || rightTarget is null)
            return false;

        // The builder needs at least one column per side (a base table always has one).
        if (leftTarget.Columns.Length == 0 || rightTarget.Columns.Length == 0)
            return false;

        // Aggregate/window projections belong to routes this method does not own, so decline
        // them and let the evaluator (or another route) raise its own value or error.
        foreach (var projection in select.Projections)
        {
            if (ContainsAggregate(projection.Expression) || ContainsWindowFunction(projection.Expression))
                return false;
        }

        // Build the combined (left ++ right) row shape exactly as GetJoinRows does, so a
        // reconstructed SourceRow resolves qualified and bare columns identically to the
        // evaluator: left columns occupy ordinals 0..leftWidth-1 and right columns follow.
        var leftColumns = leftTarget.Columns;
        var rightColumns = rightTarget.Columns;
        var leftWidth = leftColumns.Length;
        var combinedColumns = leftColumns.Concat(rightColumns).ToArray();
        var combinedQualified = CombineQualifiedColumns(
            BuildQualifiedColumns(leftTarget.Qualifier, leftColumns),
            BuildQualifiedColumns(rightTarget.Qualifier, rightColumns),
            leftWidth);
        var outputColumns = GetOutputColumns(join, context);
        var rawOutputColumns = GetRawOutputColumns(join, context);

        // A data-free ScanTarget over the combined row lets the projection and predicate
        // helpers reuse the same (possibly-qualified) column resolution and unbacked-rowid
        // guard the single-table routes use, so the mapping matches SourceRow.GetValue.
        var combinedTarget = new ScanTarget(
            leftTarget.TableName,
            leftTarget.Qualifier,
            combinedColumns,
            [],
            name => ResolveCombinedColumnIndex(name, combinedColumns, combinedQualified));

        var projections = new List<JoinProjection>();
        foreach (var projection in select.Projections)
        {
            if (!TryLowerJoinProjection(
                    projection.Expression,
                    combinedTarget,
                    outputColumns,
                    rawOutputColumns,
                    parameters,
                    context,
                    projections))
            {
                return false;
            }
        }

        if (projections.Count == 0)
            return false;

        // A LEFT WHERE must run only after the matching state has decided whether a null-extended
        // row exists. Only the bounded caller can opt into its narrow direct-comparison subset;
        // an INNER join instead folds WHERE into the per-pair predicate.
        var whereExpr = select.Where;
        if (whereExpr is not null
            && joinType == JoinType.LeftOuter
            && (!allowPostJoinWhere
                || !IsExactLeftOuterJoinWherePredicate(whereExpr, leftTarget, rightTarget)))
            return false;

        var condition = join.Condition;
        if (condition is not null && !IsCompilableJoinPredicate(condition, combinedTarget))
            return false;
        if (whereExpr is not null && !IsCompilableJoinPredicate(whereExpr, combinedTarget))
            return false;

        VdbeRowPredicate? predicate = null;
        VdbeRowPredicate? postJoinPredicate = null;
        if (joinType == JoinType.LeftOuter)
        {
            if (condition is not null)
            {
                predicate = row => IsTrue(Evaluate(
                    condition,
                    parameters,
                    new SourceRow(combinedColumns, row, combinedQualified, outerRow, outputColumns),
                    context));
            }

            if (whereExpr is not null)
            {
                postJoinPredicate = row => IsTrue(Evaluate(
                    whereExpr,
                    parameters,
                    new SourceRow(combinedColumns, row, combinedQualified, outerRow, outputColumns),
                    context));
            }
        }
        else if (condition is not null || whereExpr is not null)
        {
            // Reconstruct the combined SourceRow exactly as GetJoinRows does and defer value
            // semantics to the evaluator, so the compiled gate matches JoinConditionMatches
            // (ON) and the evaluator's post-join WHERE byte-for-byte.
            predicate = row =>
            {
                var combined = new SourceRow(combinedColumns, row, combinedQualified, outerRow, outputColumns);
                if (condition is not null && !IsTrue(Evaluate(condition, parameters, combined, context)))
                    return false;

                return whereExpr is null || IsTrue(Evaluate(whereExpr, parameters, combined, context));
            };
        }

        var program = JoinProgramBuilder.Build(
            leftTarget.TableName,
            leftWidth,
            rightTarget.TableName,
            rightColumns.Length,
            joinType,
            projections,
            predicate,
            postJoinPredicate);
        compiled = new CompiledSelect(
            program,
            [new VdbeCursorSource(leftTarget.Rows), new VdbeCursorSource(rightTarget.Rows)]);
        return true;
    }

    // Mirrors the evaluator's join projection: "*" expands to every join output column,
    // "t.*" to that source's raw columns, a bare column reads its combined ordinal, and a
    // constant scalar folds to a literal. Because a routed join carries no USING/NATURAL
    // coalescing, each output column's ordinal is its combined-row index, so a column read is
    // byte-identical to GetOutputValue. Anything else declines so the evaluator keeps it.
    private bool TryLowerJoinProjection(
        Expression expression,
        ScanTarget combinedTarget,
        IReadOnlyList<OutputColumn> outputColumns,
        IReadOnlyList<OutputColumn> rawOutputColumns,
        SqlValue[] parameters,
        QueryContext context,
        List<JoinProjection> projections)
    {
        switch (expression)
        {
            case StarExpression:
                if (outputColumns.Count == 0)
                    return false;

                foreach (var column in outputColumns)
                    projections.Add(JoinProjection.ForColumn(column.Index));
                return true;
            case QualifiedStarExpression qualifiedStar:
                // A qualifier that matches neither side declines so the evaluator raises its
                // exact "no such table" error on fallback.
                var matched = false;
                foreach (var column in rawOutputColumns)
                {
                    if (string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase))
                    {
                        projections.Add(JoinProjection.ForColumn(column.Index));
                        matched = true;
                    }
                }

                return matched;
            case ColumnExpression column when combinedTarget.ResolveColumnIndex(column.Name) is { } columnIndex:
                projections.Add(JoinProjection.ForColumn(columnIndex));
                return true;
            default:
                if (IsConstantScalarExpression(expression))
                {
                    projections.Add(JoinProjection.ForConstant(Evaluate(expression, parameters, null, context)));
                    return true;
                }

                return false;
        }
    }

    // A join ON/WHERE clause is compilable when it can be evaluated against a single combined
    // row: it must be a row-at-a-time scan predicate (no subquery/EXISTS/aggregate) and must
    // not read a rowid pseudo-column, which a joined row never exposes.
    private bool IsCompilableJoinPredicate(Expression expression, ScanTarget combinedTarget)
        => IsScanPredicate(expression) && !ReferencesUnbackedRowid(expression, combinedTarget);

    // Resolves a (possibly qualified) column reference to its ordinal in the combined
    // (left ++ right) row, mirroring SourceRow.GetValue for a non-coalesced join: a qualified
    // name hits the combined qualified map, and a bare name takes the first matching declared
    // column. Names that do not name a combined column (rowid pseudo-columns, outer-row
    // references) return null so the caller declines and the evaluator keeps ownership.
    private static int? ResolveCombinedColumnIndex(
        string name,
        string[] combinedColumns,
        IReadOnlyDictionary<string, int> combinedQualified)
    {
        if (combinedQualified.TryGetValue(name, out var qualifiedIndex))
            return qualifiedIndex;

        for (var index = 0; index < combinedColumns.Length; index++)
        {
            if (string.Equals(combinedColumns[index], name, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return null;
    }

    // Lowers a whole-table (scalar) or GROUP BY aggregation over a single base table into a
    // real VdbeProgram driven by the AggReset/AggStep/AggFinalize opcode family (plus the
    // sorter and SameGroup/Goto control flow for grouping), or returns false so the
    // evaluator keeps ownership of shapes the aggregate route cannot preserve exactly.
    //
    // Supported (routed to the VDBE): a single real base table; optional row-at-a-time
    // WHERE and aggregate-only HAVING predicates; and projections that are each a supported
    // built-in or registered aggregate call over bare, backed columns (COUNT(*)/COUNT()/
    // COUNT(col)/SUM/TOTAL/AVG/MIN/MAX/GROUP_CONCAT(col)), a bare-column GROUP BY key
    // (grouped only), or a folded constant that is also an aggregate expression.
    // Accumulation, empty-input identities, group ordering (first-seen), and group equality all
    // reuse the evaluator's own helpers, so a routed result row is byte-identical to the evaluator's.
    //
    // Deliberately excluded (kept on the evaluator): DISTINCT, ORDER
    // BY, window functions, DISTINCT/FILTER aggregate modifiers, aggregate arguments that
    // are not bare columns (e.g. sum(x+1), group_concat(x,'-')), composite aggregate
    // expressions (e.g. sum(x)+1), non-aggregate/non-constant projections that force the
    // evaluator's mixing/GROUP BY errors, group keys or group-key projections that are not
    // bare columns, grouped queries with no aggregate, and joins/derived/CTE/view/schema or
    // source-less selects.
    private bool TryCompileAggregateSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        compiled = null!;

        if (select.Distinct
            || select.Limit is not null
            || select.Offset is not null
            || select.OrderBy.Count != 0
            || select.Projections.Count == 0)
        {
            return false;
        }

        var grouped = select.GroupBy.Count > 0;
        var hasAggregate = select.Projections.Any(projection => ContainsAggregate(projection.Expression));
        if (!grouped && !hasAggregate)
            return false;

        var target = ResolveScanTarget(select.Source, context);
        if (target is null)
            return false;

        // Every GROUP BY key must be a bare column resolvable to a real ordinal; anything
        // else (expressions, unbacked rowids) declines so the evaluator keeps ownership.
        var groupKeyColumns = new List<int>();
        var groupKeyNames = new List<string>();
        foreach (var expression in select.GroupBy)
        {
            if (expression is not ColumnExpression column
                || target.ResolveColumnIndex(column.Name) is not { } ordinal)
            {
                return false;
            }

            groupKeyColumns.Add(ordinal);
            groupKeyNames.Add(column.Name);
        }

        // Classify each projection into exactly one of: a supported aggregate call, a group
        // key (grouped only), or a folded constant. Any other shape declines the route so
        // the evaluator produces the value or raises its exact error on fallback.
        var aggregates = new List<AggregateFunctionSpec>();
        var outputs = new List<AggregateOutput>();
        foreach (var projection in select.Projections)
        {
            if (TryClassifyAggregateCall(
                    projection.Expression, target, parameters, context, outerRow, aggregates, out var aggregateOutput))
            {
                outputs.Add(aggregateOutput);
                continue;
            }

            if (grouped && TryResolveGroupKeyOutput(projection.Expression, select.GroupBy, out var groupKeyOutput))
            {
                outputs.Add(groupKeyOutput);
                continue;
            }

            // A constant is only foldable here when the evaluator also treats it as an
            // aggregate expression; otherwise the evaluator would raise a mixing/GROUP BY
            // error that the route must preserve by declining.
            if (IsConstantScalarExpression(projection.Expression) && IsAggregateExpression(projection.Expression))
            {
                outputs.Add(AggregateOutput.ForConstant(Evaluate(projection.Expression, parameters, null, context)));
                continue;
            }

            return false;
        }

        // The builder requires at least one aggregate; a grouped projection list of only
        // keys/constants (e.g. SELECT x FROM t GROUP BY x) falls back to the evaluator.
        if (aggregates.Count == 0)
            return false;

        AggregateHavingFilter? having = null;
        if (select.Having is not null
            && !TryCompileAggregateHaving(
                select.Having,
                target,
                parameters,
                context,
                outerRow,
                aggregates,
                out having))
        {
            return false;
        }

        VdbeRowPredicate? predicate = null;
        if (select.Where is not null)
        {
            predicate = CompileRowPredicate(select.Where, target, parameters, context, outerRow);
            if (predicate is null)
                return false;
        }

        VdbeProgram program;
        if (grouped)
        {
            var (orderComparer, groupComparer) = BuildGroupComparers(
                select.GroupBy, groupKeyNames, target, predicate, parameters, context, outerRow);
            program = AggregateProgramBuilder.BuildGrouped(
                target.TableName,
                target.Columns.Length,
                groupKeyColumns,
                aggregates,
                outputs,
                orderComparer,
                groupComparer,
                predicate,
                having);
        }
        else
        {
            program = AggregateProgramBuilder.BuildScalar(
                target.TableName,
                target.Columns.Length,
                aggregates,
                outputs,
                predicate,
                having);
        }

        compiled = new CompiledSelect(program, [new VdbeCursorSource(target.Rows)]);
        return true;
    }

    // Rewrites an aggregate-only HAVING expression into a predicate over finalized aggregate
    // registers. This keeps comparison, NULL, affinity, and parameter semantics in Evaluate while
    // the VDBE owns the group finalization and conditional result emission.
    private bool TryCompileAggregateHaving(
        Expression expression,
        ScanTarget target,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        List<AggregateFunctionSpec> aggregates,
        out AggregateHavingFilter having)
    {
        var inputs = new List<AggregateOutput>();
        var inputNames = new List<string>();
        if (!TryRewriteAggregateHaving(
                expression,
                target,
                parameters,
                context,
                outerRow,
                aggregates,
                inputs,
                inputNames,
                out var rewritten))
        {
            having = null!;
            return false;
        }

        var names = inputNames.ToArray();
        having = new AggregateHavingFilter(
            inputs,
            values => IsTrue(Evaluate(
                rewritten,
                parameters,
                new SourceRow(names, values, Parent: outerRow),
                context)),
            "skip aggregate result when HAVING is false");
        return true;
    }

    // The VDBE filter receives only a fixed register tuple. Restrict HAVING to aggregate calls
    // and scalar syntax whose remaining leaves are parameters or literals, so no representative-row,
    // subquery, collation-state, or non-deterministic function dependency can leak into the route.
    private bool TryRewriteAggregateHaving(
        Expression expression,
        ScanTarget target,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        List<AggregateFunctionSpec> aggregates,
        List<AggregateOutput> inputs,
        List<string> inputNames,
        out Expression rewritten)
    {
        switch (expression)
        {
            case LiteralExpression or ParameterExpression:
                rewritten = expression;
                return true;
            case FunctionExpression function:
                if (!TryClassifyAggregateCall(
                        function,
                        target,
                        parameters,
                        context,
                        outerRow,
                        aggregates,
                        out var output))
                {
                    rewritten = null!;
                    return false;
                }

                var inputIndex = inputs.Count;
                inputs.Add(output);
                var inputName = $"__turso_having_aggregate_{inputIndex}__";
                inputNames.Add(inputName);
                rewritten = new ColumnExpression(inputName);
                return true;
            case UnaryExpression unary when TryRewriteAggregateHaving(
                    unary.Operand,
                    target,
                    parameters,
                    context,
                    outerRow,
                    aggregates,
                    inputs,
                    inputNames,
                    out var operand):
                rewritten = unary with { Operand = operand };
                return true;
            case BinaryExpression binary
                when TryRewriteAggregateHaving(
                        binary.Left,
                        target,
                        parameters,
                        context,
                        outerRow,
                        aggregates,
                        inputs,
                        inputNames,
                        out var left)
                    && TryRewriteAggregateHaving(
                        binary.Right,
                        target,
                        parameters,
                        context,
                        outerRow,
                        aggregates,
                        inputs,
                        inputNames,
                        out var right):
                rewritten = binary with { Left = left, Right = right };
                return true;
            case CastExpression cast when TryRewriteAggregateHaving(
                    cast.Expression,
                    target,
                    parameters,
                    context,
                    outerRow,
                    aggregates,
                    inputs,
                    inputNames,
                    out var castInput):
                rewritten = cast with { Expression = castInput };
                return true;
            case CollationExpression collation when TryRewriteAggregateHaving(
                    collation.Expression,
                    target,
                    parameters,
                    context,
                    outerRow,
                    aggregates,
                    inputs,
                    inputNames,
                    out var collationInput):
                rewritten = collation with { Expression = collationInput };
                return true;
            default:
                rewritten = null!;
                return false;
        }
    }

    // Lowers the largest window subset that WindowProgramBuilder can run with EXACT running-frame
    // semantics: a single base-table SELECT whose window calls all share one running frame
    // (ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) over aggregate window functions. It reuses
    // the evaluator's own accumulation (BuildAccumulatorAggregate), partition equality
    // (BuildGroupComparers), and ordering (CompareRows) so a routed row is byte-identical to the
    // fallback. Anything the running-frame program cannot reproduce exactly declines (returns false)
    // so the tree-walking evaluator keeps ownership and raises its own value or error.
    //
    // Routed grammar (all conditions required):
    //   SELECT <proj> [,...] FROM <single base table> [WHERE <row predicate>]
    //   [ORDER BY <partition cols as prefix> , <window ORDER BY terms>]
    // where each <proj> is one of: '*', a bare backed column, a folded constant, or
    //   agg(<bare column>|*) OVER ([PARTITION BY <bare cols>] [ORDER BY <scan terms>]
    //                              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
    // with agg in {count, sum, total, avg, min, max, group_concat/1, registered aggregates}, and
    // every window call sharing one identical OVER spec.
    //
    // Exactness of the emitted (partition, order) sort order requires the top-level ORDER BY to be
    // the partition columns (in any direction, as a bijective prefix) followed by the window ORDER BY
    // terms verbatim; absent an ORDER BY the window must be unpartitioned and unordered so the sorter
    // preserves scan order. See the decline conditions below for everything routed to the evaluator.
    private bool TryCompileWindowSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        compiled = null!;

        // Only engage for a SELECT that actually projects a window call; every other shape is
        // owned by the scan/aggregate/sorted/join routes or the evaluator.
        if (!select.Projections.Any(projection => ContainsWindowFunction(projection.Expression)))
            return false;

        // Reshaping/dedup clauses stay on the evaluator so this route owns only the pure
        // partition -> order -> running-emit pipeline. LIMIT/OFFSET are handled upstream by
        // TryCompileLimitedSelect (whose bases decline windows), so they never reach here.
        if (select.Distinct
            || select.Having is not null
            || select.GroupBy.Count != 0
            || select.Limit is not null
            || select.Offset is not null
            || select.Projections.Count == 0)
        {
            return false;
        }

        // A window select that also carries a plain (non-window) aggregate or GROUP BY is an
        // evaluator error ("window functions cannot be combined with aggregates or GROUP BY");
        // decline so the evaluator raises it. ContainsAggregate is false for window calls.
        if (select.Projections.Any(projection => ContainsAggregate(projection.Expression)))
            return false;

        var target = ResolveScanTarget(select.Source, context);
        if (target is null)
            return false;

        // Classify every projection into a pass-through column, a running window call sharing
        // one spec, or a folded constant. Any other shape (computed expressions over window
        // results, qualified stars, scalar functions) declines so the evaluator produces it.
        WindowSpecification? spec = null;
        var windows = new List<AggregateFunctionSpec>();
        var outputs = new List<WindowOutput>();
        var sawWindow = false;
        foreach (var projection in select.Projections)
        {
            switch (projection.Expression)
            {
                case StarExpression:
                    if (target.Columns.Length == 0)
                        return false;
                    for (var index = 0; index < target.Columns.Length; index++)
                        outputs.Add(WindowOutput.ForColumn(index));
                    continue;
                case ColumnExpression column when target.ResolveColumnIndex(column.Name) is { } ordinal:
                    outputs.Add(WindowOutput.ForColumn(ordinal));
                    continue;
                case FunctionExpression function when function.Window is not null:
                    if (!TryClassifyRunningWindowCall(
                            function, target, parameters, context, outerRow, ref spec, windows, out var windowOutput))
                    {
                        return false;
                    }

                    outputs.Add(windowOutput);
                    sawWindow = true;
                    continue;
                default:
                    if (IsConstantScalarExpression(projection.Expression))
                    {
                        outputs.Add(WindowOutput.ForConstant(Evaluate(projection.Expression, parameters, null, context)));
                        continue;
                    }

                    return false;
            }
        }

        if (!sawWindow || spec is null)
            return false;

        ValidateOrderByCollations(spec.OrderBy);

        // PARTITION BY keys must be bare, backed columns so the builder can copy them into the
        // partition-key registers and the reused group-key equality can compare them.
        var partitionColumns = new List<int>();
        var partitionNames = new List<string>();
        foreach (var partitionExpression in spec.PartitionBy)
        {
            if (partitionExpression is not ColumnExpression column
                || target.ResolveColumnIndex(column.Name) is not { } ordinal)
            {
                return false;
            }

            partitionColumns.Add(ordinal);
            partitionNames.Add(column.Name);
        }

        // Window ORDER BY terms drive the running accumulation order, so they must be evaluable
        // against a single scanned row and must not read an unbacked rowid the materialized row
        // cannot supply. (IsScanPredicate already rejects nested window/aggregate terms.)
        foreach (var term in spec.OrderBy)
        {
            if (!IsScanPredicate(term.Expression) || ReferencesUnbackedRowid(term.Expression, target))
                return false;
        }

        var partitionCount = partitionColumns.Count;
        var windowOrderCount = spec.OrderBy.Count;

        VdbeRowPredicate? predicate = null;
        if (select.Where is not null)
        {
            predicate = CompileRowPredicate(select.Where, target, parameters, context, outerRow);
            if (predicate is null)
                return false;
        }

        // The builder emits rows in its own (partition, order) sort order. That order is exact
        // only when the evaluator would emit the same order:
        //   * no ORDER BY  -> the evaluator emits scan order, which matches only the single
        //     unordered, unpartitioned running frame; the sorter preserves scan order.
        //   * ORDER BY      -> it must be [all partition columns as a bijective prefix] ++ [the
        //     window ORDER BY terms verbatim], so the sort is partition-contiguous with window
        //     order within each partition and equals the evaluator's output order term-for-term.
        VdbeRowComparer orderComparer;
        if (select.OrderBy.Count == 0)
        {
            if (partitionCount != 0 || windowOrderCount != 0)
                return false;

            orderComparer = static (_, _) => 0;
        }
        else
        {
            var resolvedOrderBy = ResolveOrderBy(select.OrderBy, select.Projections);
            ValidateOrderByCollations(resolvedOrderBy);
            if (resolvedOrderBy.Count != partitionCount + windowOrderCount)
                return false;

            var partitionOrdinals = new HashSet<int>(partitionColumns);
            var seenOrdinals = new HashSet<int>();
            for (var index = 0; index < partitionCount; index++)
            {
                if (resolvedOrderBy[index].Expression is not ColumnExpression column
                    || target.ResolveColumnIndex(column.Name) is not { } ordinal
                    || !partitionOrdinals.Contains(ordinal)
                    || !seenOrdinals.Add(ordinal))
                {
                    return false;
                }
            }

            for (var index = 0; index < windowOrderCount; index++)
            {
                var top = resolvedOrderBy[partitionCount + index];
                var windowTerm = spec.OrderBy[index];
                if (!top.Expression.Equals(windowTerm.Expression)
                    || top.Descending != windowTerm.Descending
                    || top.NullPlacement != windowTerm.NullPlacement)
                    return false;
            }

            foreach (var term in resolvedOrderBy)
            {
                if (!IsScanPredicate(term.Expression) || ReferencesUnbackedRowid(term.Expression, target))
                    return false;
            }

            var columns = target.Columns;
            var qualifiedColumns = BuildQualifiedColumns(target.Qualifier, columns);
            orderComparer = (leftRow, rightRow) => CompareRows(
                new SourceRow(columns, leftRow, qualifiedColumns, outerRow),
                new SourceRow(columns, rightRow, qualifiedColumns, outerRow),
                resolvedOrderBy,
                parameters,
                context);
        }

        // Partition boundary detection reuses the evaluator's group-key equality so a routed
        // partition break matches the evaluator's GetGroupKey exactly. The builder requires a
        // partition comparer iff there are partition columns.
        VdbeGroupComparer? partitionComparer = null;
        if (partitionCount > 0)
        {
            var (_, groupComparer) = BuildGroupComparers(
                spec.PartitionBy, partitionNames, target, predicate, parameters, context, outerRow);
            partitionComparer = groupComparer;
        }

        var program = WindowProgramBuilder.Build(
            target.TableName,
            target.Columns.Length,
            partitionColumns,
            windows,
            outputs,
            orderComparer,
            partitionComparer,
            predicate);
        compiled = new CompiledSelect(program, [new VdbeCursorSource(target.Rows)]);
        return true;
    }

    // Recognizes a top-level running-frame window call over bare, backed columns, appends its
    // VdbeAggregate to <paramref name="windows"/>, and yields the output that projects the
    // finalized per-row value. Declines (returns false) for DISTINCT/FILTER modifiers, any frame
    // other than the running frame, non-aggregate window functions (row_number/rank/percentile),
    // arguments that are not bare columns, or a spec that differs from an earlier window call, so
    // those shapes fall back to the evaluator, which produces the value or its exact error.
    private bool TryClassifyRunningWindowCall(
        FunctionExpression function,
        ScanTarget target,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        ref WindowSpecification? spec,
        List<AggregateFunctionSpec> windows,
        out WindowOutput output)
    {
        output = default;

        // Frame/window modifiers the running-frame accumulator cannot honor. FILTER would need to
        // evaluate a predicate over columns the accumulator does not buffer; DISTINCT dedup is not
        // modeled by the running AggStep.
        if (function.Distinct || function.Filter is not null)
            return false;

        var window = function.Window!;
        if (!IsRunningWindowFrame(window.Frame))
            return false;

        // Only aggregate window functions the evaluator itself accepts. Percentile and ranking
        // (row_number/rank/lag/...) calls decline so the evaluator raises its own
        // "not a supported window function" diagnostic instead of the route silently diverging.
        // Percentile is excluded because the evaluator's ValidateWindowFunction rejects it as a
        // window function even though it is a managed aggregate.
        if (!IsBuiltInAggregate(function)
            && !TryGetAggregateFunction(function.Name, function.Arguments.Count, out _))
        {
            return false;
        }

        // Every window call must share one identical OVER spec so a single sorter pass and one
        // partition/order layout serve them all.
        if (spec is null)
            spec = window;
        else if (!WindowSpecsEqual(spec, window))
            return false;

        // Arguments must be bare, backed columns (or the nullary count(*)); group_concat(col, sep)
        // and any computed argument decline because the accumulator only buffers column tuples.
        var argumentColumns = new List<int>(function.Arguments.Count);
        var argumentNames = new string[function.Arguments.Count];
        for (var index = 0; index < function.Arguments.Count; index++)
        {
            if (function.Arguments[index] is not ColumnExpression column
                || target.ResolveColumnIndex(column.Name) is not { } ordinal)
            {
                return false;
            }

            argumentColumns.Add(ordinal);
            argumentNames[index] = column.Name;
        }

        var windowIndex = windows.Count;
        windows.Add(new AggregateFunctionSpec(
            BuildAccumulatorAggregate(function, argumentNames, parameters, context, outerRow),
            argumentColumns));
        output = WindowOutput.ForWindow(windowIndex);
        return true;
    }

    // The one frame WindowProgramBuilder models: ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW.
    // The parser also produces this shape for bare "ROWS UNBOUNDED PRECEDING". A null frame (the
    // default RANGE frame) and every bounded/forward-looking ROWS frame decline.
    private static bool IsRunningWindowFrame(WindowFrame? frame)
        => frame is not null
            && frame.Start.Kind == FrameBoundKind.UnboundedPreceding
            && frame.End.Kind == FrameBoundKind.CurrentRow;

    // Structural equality of two window specs: PARTITION BY expressions and ORDER BY terms compared
    // element-wise (record equality on the IReadOnlyList fields is reference equality, so the lists
    // must be walked), and the frame by value.
    private static bool WindowSpecsEqual(WindowSpecification left, WindowSpecification right)
    {
        if (left.PartitionBy.Count != right.PartitionBy.Count || left.OrderBy.Count != right.OrderBy.Count)
            return false;

        for (var index = 0; index < left.PartitionBy.Count; index++)
        {
            if (!left.PartitionBy[index].Equals(right.PartitionBy[index]))
                return false;
        }

        for (var index = 0; index < left.OrderBy.Count; index++)
        {
            if (!left.OrderBy[index].Equals(right.OrderBy[index]))
                return false;
        }

        return Equals(left.Frame, right.Frame);
    }

    // Recognizes a top-level supported aggregate call over bare, backed columns, appends its
    // VdbeAggregate to <paramref name="aggregates"/>, and yields the output that projects the
    // accumulator's finalized value. Declines (returns false) for windowed calls,
    // DISTINCT/FILTER modifiers, unsupported functions, or arguments that are not bare
    // columns, so those shapes fall back to the evaluator.
    private bool TryClassifyAggregateCall(
        Expression expression,
        ScanTarget target,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        List<AggregateFunctionSpec> aggregates,
        out AggregateOutput output)
    {
        output = default;
        if (expression is not FunctionExpression function
            || function.Window is not null
            || function.Filter is not null
            || function.Distinct)
        {
            return false;
        }

        if (!IsBuiltInAggregate(function)
            && !IsManagedPercentileAggregate(function.Name)
            && !TryGetAggregateFunction(function.Name, function.Arguments.Count, out _))
        {
            return false;
        }

        var argumentColumns = new List<int>(function.Arguments.Count);
        var argumentNames = new string[function.Arguments.Count];
        for (var i = 0; i < function.Arguments.Count; i++)
        {
            if (function.Arguments[i] is not ColumnExpression column
                || target.ResolveColumnIndex(column.Name) is not { } ordinal)
            {
                return false;
            }

            argumentColumns.Add(ordinal);
            argumentNames[i] = column.Name;
        }

        var accumulator = aggregates.Count;
        aggregates.Add(new AggregateFunctionSpec(
            BuildAccumulatorAggregate(function, argumentNames, parameters, context, outerRow),
            argumentColumns));
        output = AggregateOutput.ForAggregate(accumulator);
        return true;
    }

    // Builds a VdbeAggregate whose accumulation replays the evaluator's own aggregate
    // semantics: it buffers each scanned argument tuple as a synthetic single-row source
    // keyed by the argument column names, then finalizes by handing those rows to
    // EvaluateAggregateFunction. This reuses the evaluator exactly (COUNT ignoring NULLs,
    // SUM's integer/real promotion, MIN/MAX typing, GROUP_CONCAT ordering, and every
    // empty-input identity), so a routed aggregate is byte-identical to the fallback.
    private VdbeAggregate BuildAccumulatorAggregate(
        FunctionExpression function,
        string[] argumentNames,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        return new VdbeAggregate
        {
            Name = function.Name.ToLowerInvariant(),
            CreateContext = static () => new List<SqlValue[]>(),
            Accumulate = static (contextObject, arguments) =>
            {
                ((List<SqlValue[]>)contextObject!).Add(arguments);
                return contextObject;
            },
            Finalize = contextObject =>
            {
                var tuples = (List<SqlValue[]>)contextObject!;
                var rows = new List<SourceRow>(tuples.Count);
                foreach (var tuple in tuples)
                    rows.Add(new SourceRow(argumentNames, tuple, Parent: outerRow));

                return EvaluateAggregateFunction(function, rows, parameters, context);
            },
        };
    }

    // Matches a projection that is structurally one of the GROUP BY key expressions, so it
    // projects that group's saved key. Mirrors the evaluator's rule that a non-aggregate
    // projection must appear in GROUP BY: a group-key column projects from the saved key,
    // and anything else declines so the evaluator raises its error on fallback.
    private static bool TryResolveGroupKeyOutput(
        Expression expression,
        IReadOnlyList<Expression> groupBy,
        out AggregateOutput output)
    {
        for (var index = 0; index < groupBy.Count; index++)
        {
            if (groupBy[index].Equals(expression))
            {
                output = AggregateOutput.ForGroupKey(index);
                return true;
            }
        }

        output = default;
        return false;
    }

    // Builds the two delegates a grouped aggregate program needs: an order comparer that
    // makes each group's rows contiguous and sequences groups by first appearance (so the
    // stable sorter reproduces the evaluator's first-seen Dictionary order with scan-order
    // rows inside each group), and an equality comparer that decides group membership. Both
    // reuse GetGroupKey so grouping keys, NULL grouping, and integer/real key coalescing are
    // byte-identical to the evaluator. The first-seen map is built lazily (so EXPLAIN, which
    // never runs the program, does no data work) and rebuilt on a miss (so it stays correct
    // across append-only reset/replay).
    private (VdbeRowComparer OrderComparer, VdbeGroupComparer GroupComparer) BuildGroupComparers(
        IReadOnlyList<Expression> groupBy,
        IReadOnlyList<string> groupKeyNames,
        ScanTarget target,
        VdbeRowPredicate? predicate,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        var columns = target.Columns;
        var qualifiedColumns = BuildQualifiedColumns(target.Qualifier, columns);
        var keyNames = groupKeyNames.ToArray();

        string KeyOfRow(SqlValue[] row) =>
            GetGroupKey(groupBy, parameters, new SourceRow(columns, row, qualifiedColumns, outerRow), context);
        string KeyOfSlice(SqlValue[] slice) =>
            GetGroupKey(groupBy, parameters, new SourceRow(keyNames, slice, Parent: outerRow), context);

        Dictionary<string, int>? firstSeen = null;
        void BuildFirstSeen()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var sequence = 0;
            foreach (var row in target.Rows)
            {
                if (predicate is not null && !predicate(row))
                    continue;

                var key = KeyOfRow(row);
                if (!map.ContainsKey(key))
                    map[key] = sequence++;
            }

            firstSeen = map;
        }

        VdbeRowComparer orderComparer = (left, right) =>
        {
            var leftKey = KeyOfRow(left);
            var rightKey = KeyOfRow(right);
            if (string.Equals(leftKey, rightKey, StringComparison.Ordinal))
                return 0;

            if (firstSeen is null
                || !firstSeen.ContainsKey(leftKey)
                || !firstSeen.ContainsKey(rightKey))
            {
                BuildFirstSeen();
            }

            return firstSeen![leftKey].CompareTo(firstSeen[rightKey]);
        };

        VdbeGroupComparer groupComparer = (left, right) =>
            string.Equals(KeyOfSlice(left), KeyOfSlice(right), StringComparison.Ordinal);

        return (orderComparer, groupComparer);
    }

    // Resolves a table source to a single compilable base-table scan target, or
    // null when the source is anything else (schema table, CTE, view, join,
    // derived table, series, or a missing table) so the evaluator keeps ownership.
    private static ScanTarget? ResolveScanTarget(TableSource? source, QueryContext context)
    {
        if (source is not NamedTableSource named)
            return null;
        if (IsSchemaTable(named.Name)
            || context.CommonTableExpressions.ContainsKey(named.Name)
            || TryGetView(context, named.Name, out _))
        {
            return null;
        }

        if (!context.Tables.TryGetValue(named.Name, out var table))
            return null;

        var qualifier = named.Alias ?? named.Name;
        var columns = table.Columns;
        var qualifiedColumns = BuildQualifiedColumns(qualifier, columns);
        return new ScanTarget(
            named.Name,
            qualifier,
            columns,
            table.Rows,
            name => ResolveScanColumnIndex(name, columns, qualifiedColumns),
            table.HasRowid ? table.RowIds : null);
    }

    private static int? ResolveScanColumnIndex(
        string name,
        string[] columns,
        IReadOnlyDictionary<string, int> qualifiedColumns)
    {
        if (qualifiedColumns.TryGetValue(name, out var qualifiedIndex))
            return qualifiedIndex;

        for (var index = 0; index < columns.Length; index++)
        {
            if (string.Equals(columns[index], name, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return null;
    }

    // Builds a per-row predicate for a compilable WHERE clause, delegating value
    // semantics to the evaluator so the compiled filter matches it exactly. Returns
    // null for predicates that need more than the current row (subqueries, EXISTS,
    // or aggregates), keeping them on the evaluator.
    private VdbeRowPredicate? CompileRowPredicate(
        Expression where,
        ScanTarget target,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        if (!IsScanPredicate(where))
            return null;

        // The compiled scan only materializes declared columns, so a WHERE that reads the
        // rowid pseudo-column (and no real column shadows it) must run on the evaluator,
        // which resolves rowids from the scanned row.
        if (ReferencesUnbackedRowid(where, target))
            return null;

        var columns = target.Columns;
        var qualifiedColumns = BuildQualifiedColumns(target.Qualifier, columns);
        return row => IsTrue(Evaluate(
            where,
            parameters,
            new SourceRow(columns, row, qualifiedColumns, outerRow),
            context));
    }

    // A rowid-aware scan keeps the evaluator authoritative for SQL comparison semantics, but only claims a
    // single hidden-rowid comparison against a literal or bound parameter. More complex rowid expressions
    // remain evaluator-backed rather than risking a different name-resolution or error-timing contract.
    private VdbeRowIdPredicate? CompileSimpleRowIdPredicate(
        Expression where,
        ScanTarget target,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        if (!target.HasRowId || !IsSimpleRowIdComparison(where, target))
            return null;

        var columns = target.Columns;
        var qualifiedColumns = BuildQualifiedColumns(target.Qualifier, columns);
        return (row, rowId) => IsTrue(Evaluate(
            where,
            parameters,
            new SourceRow(
                columns,
                row,
                qualifiedColumns,
                outerRow,
                RowId: rowId,
                RowIdQualifier: target.Qualifier),
            context));
    }

    private static bool IsSimpleRowIdComparison(Expression expression, ScanTarget target)
    {
        if (expression is not BinaryExpression binary || !IsComparisonOperator(binary.Operator))
            return false;

        return (IsTargetRowIdReference(binary.Left, target) && IsLiteralOrParameter(binary.Right))
            || (IsTargetRowIdReference(binary.Right, target) && IsLiteralOrParameter(binary.Left));
    }

    private static bool IsTargetRowIdReference(Expression expression, ScanTarget target)
    {
        if (expression is not ColumnExpression column
            || !target.HasRowId
            || target.ResolveColumnIndex(column.Name) is not null)
        {
            return false;
        }

        var separator = column.Name.IndexOf('.');
        var bareName = separator < 0 ? column.Name : column.Name[(separator + 1)..];
        return EmbeddedTable.IsRowidAliasName(bareName)
            && (separator < 0
                || string.Equals(
                    column.Name[..separator],
                    target.Qualifier,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLiteralOrParameter(Expression expression)
        => expression is LiteralExpression or ParameterExpression;

    private static bool IsComparisonOperator(BinaryOperator op)
        => op is BinaryOperator.Is
            or BinaryOperator.IsNot
            or BinaryOperator.Equal
            or BinaryOperator.NotEqual
            or BinaryOperator.LessThan
            or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan
            or BinaryOperator.GreaterThanOrEqual;

    // Detects a reference to rowid/_rowid_/oid that the scan target does not back with a
    // declared column, so the predicate cannot be compiled against materialized columns.
    private static bool ReferencesUnbackedRowid(Expression expression, ScanTarget target)
    {
        switch (expression)
        {
            case ColumnExpression column:
                var separator = column.Name.IndexOf('.');
                var bare = separator < 0 ? column.Name : column.Name[(separator + 1)..];
                return EmbeddedTable.IsRowidAliasName(bare) && target.ResolveColumnIndex(column.Name) is null;
            case UnaryExpression unary:
                return ReferencesUnbackedRowid(unary.Operand, target);
            case BinaryExpression binary:
                return ReferencesUnbackedRowid(binary.Left, target)
                    || ReferencesUnbackedRowid(binary.Right, target);
            case CollationExpression collation:
                return ReferencesUnbackedRowid(collation.Expression, target);
            case CastExpression cast:
                return ReferencesUnbackedRowid(cast.Expression, target);
            case FunctionExpression function:
                return function.Arguments.Any(argument => ReferencesUnbackedRowid(argument, target));
            case CaseExpression @case:
                return (@case.Operand is not null && ReferencesUnbackedRowid(@case.Operand, target))
                    || @case.Clauses.Any(clause =>
                        ReferencesUnbackedRowid(clause.When, target) || ReferencesUnbackedRowid(clause.Then, target))
                    || (@case.Else is not null && ReferencesUnbackedRowid(@case.Else, target));
            case LikeExpression like:
                return ReferencesUnbackedRowid(like.Value, target)
                    || ReferencesUnbackedRowid(like.Pattern, target)
                    || (like.Escape is not null && ReferencesUnbackedRowid(like.Escape, target));
            case GlobExpression glob:
                return ReferencesUnbackedRowid(glob.Value, target)
                    || ReferencesUnbackedRowid(glob.Pattern, target);
            case BetweenExpression between:
                return ReferencesUnbackedRowid(between.Value, target)
                    || ReferencesUnbackedRowid(between.Lower, target)
                    || ReferencesUnbackedRowid(between.Upper, target);
            case InExpression @in:
                return ReferencesUnbackedRowid(@in.Value, target)
                    || @in.Values.Any(value => ReferencesUnbackedRowid(value, target));
            default:
                return false;
        }
    }

    // A WHERE clause is compilable when it can be evaluated against a single scanned
    // row: it must not embed subqueries, EXISTS, or aggregates, which the row-at-a-
    // time scan cannot satisfy on its own.
    private bool IsScanPredicate(Expression expression)
    {
        switch (expression)
        {
            case ScalarSubqueryExpression or ExistsExpression or InSubqueryExpression:
                return false;
            case LiteralExpression or ParameterExpression or ColumnExpression:
                return true;
            case StarExpression or QualifiedStarExpression:
                return false;
            case FunctionExpression function:
                return function.Window is null
                    && !function.CountStar
                    && function.Filter is null
                    && !ContainsAggregate(function)
                    && function.Arguments.All(IsScanPredicate);
            case UnaryExpression unary:
                return IsScanPredicate(unary.Operand);
            case BinaryExpression binary:
                return IsScanPredicate(binary.Left) && IsScanPredicate(binary.Right);
            case CollationExpression collation:
                return IsScanPredicate(collation.Expression);
            case CastExpression cast:
                return IsScanPredicate(cast.Expression);
            case CaseExpression @case:
                return (@case.Operand is null || IsScanPredicate(@case.Operand))
                    && @case.Clauses.All(clause =>
                        IsScanPredicate(clause.When) && IsScanPredicate(clause.Then))
                    && (@case.Else is null || IsScanPredicate(@case.Else));
            case LikeExpression like:
                return IsScanPredicate(like.Value)
                    && IsScanPredicate(like.Pattern)
                    && (like.Escape is null || IsScanPredicate(like.Escape));
            case GlobExpression glob:
                return IsScanPredicate(glob.Value) && IsScanPredicate(glob.Pattern);
            case BetweenExpression between:
                return IsScanPredicate(between.Value)
                    && IsScanPredicate(between.Lower)
                    && IsScanPredicate(between.Upper);
            case InExpression @in:
                return IsScanPredicate(@in.Value) && @in.Values.All(IsScanPredicate);
            default:
                return false;
        }
    }

    private static ExecutionResult RunCompiledProgram(CompiledSelect compiled, string[] columns)
        => RunCompiledProgram(compiled, columns, parameterBinding: null);

    // Runs a lowered read program, optionally supplying the late-bound parameter binding its
    // LoadParameter opcodes read. A null binding matches the constant-only routes, whose programs
    // declare no parameter slots; a routed parameterized VALUES supplies a binding sized to the
    // program's ParameterSlotCount.
    private static ExecutionResult RunCompiledProgram(
        CompiledSelect compiled,
        string[] columns,
        VdbeParameterBinding? parameterBinding)
    {
        using var runtime = new ResumableStatement(
            compiled.Program,
            compiled.CursorSources,
            parameterBinding: parameterBinding);
        var rows = new List<SqlValue[]>();
        while (true)
        {
            switch (runtime.StepResumable())
            {
                case ResumableStatementStepResult.Row:
                    rows.Add([.. runtime.CurrentRow!]);
                    break;
                case ResumableStatementStepResult.Done:
                    return new ExecutionResult(columns, rows, 0);
                default:
                    throw new EmbeddedSqlException("Compiled program yielded during evaluation.");
            }
        }
    }

    // Executes a lowered INSERT/UPDATE/DELETE program, buffering any RETURNING rows and
    // surfacing the rows-affected count and last-insert rowid the write opcodes tracked.
    private static ExecutionResult RunCompiledDml(
        CompiledDml compiled,
        string[] columns,
        bool hasReturning,
        SqlValue[] parameters)
    {
        using var runtime = new ResumableStatement(
            compiled.Program,
            compiled.CursorSources,
            compiled.RuntimeWriteTargets,
            BuildValuesBinding(compiled.ParameterIndices ?? [], parameters));
        var rows = new List<SqlValue[]>();
        while (true)
        {
            switch (runtime.StepResumable())
            {
                case ResumableStatementStepResult.Row:
                    rows.Add([.. runtime.CurrentRow!]);
                    break;
                case ResumableStatementStepResult.Done:
                    var rowsAffected = runtime.RowsAffected;
                    return new ExecutionResult(
                        hasReturning ? columns : [],
                        hasReturning ? rows : [],
                        rowsAffected,
                        rowsAffected > 0)
                    {
                        LastInsertRowId = runtime.LastInsertRowId,
                    };
                default:
                    throw new EmbeddedSqlException("Compiled program yielded during evaluation.");
            }
        }
    }

    // Attempts to lower an INSERT into a write program. Returns false (falling back to the
    // evaluator) for schema tables, missing tables, or RETURNING clauses outside the
    // lowerable subset. Genuine statement errors (bad columns) still surface via the
    // shared PrepareInsert/BuildInsertRow helpers, matching the evaluator exactly.
    private bool TryCompileInsert(
        InsertStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        out CompiledDml compiled,
        out string[] columns,
        out bool hasReturning)
    {
        compiled = null!;
        columns = [];
        hasReturning = false;

        if (statement.ConflictAlgorithm is not null
            || statement.Source is not null
            || context.CommonTableExpressions.Count != 0
            || IsSchemaTable(statement.TableName)
            || !context.Tables.TryGetValue(statement.TableName, out var table))
        {
            return false;
        }

        if (!TryCompileReturningClause(
                statement.Returning,
                table,
                statement.TableName,
                parameters,
                context,
                out var returningProgram,
                out columns,
                out hasReturning))
            return false;

        var plan = PrepareInsert(statement, table);
        var rowsToInsert = new List<SqlValue[]>(statement.Rows.Count);
        var insertedRowIds = new List<long>(statement.Rows.Count);
        var returningRows = hasReturning ? new List<SqlValue[]>(statement.Rows.Count) : null;
        var returningRowIds = hasReturning ? new List<long>(statement.Rows.Count) : null;
        var writeTarget = new VdbeWriteTarget
        {
            TableName = statement.TableName,
            RowCount = statement.Rows.Count,
            MutateRow = index =>
            {
                var (row, rowid) = BuildInsertRow(statement, table, plan, statement.Rows[index], parameters, context);
                rowsToInsert.Add(row);
                insertedRowIds.Add(rowid);
                returningRows?.Add(row);
                returningRowIds?.Add(rowid);
                return new VdbeRowMutation(row, rowid);
            },
            Commit = () =>
            {
                CommitInserts(context, statement.TableName, table, rowsToInsert, insertedRowIds);
                return table.HasRowid && insertedRowIds.Count > 0
                    ? insertedRowIds[^1]
                    : (long?)null;
            },
        };

        compiled = hasReturning
            ? DmlStatementCompiler.CompileWithFilter(
                DmlKind.Insert,
                statement.TableName,
                table.Columns.Length,
                filter: null,
                returningProgram!,
                writeTarget,
                new VdbeCursorSource(
                    returningRows!,
                    table.HasRowid ? returningRowIds : null))
            : DmlStatementCompiler.Compile(
                DmlKind.Insert,
                statement.TableName,
                table.Columns.Length,
                predicate: null,
                returning: Array.Empty<DmlReturningExpression>(),
                writeTarget);
        return true;
    }

    private bool TryCompileUpdate(
        UpdateStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        out CompiledDml compiled,
        out string[] columns,
        out bool hasReturning)
    {
        compiled = null!;
        columns = [];
        hasReturning = false;

        if (statement.Limit is not null
            || statement.Offset is not null
            || statement.EffectiveOrderBy.Count != 0
            || context.CommonTableExpressions.Count != 0
            || IsSchemaTable(statement.TableName)
            || !context.Tables.TryGetValue(statement.TableName, out var table))
        {
            return false;
        }

        DmlRowFilter? filter = null;
        if (statement.Where is not null)
        {
            filter = CompileDmlRowPredicate(statement.Where, table, statement.TableName, parameters, context);
            if (filter is null)
                return false;
        }

        if (!TryCompileReturningClause(
                statement.Returning,
                table,
                statement.TableName,
                parameters,
                context,
                out var returningProgram,
                out columns,
                out hasReturning))
            return false;

        var plan = PrepareUpdate(statement, table);
        var rowCount = table.Rows.Count;
        var newRows = new List<SqlValue[]>(rowCount);
        var newRowIds = new List<long>(rowCount);
        var updatedPositions = new List<int>();
        var returningRows = hasReturning ? new List<SqlValue[]>(rowCount) : null;
        var returningRowIds = hasReturning ? new List<long>(rowCount) : null;
        for (var index = 0; index < rowCount; index++)
        {
            newRows.Add(table.Rows[index]);
            newRowIds.Add(index < table.RowIds.Count ? table.RowIds[index] : index + 1);
        }

        var writeTarget = new VdbeWriteTarget
        {
            TableName = statement.TableName,
            RowCount = rowCount,
            GetRow = index => table.Rows[index],
            GetRowId = index => index < table.RowIds.Count ? table.RowIds[index] : index + 1,
            MutateRow = index =>
            {
                var original = table.Rows[index];
                var rowid = index < table.RowIds.Count ? table.RowIds[index] : index + 1;
                var (updated, newRowid) = BuildUpdatedRow(statement, table, plan, original, rowid, parameters, context);
                newRows[index] = updated;
                newRowIds[index] = newRowid;
                updatedPositions.Add(index);
                returningRows?.Add(updated);
                returningRowIds?.Add(newRowid);
                return new VdbeRowMutation(updated, newRowid);
            },
            Commit = () =>
            {
                CommitUpdates(
                    context,
                    statement.TableName,
                    table,
                    table.Rows,
                    newRows,
                    newRowIds,
                    plan,
                    updatedPositions);
                return (long?)null;
            },
        };

        compiled = hasReturning
            ? DmlStatementCompiler.CompileWithFilter(
                DmlKind.Update,
                statement.TableName,
                table.Columns.Length,
                filter,
                returningProgram!,
                writeTarget,
                new VdbeCursorSource(
                    returningRows!,
                    table.HasRowid ? returningRowIds : null))
            : DmlStatementCompiler.CompileWithFilter(
                DmlKind.Update,
                statement.TableName,
                table.Columns.Length,
                filter,
                Array.Empty<DmlReturningExpression>(),
                writeTarget);
        return true;
    }

    private bool TryCompileDelete(
        DeleteStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        out CompiledDml compiled,
        out string[] columns,
        out bool hasReturning)
    {
        compiled = null!;
        columns = [];
        hasReturning = false;

        if (statement.Limit is not null
            || statement.Offset is not null
            || statement.EffectiveOrderBy.Count != 0
            || context.CommonTableExpressions.Count != 0
            || IsSchemaTable(statement.TableName)
            || !context.Tables.TryGetValue(statement.TableName, out var table))
        {
            return false;
        }

        DmlRowFilter? filter = null;
        if (statement.Where is not null)
        {
            filter = CompileDmlRowPredicate(statement.Where, table, statement.TableName, parameters, context);
            if (filter is null)
                return false;
        }

        if (!TryCompileReturningClause(
                statement.Returning,
                table,
                statement.TableName,
                parameters,
                context,
                out var returningProgram,
                out columns,
                out hasReturning))
            return false;

        var rowCount = table.Rows.Count;
        var deleted = new bool[rowCount];
        var returningRows = hasReturning ? new List<SqlValue[]>(rowCount) : null;
        var returningRowIds = hasReturning ? new List<long>(rowCount) : null;
        var writeTarget = new VdbeWriteTarget
        {
            TableName = statement.TableName,
            RowCount = rowCount,
            GetRow = index => table.Rows[index],
            GetRowId = index => index < table.RowIds.Count ? table.RowIds[index] : index + 1,
            DeleteRow = index =>
            {
                deleted[index] = true;
                returningRows?.Add(table.Rows[index]);
                returningRowIds?.Add(index < table.RowIds.Count ? table.RowIds[index] : index + 1);
            },
            Commit = () =>
            {
                var keptRows = new List<SqlValue[]>(rowCount);
                var keptRowIds = new List<long>(rowCount);
                for (var index = 0; index < table.Rows.Count; index++)
                {
                    if (index < deleted.Length && deleted[index])
                        continue;
                    keptRows.Add(table.Rows[index]);
                    keptRowIds.Add(index < table.RowIds.Count ? table.RowIds[index] : index + 1);
                }

                table.Rows.Clear();
                table.Rows.AddRange(keptRows);
                table.RowIds.Clear();
                table.RowIds.AddRange(keptRowIds);
                return (long?)null;
            },
        };

        compiled = hasReturning
            ? DmlStatementCompiler.CompileWithFilter(
                DmlKind.Delete,
                statement.TableName,
                table.Columns.Length,
                filter,
                returningProgram!,
                writeTarget,
                new VdbeCursorSource(
                    returningRows!,
                    table.HasRowid ? returningRowIds : null))
            : DmlStatementCompiler.CompileWithFilter(
                DmlKind.Delete,
                statement.TableName,
                table.Columns.Length,
                filter,
                Array.Empty<DmlReturningExpression>(),
                writeTarget);
        return true;
    }

    // Reuses the SELECT expression emitter for RETURNING. The write loop first buffers every affected
    // row, then this block runs over that buffer in source order before Commit, preserving evaluator
    // predicate/assignment callback order while keeping projection failures statement-atomic.
    private bool TryCompileReturningClause(
        IReadOnlyList<Projection>? returning,
        EmbeddedTable table,
        string tableName,
        SqlValue[] parameters,
        QueryContext context,
        out DmlReturningProgram? program,
        out string[] columns,
        out bool hasReturning)
    {
        program = null;
        columns = [];
        hasReturning = false;
        if (returning is null)
            return true;

        var qualifiedColumns = BuildQualifiedColumns(tableName, table.Columns);
        var target = new ScanTarget(
            tableName,
            tableName,
            table.Columns,
            table.Rows,
            name => ResolveScanColumnIndex(name, table.Columns, qualifiedColumns),
            table.HasRowid ? table.RowIds : null);
        if (!SelectStatementCompiler.TryExpandProjections(returning, target, out var projections))
            return false;

        var outputColumns = BuildOutputColumns(tableName, table.Columns);
        columns = GetColumnNames(returning, outputColumns, outputColumns);
        if (columns.Length != projections.Count)
            return false;

        var instructions = new List<VdbeInstruction>();
        var emitter = new SelectStatementCompiler.ExpressionEmitter(
            target,
            new Cursor(1),
            projections.Count,
            instructions,
            IsConstantScalarExpression,
            expression => Evaluate(expression, parameters, null, context),
            function => TryGetRoutableBuiltinScalarCall(function, out var routable)
                ? BuildBuiltinScalarFunction(routable, parameters, context)
                : null,
            ArithmeticNumericAffinity,
            ModuloNumericAffinity);
        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (projection.ColumnIndex is { } columnIndex)
            {
                instructions.Add(new ColumnInstruction(new Cursor(1), columnIndex, new Register(index)));
            }
            else if (!emitter.TryEmit(projection.Expression!, new Register(index)))
            {
                return false;
            }
        }

        program = new DmlReturningProgram(
            instructions,
            projections.Count,
            emitter.RegisterCount,
            emitter.ParameterIndices);
        hasReturning = true;
        return true;
    }

    // Builds a per-row predicate for a compilable DML WHERE clause. The emitted filter
    // builds the same SourceRow shape the evaluated UPDATE/DELETE use (no qualified
    // columns), so column resolution matches exactly. A hidden-rowid reference becomes
    // a rowid-aware filter only for a rowid table; WITHOUT ROWID tables keep the evaluator's
    // diagnostic path.
    private DmlRowFilter? CompileDmlRowPredicate(
        Expression where,
        EmbeddedTable table,
        string tableName,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (!IsScanPredicate(where))
            return null;

        var qualifiedColumns = BuildQualifiedColumns(tableName, table.Columns);
        var scanTarget = new ScanTarget(
            tableName,
            tableName,
            table.Columns,
            table.Rows,
            name => ResolveScanColumnIndex(name, table.Columns, qualifiedColumns));
        if (!ReferencesUnbackedRowid(where, scanTarget))
        {
            return DmlRowFilter.ForRow(row => IsTrue(Evaluate(
                where,
                parameters,
                new SourceRow(table.Columns, row, RowIdQualifier: tableName),
                context)));
        }

        if (!table.HasRowid)
            return null;

        return DmlRowFilter.ForRowId((row, rowId) => IsTrue(Evaluate(
            where,
            parameters,
            new SourceRow(table.Columns, row, RowId: rowId, RowIdQualifier: tableName),
            context)));
    }

    // Only literal numeric arithmetic is safe to evaluate while compiling: it is deterministic
    // and cannot raise or invoke user code. Other row-independent expressions must execute at their
    // normal row position (when lowerable) or remain on the evaluator, so volatile functions run per
    // row and an erroring projection is not evaluated for an empty input.
    private bool IsConstantScalarExpression(Expression expression)
    {
        return expression switch
        {
            LiteralExpression => true,
            BinaryExpression binary when TryMapArithmeticOperator(binary.Operator, out _)
                => IsConstantScalarExpression(binary.Left) && IsConstantScalarExpression(binary.Right),
            CollationExpression collation => IsConstantScalarExpression(collation.Expression),
            _ => false,
        };
    }

    private ExecutionResult ExecuteExplain(
        ExplainStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        switch (statement.Inner)
        {
            case SelectStatement select
                when TryCompileSelect(select, parameters, context, outerRow: null, out var compiledSelect):
                return DescribeProgram(compiledSelect.Program);
            case CompoundSelectStatement compound
                when TryCompileCompoundSelect(compound, parameters, context, outerRow: null, out var compiledCompound):
                return DescribeProgram(compiledCompound.Program);
            case InsertStatement insert
                when CanRouteInsertThroughCompiler(insert, context)
                    && TryCompileInsert(insert, parameters, context, out var compiledInsert, out _, out _):
                return DescribeProgram(compiledInsert.Program);
            case UpdateStatement update
                when CanRouteUpdateThroughCompiler(update, context)
                    && TryCompileUpdate(update, parameters, context, out var compiledUpdate, out _, out _):
                return DescribeProgram(compiledUpdate.Program);
            case DeleteStatement delete
                when CanCompileDml(context)
                    && TryCompileDelete(delete, parameters, context, out var compiledDelete, out _, out _):
                return DescribeProgram(compiledDelete.Program);
            case ValuesClause values
                when TryCompileValues(values, out var compiledValues, out _):
                return DescribeProgram(compiledValues.Program);
            case WithSelectStatement with
                when TryBuildRecursiveCteExplainProgram(with, parameters, context, out var recursiveProgram):
                return DescribeProgram(recursiveProgram);
        }

        // No fake plan: EXPLAIN only reports programs that were genuinely lowered.
        throw new EmbeddedSqlException(
            "EXPLAIN is only supported for statements lowered to the bytecode compiler.");
    }

    internal static string[] ExplainColumns() => ["addr", "opcode", "p1", "p2", "p3", "p4", "comment"];

    private ExecutionResult ExecuteExplainQueryPlan(
        ExplainQueryPlanStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        var usesCompiledProgram = statement.Inner switch
        {
            SelectStatement select => !context.CancellationToken.CanBeCanceled
                && TryCompileSelect(select, parameters, context, outerRow: null, out _),
            CompoundSelectStatement compound => !context.CancellationToken.CanBeCanceled
                && TryCompileCompoundSelect(
                    compound,
                    parameters,
                    context,
                    outerRow: null,
                    out _),
            ValuesClause values => TryPrepareValuesLowering(values, out _),
            // WITH execution starts by materializing CTE inputs. Report the evaluator boundary
            // rather than evaluating those inputs merely to discover a later compiled phase.
            WithSelectStatement => false,
            InsertStatement insert => CanRouteInsertThroughCompiler(insert, context)
                && TryCompileInsert(insert, parameters, context, out _, out _, out _),
            UpdateStatement update => CanRouteUpdateThroughCompiler(update, context)
                && TryCompileUpdate(update, parameters, context, out _, out _, out _),
            DeleteStatement delete => CanCompileDml(context)
                && TryCompileDelete(delete, parameters, context, out _, out _, out _),
            QueryStatement or WithDmlStatement => false,
            _ => throw new EmbeddedSqlException(
                "EXPLAIN QUERY PLAN is only supported for queries and INSERT, UPDATE, or DELETE statements."),
        };

        var detail = usesCompiledProgram
            ? "MANAGED COMPILED VDBE"
            : "MANAGED EVALUATOR FALLBACK";
        return new ExecutionResult(
            ExplainQueryPlanColumns(),
            [
                [
                    SqlValue.Integer(0),
                    SqlValue.Integer(0),
                    SqlValue.Integer(0),
                    SqlValue.Text(detail),
                ],
            ],
            0);
    }

    internal static string[] ExplainQueryPlanColumns() => ["id", "parent", "notused", "detail"];

    private static ExecutionResult DescribeProgram(VdbeProgram program)
    {
        var rows = new List<SqlValue[]>(program.Instructions.Count);
        for (var address = 0; address < program.Instructions.Count; address++)
        {
            var instruction = program.Instructions[address];
            var (p1, p2, p3, p4, comment) = DescribeInstruction(instruction);
            rows.Add(
            [
                SqlValue.Integer(address),
                SqlValue.Text(instruction.Opcode.ToString()),
                SqlValue.Integer(p1),
                SqlValue.Integer(p2),
                SqlValue.Integer(p3),
                p4 is null ? SqlValue.Null : SqlValue.Text(p4),
                SqlValue.Text(comment),
            ]);
        }

        return new ExecutionResult(ExplainColumns(), rows, 0);
    }

    private static (long P1, long P2, long P3, string? P4, string Comment) DescribeInstruction(
        VdbeInstruction instruction)
    {
        return instruction switch
        {
            LoadConstantInstruction load => (
                load.Destination.Index,
                0,
                0,
                FormatExplainValue(load.Value),
                $"r[{load.Destination.Index}]={FormatExplainValue(load.Value)}"),
            LoadParameterInstruction loadParameter => (
                loadParameter.Destination.Index,
                loadParameter.Slot.Index,
                0,
                $"param[{loadParameter.Slot.Index}]",
                $"r[{loadParameter.Destination.Index}]=param[{loadParameter.Slot.Index}]"),
            CopyInstruction copy => (
                copy.Source.Index,
                copy.Destination.Index,
                0,
                null,
                $"r[{copy.Destination.Index}]=r[{copy.Source.Index}]"),
            // Mirrors VdbeExplain.Describe so a routed scalar-function projection renders byte-identically
            // to the canonical instruction renderer.
            FunctionInstruction function => (
                function.Destination.Index,
                function.Arguments.Start.Index,
                function.Arguments.Count,
                function.Function.Name,
                $"r[{function.Destination.Index}]={function.Function.Name}({FormatRegisterRange(function.Arguments)})"),
            // Shared with the executor's own describe path (like the recursive-worktable opcodes below), so a
            // routed arithmetic projection renders byte-identically to the canonical instruction renderer.
            ArithmeticInstruction => VdbeExplain.Describe(instruction),
            NumericAffinityInstruction => VdbeExplain.Describe(instruction),
            OpenReadCursorInstruction open => (
                open.Cursor.Index,
                0,
                open.ColumnCount,
                open.TableName,
                open.TableName is null
                    ? $"open read cursor {open.Cursor.Index}"
                    : $"open read cursor {open.Cursor.Index} on {open.TableName} ({open.ColumnCount} cols)"),
            OpenWriteCursorInstruction openWrite => (
                openWrite.Cursor.Index,
                0,
                openWrite.ColumnCount,
                openWrite.TableName,
                $"open write cursor {openWrite.Cursor.Index} on {openWrite.TableName} ({openWrite.ColumnCount} cols)"),
            CloseCursorInstruction close => (close.Cursor.Index, 0, 0, null, $"close cursor {close.Cursor.Index}"),
            RewindCursorInstruction rewind => (
                rewind.Cursor.Index,
                rewind.EmptyTarget.Offset,
                0,
                null,
                $"rewind cursor {rewind.Cursor.Index}, goto {rewind.EmptyTarget.Offset} if empty"),
            ColumnInstruction column => (
                column.Cursor.Index,
                column.ColumnIndex,
                column.Destination.Index,
                null,
                $"r[{column.Destination.Index}]=c{column.Cursor.Index}.col[{column.ColumnIndex}]"),
            RowIdInstruction rowId => (
                rowId.Cursor.Index,
                rowId.Destination.Index,
                0,
                null,
                $"r[{rowId.Destination.Index}]=c{rowId.Cursor.Index}.rowid"),
            FilterInstruction filter => (
                filter.Cursor.Index,
                filter.FalseTarget.Offset,
                0,
                null,
                filter.Description),
            FilterRowIdInstruction => VdbeExplain.Describe(instruction),
            FilterRegistersInstruction filterRegisters => (
                filterRegisters.Row.Start.Index,
                filterRegisters.FalseTarget.Offset,
                filterRegisters.Row.Count,
                null,
                filterRegisters.Description),
            NextInstruction next => (
                next.Cursor.Index,
                next.LoopTarget.Offset,
                0,
                null,
                $"next cursor {next.Cursor.Index}, goto {next.LoopTarget.Offset} if more rows"),
            OpenSorterInstruction openSorter => (
                openSorter.Sorter.Index,
                0,
                openSorter.ColumnCount,
                null,
                $"open sorter {openSorter.Sorter.Index} ({openSorter.ColumnCount} cols)"),
            SorterInsertInstruction sorterInsert => (
                sorterInsert.Sorter.Index,
                sorterInsert.Record.Start.Index,
                sorterInsert.Record.Count,
                null,
                $"sorter {sorterInsert.Sorter.Index} insert {FormatRegisterRange(sorterInsert.Record)}"),
            SorterSortInstruction sorterSort => (
                sorterSort.Sorter.Index,
                sorterSort.EmptyTarget.Offset,
                0,
                null,
                $"sort sorter {sorterSort.Sorter.Index}, goto {sorterSort.EmptyTarget.Offset} if empty"),
            SorterDataInstruction sorterData => (
                sorterData.Sorter.Index,
                sorterData.Destination.Start.Index,
                sorterData.Destination.Count,
                null,
                $"{FormatRegisterRange(sorterData.Destination)}=sorter {sorterData.Sorter.Index} data"),
            SorterNextInstruction sorterNext => (
                sorterNext.Sorter.Index,
                sorterNext.LoopTarget.Offset,
                0,
                null,
                $"next sorter {sorterNext.Sorter.Index}, goto {sorterNext.LoopTarget.Offset} if more rows"),
            CloseSorterInstruction closeSorter => (
                closeSorter.Sorter.Index,
                0,
                0,
                null,
                $"close sorter {closeSorter.Sorter.Index}"),
            ResultRowInstruction result => (
                result.Values.Start.Index,
                result.Values.Count,
                0,
                null,
                DescribeResultRow(result.Values)),
            DistinctResultRowInstruction distinct => (
                distinct.Values.Start.Index,
                distinct.Values.Count,
                distinct.DistinctSetIndex,
                null,
                $"{DescribeResultRow(distinct.Values)} if new to distinct set {distinct.DistinctSetIndex}"),
            YieldInstruction => (0, 0, 0, null, "yield"),
            DeleteInstruction delete => (
                delete.Cursor.Index,
                0,
                0,
                null,
                $"delete current row of cursor {delete.Cursor.Index}"),
            InsertInstruction insert => (
                insert.Cursor.Index,
                0,
                0,
                null,
                $"insert row into cursor {insert.Cursor.Index}"),
            UpdateInstruction update => (
                update.Cursor.Index,
                0,
                0,
                null,
                $"update current row of cursor {update.Cursor.Index}"),
            CommitInstruction commit => (
                commit.Cursor.Index,
                0,
                0,
                null,
                $"commit mutations of cursor {commit.Cursor.Index}"),
            GotoInstruction gotoInstruction => (
                0,
                gotoInstruction.Target.Offset,
                0,
                null,
                $"goto {gotoInstruction.Target.Offset}"),
            JumpIfInstruction jumpIf => (
                jumpIf.Register.Index,
                jumpIf.Target.Offset,
                0,
                null,
                $"goto {jumpIf.Target.Offset} if r[{jumpIf.Register.Index}]"),
            AggResetInstruction aggReset => (
                aggReset.Accumulator.Index,
                0,
                0,
                null,
                $"reset accumulator {aggReset.Accumulator.Index}"),
            AggStepInstruction aggStep => (
                aggStep.Accumulator.Index,
                aggStep.Arguments.Start.Index,
                aggStep.Arguments.Count,
                aggStep.Aggregate.Name,
                $"accumulator {aggStep.Accumulator.Index}={aggStep.Aggregate.Name} step {FormatRegisterRange(aggStep.Arguments)}"),
            AggFinalizeInstruction aggFinalize => (
                aggFinalize.Accumulator.Index,
                aggFinalize.Destination.Index,
                0,
                aggFinalize.Aggregate.Name,
                $"r[{aggFinalize.Destination.Index}]={aggFinalize.Aggregate.Name} finalize accumulator {aggFinalize.Accumulator.Index}"),
            SameGroupInstruction sameGroup => (
                sameGroup.CurrentKey.Start.Index,
                sameGroup.SameGroupTarget.Offset,
                sameGroup.SavedKey.Start.Index,
                null,
                $"goto {sameGroup.SameGroupTarget.Offset} if group {FormatRegisterRange(sameGroup.CurrentKey)}=={FormatRegisterRange(sameGroup.SavedKey)}"),
            RowSetInsertInstruction rowSetInsert => (
                rowSetInsert.Values.Start.Index,
                rowSetInsert.Values.Count,
                rowSetInsert.RowSetIndex,
                null,
                $"insert {FormatRegisterRange(rowSetInsert.Values)} into row set {rowSetInsert.RowSetIndex}"),
            CompoundResultRowInstruction compound => (
                compound.Values.Start.Index,
                compound.Values.Count,
                compound.OutputSetIndex,
                FormatSetList(compound.MembershipSetIndices),
                $"{DescribeResultRow(compound.Values)} if new to distinct set {compound.OutputSetIndex} and {FormatMembership(compound.Mode)} {FormatSetList(compound.MembershipSetIndices)}"),
            OffsetGateInstruction offsetGate => (
                offsetGate.Counter.Index,
                offsetGate.SkipTarget.Offset,
                0,
                null,
                $"goto {offsetGate.SkipTarget.Offset} and decrement r[{offsetGate.Counter.Index}] while r[{offsetGate.Counter.Index}]>0"),
            LimitGateInstruction limitGate => (
                limitGate.Counter.Index,
                limitGate.DoneTarget.Offset,
                0,
                null,
                $"goto {limitGate.DoneTarget.Offset} when r[{limitGate.Counter.Index}]<=0, else decrement r[{limitGate.Counter.Index}]"),
            HaltInstruction => (0, 0, 0, null, "halt"),
            // The recursive-worktable opcodes are shared with the executor's own describe path, so reuse
            // its canonical rendering here rather than duplicating it and risking drift.
            OpenWorkTableInstruction => VdbeExplain.Describe(instruction),
            SeedWorkTableInstruction => VdbeExplain.Describe(instruction),
            WorkTableStepInstruction => VdbeExplain.Describe(instruction),
            WorkTableExpandInstruction => VdbeExplain.Describe(instruction),
            CloseWorkTableInstruction => VdbeExplain.Describe(instruction),
            _ => throw new EmbeddedSqlException($"Cannot describe unsupported opcode {instruction.Opcode}."),
        };
    }

    private static string DescribeResultRow(RegisterRange range)
    {
        if (range.Count == 0)
            return "output=r[]";

        var start = range.Start.Index;
        return range.Count == 1
            ? $"output=r[{start}]"
            : $"output=r[{start}..{start + range.Count - 1}]";
    }

    // Mirrors VdbeExplain.FormatMembership so the SQL EXPLAIN comment for a routed set operation is
    // byte-identical to the canonical instruction renderer.
    private static string FormatMembership(CompoundMembershipMode mode) => mode switch
    {
        CompoundMembershipMode.PresentInAll => "present in all of",
        CompoundMembershipMode.AbsentFromAll => "absent from all of",
        _ => throw new EmbeddedSqlException($"Unknown compound membership mode {mode}."),
    };

    // Mirrors VdbeExplain.FormatSetList so the SQL EXPLAIN p4/comment for a routed set operation is
    // byte-identical to the canonical instruction renderer.
    private static string FormatSetList(IReadOnlyList<int> setIndices)
    {
        if (setIndices is null || setIndices.Count == 0)
            return "sets {}";

        return $"sets {{{string.Join(",", setIndices)}}}";
    }

    private static string FormatRegisterRange(RegisterRange range)
    {
        if (range.Count == 0)
            return "r[]";

        var start = range.Start.Index;
        return range.Count == 1
            ? $"r[{start}]"
            : $"r[{start}..{start + range.Count - 1}]";
    }

    private static string FormatExplainValue(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => "NULL",
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Text => $"'{value.AsText()}'",
            SqlValueKind.Blob => FormatExplainBlob(value.AsBlob().Span),
            _ => throw new EmbeddedSqlException($"Unknown SQL value kind {value.Kind}."),
        };
    }

    private static string FormatExplainBlob(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2 + 3);
        builder.Append("x'");
        foreach (var b in bytes)
            builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        builder.Append('\'');
        return builder.ToString();
    }

    private ExecutionResult ExecuteSelect(
        SelectStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        var resolvedOrderBy = ResolveOrderBy(statement.OrderBy, statement.Projections);
        ValidateOrderByCollations(resolvedOrderBy);
        var windowFunctions = CollectSelectWindowFunctions(statement);
        foreach (var function in windowFunctions)
            ValidateOrderByCollations(function.Window!.OrderBy);
        var limit = statement.Limit is null
            ? (long?)null
            : RequireLimitInteger(Evaluate(statement.Limit, parameters, outerRow, context));
        var offset = statement.Offset is null
            ? 0
            : Math.Max(0, RequireLimitInteger(Evaluate(statement.Offset, parameters, outerRow, context)));
        var hasAggregate = statement.Projections.Any(projection => ContainsAggregate(projection.Expression))
            || statement.Having is not null && ContainsAggregate(statement.Having)
            || statement.OrderBy.Any(term => ContainsAggregate(term.Expression));
        var hasWindow = windowFunctions.Count > 0;
        if (statement.Where is not null && ContainsWindowFunction(statement.Where))
            throw new EmbeddedSqlException("misuse of window function in WHERE clause");
        if (statement.GroupBy.Any(ContainsWindowFunction))
            throw new EmbeddedSqlException("misuse of window function in GROUP BY clause");
        if (statement.Having is not null && ContainsWindowFunction(statement.Having))
            throw new EmbeddedSqlException("misuse of window function in HAVING clause");
        var sourceColumns = GetSourceColumns(statement.Source, context);
        var outputColumns = GetOutputColumns(statement.Source, context);
        var rawOutputColumns = GetRawOutputColumns(statement.Source, context);
        if (limit == 0)
        {
            ValidateLimitZeroExpressions(statement, sourceColumns, context, outerRow);
            return new ExecutionResult(GetColumnNames(statement.Projections, outputColumns, rawOutputColumns), [], 0);
        }

        var sourceLimit = statement.Where is null
            && !hasAggregate
            && !hasWindow
            && statement.GroupBy.Count == 0
            && statement.OrderBy.Count == 0
            && !statement.Distinct
            && offset == 0
            && limit is >= 0
            ? limit
            : null;
        var source = GetSourceRows(statement.Source, parameters, context, sourceLimit, outerRow);
        var selectedRows = new List<SourceRow>();
        foreach (var row in source.Rows)
        {
            if (statement.Where is null || IsTrue(Evaluate(statement.Where, parameters, row, context)))
                selectedRows.Add(row);
        }

        var columnNames = GetColumnNames(statement.Projections, outputColumns, rawOutputColumns);
        if (hasWindow)
        {
            if (hasAggregate || statement.GroupBy.Count > 0)
                throw new EmbeddedSqlException("window functions cannot be combined with aggregates or GROUP BY");

            return ExecuteWindowSelect(
                statement,
                selectedRows,
                windowFunctions,
                columnNames,
                outputColumns,
                offset,
                limit,
                parameters,
                context);
        }

        if (hasAggregate || statement.GroupBy.Count > 0)
        {
            if (statement.GroupBy.Count == 0)
            {
                var representative = GetAggregateRepresentative(statement, selectedRows, parameters, context);
                if (statement.Having is not null
                && !IsTrue(EvaluateAggregate(
                    statement.Having,
                    selectedRows,
                    parameters,
                    context,
                    representative)))
                {
                    return new ExecutionResult(columnNames, [], 0);
                }

                var aggregateValues = statement.Projections
                .Select(projection => EvaluateAggregate(
                    projection.Expression,
                    selectedRows,
                    parameters,
                    context,
                    representative))
                .ToArray();
                return new ExecutionResult(
                    columnNames,
                    ApplyDistinctLimit(
                        [aggregateValues],
                        statement.Distinct,
                        offset,
                        limit,
                        statement.Projections.Select(projection => GetCollation(projection.Expression)).ToArray()),
                    0);
            }

            var groups = new Dictionary<string, List<SourceRow>>(StringComparer.Ordinal);
            foreach (var row in selectedRows)
            {
                var key = GetGroupKey(statement.GroupBy, parameters, row, context);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = [];
                    groups.Add(key, group);
                }

                group.Add(row);
            }

            var groupedRows = groups.Values.Select(group =>
            {
                var representative = GetAggregateRepresentative(statement, group, parameters, context)
                    ?? group[0];
                return new GroupedResult(
                    representative,
                    group,
                    statement.Projections
                        .Select(projection => ContainsAggregate(projection.Expression)
                            ? EvaluateAggregate(
                                projection.Expression,
                                group,
                                parameters,
                                context,
                                representative)
                            : Evaluate(projection.Expression, parameters, representative, context))
                        .ToArray());
            }).ToList();
            if (statement.Having is not null)
            {
                groupedRows.RemoveAll(group => !IsTrue(
                    hasAggregate
                        ? EvaluateAggregate(
                            statement.Having,
                            group.Rows,
                            parameters,
                            context,
                            group.Representative)
                        : Evaluate(statement.Having, parameters, group.Representative, context)));
            }
            if (statement.OrderBy.Count > 0)
            {
                groupedRows.Sort((left, right) =>
                    CompareGroupedRows(left, right, resolvedOrderBy, parameters, context));
            }
            return new ExecutionResult(
                columnNames,
                ApplyDistinctLimit(
                    groupedRows.Select(group => group.Values),
                    statement.Distinct,
                    offset,
                    limit,
                    statement.Projections.Select(projection => GetCollation(projection.Expression)).ToArray()),
                0);
        }

        if (statement.OrderBy.Count > 0)
        {
            selectedRows.Sort((left, right) =>
                CompareRows(left, right, resolvedOrderBy, parameters, context));
        }

        var resultRows = new List<SqlValue[]>();
        foreach (var row in selectedRows)
        {
            var values = new List<SqlValue>();
            foreach (var projection in statement.Projections)
            {
                switch (projection.Expression)
                {
                    case StarExpression:
                        if (row is null)
                            throw new EmbeddedSqlException("SELECT * requires a row source");

                        foreach (var column in outputColumns)
                            values.Add(GetOutputValue(row, column));
                        break;
                    case QualifiedStarExpression qualifiedStar:
                        if (row is null)
                            throw new EmbeddedSqlException("SELECT * requires a row source");

                        var rawMatches = GetRawOutputColumns(statement.Source, context)
                            .Where(column => string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        if (rawMatches.Length == 0)
                            throw new EmbeddedSqlException($"no such table: {qualifiedStar.Qualifier}");

                        var matches = rawMatches
                            .Select(raw => outputColumns.FirstOrDefault(column =>
                                string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase)
                                && column.Index == raw.Index) ?? raw)
                            .ToArray();
                        foreach (var column in matches)
                            values.Add(GetOutputValue(row, column));
                        break;
                    default:
                        values.Add(Evaluate(projection.Expression, parameters, row, context));
                        break;
                }
            }

            resultRows.Add(values.ToArray());
        }

        return new ExecutionResult(
            columnNames,
            ApplyDistinctLimit(
                resultRows,
                statement.Distinct,
                offset,
                limit,
                GetDistinctProjectionCollations(
                    statement.Projections,
                    outputColumns,
                    rawOutputColumns,
                    statement.Source,
                    context)),
            0);
    }

    private static IReadOnlyList<string?> GetDistinctProjectionCollations(
        IReadOnlyList<Projection> projections,
        IReadOnlyList<OutputColumn> outputColumns,
        IReadOnlyList<OutputColumn> rawOutputColumns,
        TableSource? source,
        QueryContext context)
    {
        var collations = new List<string?>();
        foreach (var projection in projections)
        {
            switch (projection.Expression)
            {
                case StarExpression:
                    collations.AddRange(outputColumns.Select(column =>
                        GetDeclaredOutputColumnCollation(source, column, context)));
                    break;
                case QualifiedStarExpression qualifiedStar:
                    foreach (var raw in rawOutputColumns.Where(raw =>
                                 string.Equals(raw.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase)))
                    {
                        var output = outputColumns.FirstOrDefault(column =>
                            string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase)
                            && column.Index == raw.Index) ?? raw;
                        collations.Add(GetDeclaredOutputColumnCollation(source, output, context));
                    }

                    break;
                case ColumnExpression column:
                    collations.Add(GetDeclaredColumnCollation(source, column.Name, context));
                    break;
                default:
                    collations.Add(GetCollation(projection.Expression));
                    break;
            }
        }

        return collations;
    }

    private static string? GetDeclaredColumnCollation(
        TableSource? source,
        string columnName,
        QueryContext context)
    {
        var separator = columnName.IndexOf('.');
        var qualifier = separator < 0 ? null : columnName[..separator];
        var name = separator < 0 ? columnName : columnName[(separator + 1)..];
        var column = GetRawOutputColumns(source, context).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
            && (qualifier is null
                || string.Equals(candidate.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase)));
        return column is null
            ? null
            : GetDeclaredOutputColumnCollation(source, column, context);
    }

    private static string? GetDeclaredOutputColumnCollation(
        TableSource? source,
        OutputColumn column,
        QueryContext context)
    {
        switch (source)
        {
            case NamedTableSource named when context.CommonTableExpressions.TryGetValue(named.Name, out var commonTableExpression):
                {
                    var qualifier = named.Alias ?? named.Name;
                    if (!string.Equals(column.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
                        return null;

                    return column.Index < commonTableExpression.Columns.Length
                        ? commonTableExpression.Collations?.ElementAtOrDefault(column.Index)
                        : null;
                }
            case NamedTableSource named when context.Tables.TryGetValue(named.Name, out var table):
                {
                    var qualifier = named.Alias ?? named.Name;
                    if (!string.Equals(column.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
                        return null;

                    for (var index = 0; index < table.Columns.Length; index++)
                    {
                        if (string.Equals(table.Columns[index], column.Name, StringComparison.OrdinalIgnoreCase))
                            return table.ColumnDefinitions[index].Collation;
                    }

                    return null;
                }
            case NamedTableSource named when TryGetView(context, named.Name, out var view):
                {
                    var qualifier = named.Alias ?? view.Name;
                    if (!string.Equals(column.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
                        return null;

                    var viewContext = EnterView(context, view.Name);
                    var columns = ResolveViewColumns(view, viewContext);
                    return column.Index >= columns.Length
                        ? null
                        : GetQueryOutputCollations(view.Query, viewContext).ElementAtOrDefault(column.Index);
                }
            case DerivedTableSource derived:
                {
                    if (!string.Equals(column.Qualifier, derived.Alias, StringComparison.OrdinalIgnoreCase))
                        return null;

                    var columns = DescribeQuery(derived.Query, context);
                    return column.Index >= columns.Length
                        ? null
                        : GetQueryOutputCollations(derived.Query, context).ElementAtOrDefault(column.Index);
                }
            case JoinTableSource join:
                {
                    var leftWidth = GetSourceColumns(join.Left, context).Length;
                    return column.Index < leftWidth
                        ? GetDeclaredOutputColumnCollation(join.Left, column, context)
                        : GetDeclaredOutputColumnCollation(
                            join.Right,
                            column with { Index = column.Index - leftWidth },
                            context);
                }
            default:
                return null;
        }
    }

    private static IReadOnlyList<string?> GetQueryOutputCollations(
        QueryStatement query,
        QueryContext context)
    {
        return query switch
        {
            SelectStatement select => GetDistinctProjectionCollations(
                select.Projections,
                GetOutputColumns(select.Source, context),
                GetRawOutputColumns(select.Source, context),
                select.Source,
                context),
            ValuesClause values when values.Rows.Count > 0
                => values.Rows[0].Select(GetCollation).ToArray(),
            CompoundSelectStatement compound when compound.Terms.Count > 0
                => GetQueryOutputCollations(compound.Terms[0], context),
            WithSelectStatement with => GetWithQueryOutputCollations(with, context),
            _ => [],
        };
    }

    private static IReadOnlyList<string?> GetWithQueryOutputCollations(
        WithSelectStatement statement,
        QueryContext context)
    {
        var commonTableExpressions = new Dictionary<string, SourceData>(
            context.CommonTableExpressions,
            StringComparer.OrdinalIgnoreCase);
        var namesInCurrentClause = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var commonTableExpression in statement.CommonTableExpressions)
        {
            if (!namesInCurrentClause.Add(commonTableExpression.Name))
                throw new EmbeddedSqlException($"duplicate WITH table name: {commonTableExpression.Name}");

            var cteContext = context with { CommonTableExpressions = commonTableExpressions };
            var columns = ResolveCommonTableExpressionColumns(
                commonTableExpression,
                DescribeQuery(commonTableExpression.Query, cteContext));
            commonTableExpressions[commonTableExpression.Name] = new SourceData(
                columns,
                [],
                GetQueryOutputCollations(commonTableExpression.Query, cteContext));
        }

        return GetQueryOutputCollations(
            statement.Query,
            context with { CommonTableExpressions = commonTableExpressions });
    }

    private void ValidateLimitZeroExpressions(
        SelectStatement statement,
        IReadOnlyList<string> sourceColumns,
        QueryContext context,
        SourceRow? outerRow)
    {
        var qualifiedColumns = statement.Source is null
            ? null
            : GetQualifiedColumns(statement.Source, context);
        var row = new SourceRow(
            sourceColumns.ToArray(),
            Enumerable.Repeat(SqlValue.Null, sourceColumns.Count).ToArray(),
            qualifiedColumns,
            outerRow);

        foreach (var projection in statement.Projections)
            ValidateColumnReferences(projection.Expression, row);
        ValidateColumnReferences(statement.Where, row);
        foreach (var expression in statement.GroupBy)
            ValidateColumnReferences(expression, row);
        ValidateColumnReferences(statement.Having, row);
        foreach (var orderBy in statement.OrderBy)
            ValidateColumnReferences(orderBy.Expression, row);
    }

    private static void ValidateColumnReferences(Expression? expression, SourceRow row)
    {
        if (expression is null)
            return;

        switch (expression)
        {
            case ColumnExpression column:
                row.GetValue(column);
                return;
            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                    ValidateColumnReferences(argument, row);
                ValidateColumnReferences(function.Filter, row);
                if (function.Window is not null)
                {
                    foreach (var partition in function.Window.PartitionBy)
                        ValidateColumnReferences(partition, row);
                    foreach (var orderBy in function.Window.OrderBy)
                        ValidateColumnReferences(orderBy.Expression, row);
                }

                return;
            case CollationExpression collation:
                ValidateColumnReferences(collation.Expression, row);
                return;
            case CastExpression cast:
                ValidateColumnReferences(cast.Expression, row);
                return;
            case CaseExpression @case:
                ValidateColumnReferences(@case.Operand, row);
                foreach (var clause in @case.Clauses)
                {
                    ValidateColumnReferences(clause.When, row);
                    ValidateColumnReferences(clause.Then, row);
                }
                ValidateColumnReferences(@case.Else, row);
                return;
            case LikeExpression like:
                ValidateColumnReferences(like.Value, row);
                ValidateColumnReferences(like.Pattern, row);
                ValidateColumnReferences(like.Escape, row);
                return;
            case GlobExpression glob:
                ValidateColumnReferences(glob.Value, row);
                ValidateColumnReferences(glob.Pattern, row);
                return;
            case InExpression @in:
                ValidateColumnReferences(@in.Value, row);
                foreach (var value in @in.Values)
                    ValidateColumnReferences(value, row);
                return;
            case InSubqueryExpression inSubquery:
                ValidateColumnReferences(inSubquery.Value, row);
                return;
            case BetweenExpression between:
                ValidateColumnReferences(between.Value, row);
                ValidateColumnReferences(between.Lower, row);
                ValidateColumnReferences(between.Upper, row);
                return;
            case UnaryExpression unary:
                ValidateColumnReferences(unary.Operand, row);
                return;
            case BinaryExpression binary:
                ValidateColumnReferences(binary.Left, row);
                ValidateColumnReferences(binary.Right, row);
                return;
            case LiteralExpression or ParameterExpression or StarExpression or QualifiedStarExpression or ScalarSubqueryExpression or ExistsExpression:
                return;
            default:
                throw new EmbeddedSqlException($"Unsupported expression type {expression.GetType().Name}.");
        }
    }

    private ExecutionResult ExecuteCompoundSelect(
        CompoundSelectStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        ValidateCompoundOrderByCollations(statement, context);

        // Route the supported same-operator UNION / UNION ALL subset entirely through the bytecode
        // compiler, running the sequenced program as a real execution path. Its result columns are
        // the first term's, exactly as the tree-walking fold below reports first.Columns. Everything
        // else (ORDER BY/LIMIT/OFFSET, INTERSECT/EXCEPT, mixed operators, non-lowerable terms) keeps
        // the evaluator.
        if (!context.CancellationToken.CanBeCanceled
            && TryCompileCompoundSelect(statement, parameters, context, outerRow, out var compiledCompound))
        {
            var firstTerm = (SelectStatement)statement.Terms[0];
            var compoundColumns = GetColumnNames(
                firstTerm.Projections,
                GetOutputColumns(firstTerm.Source, context),
                GetRawOutputColumns(firstTerm.Source, context));
            return RunCompiledProgram(compiledCompound, compoundColumns);
        }

        var first = ExecuteCompoundTerm(statement.Terms[0], parameters, context, outerRow);
        var rows = first.Rows.Select(row => row.ToArray()).ToList();
        var collations = GetCompoundCollations(statement.Terms[0], first.Columns.Length);
        for (var index = 1; index < statement.Terms.Count; index++)
        {
            var next = ExecuteCompoundTerm(statement.Terms[index], parameters, context, outerRow);
            if (next.Columns.Length != first.Columns.Length)
                throw new EmbeddedSqlException("SELECTs to the left and right of a compound operator do not have the same number of result columns");

            rows = statement.Operators[index - 1] switch
            {
                CompoundOperator.Union => ApplyUnion(rows, next.Rows, collations),
                CompoundOperator.UnionAll => [.. rows, .. next.Rows.Select(row => row.ToArray())],
                CompoundOperator.Intersect => ApplyIntersect(rows, next.Rows, collations),
                CompoundOperator.Except => ApplyExcept(rows, next.Rows, collations),
                _ => throw new EmbeddedSqlException($"Unsupported compound operator {statement.Operators[index - 1]}."),
            };
        }

        if (statement.OrderBy.Count > 0)
            SortCompoundRows(rows, statement, first.Columns, context);

        var limit = statement.Limit is null
            ? (long?)null
            : RequireLimitInteger(Evaluate(statement.Limit, parameters, outerRow, context));
        var offset = statement.Offset is null
            ? 0
            : Math.Max(0, RequireLimitInteger(Evaluate(statement.Offset, parameters, outerRow, context)));
        return new ExecutionResult(first.Columns, ApplyDistinctLimit(rows, distinct: false, offset, limit), 0);
    }

    // Runs a single compound-select term. SELECT keeps the tree-walking evaluator so
    // compound semantics stay identical; VALUES uses the row-set evaluator.
    private ExecutionResult ExecuteCompoundTerm(
        QueryStatement term,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        return term switch
        {
            SelectStatement select => ExecuteSelect(select, parameters, context, outerRow),
            ValuesClause values => ExecuteValues(values, parameters, context, outerRow),
            _ => ExecuteQuery(term, parameters, context, outerRow),
        };
    }

    // Per-output-column collations for a compound; only SELECT terms carry projection
    // collations, so VALUES (and other) terms contribute no collation.
    private IReadOnlyList<string?> GetCompoundCollations(QueryStatement firstTerm, int columnCount)
    {
        if (firstTerm is SelectStatement select)
            return select.Projections.Select(projection => GetCollation(projection.Expression)).ToArray();

        return Enumerable.Repeat((string?)null, columnCount).ToArray();
    }

    // Routes a top-level source-less VALUES through the bytecode compiler when every cell is a
    // baked literal or a bare bind parameter, running the emitted ValuesProgramBuilder program as a
    // real execution path. Constant cells emit LoadConstant; parameter cells emit LoadParameter and
    // read their value from a late-bound VdbeParameterBinding assembled from the supplied parameter
    // array, so the same compiled program re-executes with fresh parameters without being rebuilt.
    // Its columns are named column1..columnN exactly as the tree-walking evaluator names them.
    // Everything the builder cannot reproduce byte-for-byte keeps the evaluator (see
    // TryCompileValues). Only the top-level statement reaches this wrapper; a VALUES used as a
    // derived table, a scalar/IN/EXISTS subquery, a CTE body, or a compound term still runs on the
    // evaluator via ExecuteQuery/ExecuteCompoundTerm, mirroring how compound SELECT terms stay on
    // ExecuteSelect rather than the routed ExecuteSelectStatement.
    private ExecutionResult ExecuteValuesStatement(
        ValuesClause values,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        if (TryPrepareValuesLowering(values, out var lowering))
            return ExecutePreparedValues(lowering, parameters);

        return ExecuteValues(values, parameters, context, outerRow);
    }

    // A cacheable, reusable lowering of an eligible top-level VALUES statement: the immutable compiled
    // program, its dense SQL-index-to-slot map (empty for a constant-only VALUES), and the generated
    // column-name template. A prepared statement builds this once and reuses it across every Reset/rebind,
    // so a routed VALUES no longer recompiles per Execute. Only immutable state is retained -- the
    // VdbeProgram is frozen, the slot map is read-only, and the column template is cloned per execution --
    // so nothing mutable is shared across executions. The lowering depends solely on the immutable parse
    // tree (not on bound values, which are late-bound, nor on the schema catalog, which a VALUES never
    // references), so it stays valid for the life of the prepared statement and never needs invalidation.
    internal sealed class PreparedValuesLowering
    {
        public PreparedValuesLowering(
            CompiledSelect compiled,
            IReadOnlyList<int> slotParameterIndices,
            string[] columns)
        {
            Compiled = compiled;
            SlotParameterIndices = slotParameterIndices;
            Columns = columns;
        }

        public CompiledSelect Compiled { get; }

        public IReadOnlyList<int> SlotParameterIndices { get; }

        public string[] Columns { get; }

        public VdbeProgram Program => Compiled.Program;
    }

    // Attempts to lower an eligible top-level VALUES into a PreparedValuesLowering. Returns false for a
    // fallback shape (any computed cell), which is never cached and keeps the evaluator. The unequal-width
    // diagnostic TryCompileValues raises propagates unchanged, so a width error surfaces at execution time
    // exactly as the evaluator raises it (callers resolve the lowering lazily on first execution).
    internal static bool TryPrepareValuesLowering(ValuesClause values, out PreparedValuesLowering lowering)
    {
        lowering = null!;
        if (!TryCompileValues(values, out var compiled, out var slotParameterIndices))
            return false;

        lowering = new PreparedValuesLowering(compiled, slotParameterIndices, DescribeValues(values));
        return true;
    }

    // Runs a prepared VALUES lowering against the current parameter values: it builds a fresh late-bound
    // binding for the declared slots (so a missing/out-of-range index raises the exact diagnostic the
    // evaluator raises) and executes the reused immutable program in a fresh runtime. The cached column
    // template is cloned per execution so no ExecutionResult shares a mutable array with the cache.
    internal static ExecutionResult ExecutePreparedValues(
        PreparedValuesLowering lowering,
        SqlValue[] parameters)
    {
        var binding = BuildValuesBinding(lowering.SlotParameterIndices, parameters);
        return RunCompiledProgram(lowering.Compiled, (string[])lowering.Columns.Clone(), binding);
    }

    // Lowers a VALUES row list into a ValuesProgramBuilder program, or returns false so the
    // evaluator keeps ownership of every shape the builder cannot lower exactly.
    //
    // Routed through the VDBE (return true) when every cell in every row is either:
    //  - a LiteralExpression, emitted as a baked LoadConstant, or
    //  - a ParameterExpression (SQL ?, ?NNN, or a named :/@/$ placeholder), emitted as a late-bound
    //    LoadParameter. Each distinct SQL parameter index is mapped to a dense zero-based slot in
    //    first-appearance (row-major) order via slotParameterIndices, so slot width stays minimal
    //    and duplicate placeholders -- the same ?NNN number or the same named identity, which the
    //    parser already collapses to one ParameterExpression.Index -- share a slot and therefore a
    //    value, preserving SQLite parameter identity. The generated column names (column1..columnN)
    //    come from DescribeValues, matching the evaluator's own metadata.
    //
    // Deliberately kept on the evaluator (return false):
    //  - any computed cell (arithmetic, function, CAST, CASE, subquery, column reference, ...): its
    //    value, folding, and error timing (e.g. VALUES (1/0), or a correlated column) stay with the
    //    evaluator, so a mixed row with even one computed cell is never lowered.
    //
    // Builder validation maps onto the evaluator's exact diagnostic: a parsed statement always has
    // at least one row and at least one term per row, so an unequal-width literal/parameter VALUES
    // is the only ArgumentException the builder can raise, and it surfaces as the identical
    // "all VALUES must have the same number of terms" the evaluator raises.
    //
    // slotParameterIndices maps each declared parameter slot to the 1-based SQL parameter index whose
    // value binds it; it is empty for a constant-only VALUES (which declares no slots).
    private static bool TryCompileValues(
        ValuesClause values,
        out CompiledSelect compiled,
        out IReadOnlyList<int> slotParameterIndices)
    {
        compiled = null!;
        slotParameterIndices = [];

        var parameterSlots = new Dictionary<int, int>();
        var slotToParameter = new List<int>();
        var cellRows = new List<IReadOnlyList<ValuesCell>>(values.Rows.Count);
        foreach (var row in values.Rows)
        {
            var cells = new ValuesCell[row.Count];
            for (var index = 0; index < row.Count; index++)
            {
                switch (row[index])
                {
                    case LiteralExpression literal:
                        cells[index] = ValuesCell.Constant(literal.Value);
                        break;
                    case ParameterExpression parameter:
                        if (!parameterSlots.TryGetValue(parameter.Index, out var slot))
                        {
                            slot = slotToParameter.Count;
                            parameterSlots.Add(parameter.Index, slot);
                            slotToParameter.Add(parameter.Index);
                        }

                        cells[index] = ValuesCell.Parameter(slot);
                        break;
                    default:
                        return false;
                }
            }

            cellRows.Add(cells);
        }

        VdbeProgram program;
        try
        {
            program = ValuesProgramBuilder.BuildCells(cellRows);
        }
        catch (ArgumentException exception)
        {
            throw new EmbeddedSqlException("all VALUES must have the same number of terms", exception);
        }

        compiled = new CompiledSelect(program, []);
        slotParameterIndices = slotToParameter;
        return true;
    }

    // Assembles the late-bound binding for a routed parameterized VALUES: one value per declared
    // slot, read from the supplied parameter array through ReadParameter so an out-of-range index
    // raises the exact "Missing value for parameter at position N" the evaluator raises. A
    // constant-only VALUES declares no slots and takes the empty binding.
    private static VdbeParameterBinding BuildValuesBinding(
        IReadOnlyList<int> slotParameterIndices,
        SqlValue[] parameters)
    {
        if (slotParameterIndices.Count == 0)
            return VdbeParameterBinding.Empty;

        var slotValues = new SqlValue[slotParameterIndices.Count];
        for (var slot = 0; slot < slotValues.Length; slot++)
            slotValues[slot] = ReadParameter(parameters, slotParameterIndices[slot]);

        return VdbeParameterBinding.FromValues(slotValues);
    }

    // Evaluates a VALUES(...) clause into a result whose columns are named
    // "column1".."columnN". Expressions are evaluated once per row against the current
    // row context so correlated VALUES (e.g. (VALUES(outer_col))) resolve outer columns.
    private ExecutionResult ExecuteValues(
        ValuesClause values,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        var columnCount = values.Rows[0].Count;
        var columns = new string[columnCount];
        for (var index = 0; index < columnCount; index++)
            columns[index] = $"column{index + 1}";

        var rows = new List<SqlValue[]>(values.Rows.Count);
        foreach (var row in values.Rows)
        {
            if (row.Count != columnCount)
                throw new EmbeddedSqlException("all VALUES must have the same number of terms");

            var evaluated = new SqlValue[columnCount];
            for (var index = 0; index < columnCount; index++)
                evaluated[index] = Evaluate(row[index], parameters, outerRow, context);
            rows.Add(evaluated);
        }

        return new ExecutionResult(columns, rows, 0);
    }

    private ExecutionResult ExecuteWithSelect(
        WithSelectStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        var cteContext = MaterializeCommonTableExpressions(
            statement.CommonTableExpressions,
            parameters,
            context,
            outerRow);

        return ExecuteQuery(statement.Query, parameters, cteContext, outerRow);
    }

    private ExecutionResult ExecuteWithDml(
        WithDmlStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        ValidateDmlCommonTableExpressionUsage(statement);
        var cteContext = MaterializeCommonTableExpressions(
            statement.CommonTableExpressions,
            parameters,
            context,
            outerRow: null);
        var backup = CloneTables(context.Tables);
        try
        {
            return statement.Dml switch
            {
                InsertStatement insert => ExecuteInsert(insert, parameters, cteContext),
                UpdateStatement update => ExecuteUpdate(update, parameters, cteContext),
                DeleteStatement delete => ExecuteDelete(delete, parameters, cteContext),
                _ => throw new EmbeddedSqlException("WITH must precede an INSERT, UPDATE, or DELETE statement."),
            };
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }
    }

    private static void ValidateDmlCommonTableExpressionUsage(WithDmlStatement statement)
    {
        var names = statement.CommonTableExpressions.Select(commonTableExpression => commonTableExpression.Name).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            return;

        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (CountDmlReferences(statement.Dml, name) > 0)
                required.Add(name);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var commonTableExpression in statement.CommonTableExpressions)
            {
                if (!required.Contains(commonTableExpression.Name))
                    continue;

                foreach (var name in names)
                {
                    if (CountAllReferences(commonTableExpression.Query, name) > 0)
                        changed |= required.Add(name);
                }
            }
        }

        var unused = names.FirstOrDefault(name => !required.Contains(name));
        if (unused is not null)
        {
            throw new EmbeddedSqlException(
                $"Managed CTE DML requires every CTE to contribute to the DML statement; {unused} is unused.");
        }
    }

    private QueryContext MaterializeCommonTableExpressions(
        IReadOnlyList<CommonTableExpression> expressions,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        var resolvedExpressions = new Dictionary<string, SourceData>(
            context.CommonTableExpressions,
            StringComparer.OrdinalIgnoreCase);
        var namesInCurrentClause = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var commonTableExpression in expressions)
        {
            if (!namesInCurrentClause.Add(commonTableExpression.Name))
                throw new EmbeddedSqlException($"duplicate WITH table name: {commonTableExpression.Name}");

            var cteContext = context with { CommonTableExpressions = resolvedExpressions };
            var resolved = CountAllReferences(commonTableExpression.Query, commonTableExpression.Name) > 0
                ? EvaluateRecursiveCte(commonTableExpression, parameters, cteContext, outerRow)
                : EvaluateNonRecursiveCte(commonTableExpression, parameters, cteContext, outerRow);
            resolvedExpressions[commonTableExpression.Name] = resolved;
        }

        return context with { CommonTableExpressions = resolvedExpressions };
    }

    private SourceData EvaluateNonRecursiveCte(
        CommonTableExpression commonTableExpression,
        SqlValue[] parameters,
        QueryContext cteContext,
        SourceRow? outerRow)
    {
        var result = MaterializeQueryResult(
            ExecuteQuery(commonTableExpression.Query, parameters, cteContext, outerRow));
        var columns = ResolveCommonTableExpressionColumns(commonTableExpression, result.Columns);
        return new SourceData(
            columns,
            result.Rows.Select(row => new SourceRow(columns, row.ToArray())).ToArray(),
            GetQueryOutputCollations(commonTableExpression.Query, cteContext));
    }

    // Safety cap on the number of rows a recursive CTE may materialize. SQLite streams
    // results and relies on the query eventually terminating; because this engine
    // materializes eagerly, a runaway UNION ALL recursion (or a genuinely non-terminating
    // query that SQLite would also loop on forever) is bounded here so it fails loudly
    // instead of exhausting memory. The cap is far above any legitimately supported query.
    private const int RecursiveCteRowLimit = 100_000;

    // Evaluates a recursive common table expression using semi-naive (working-set)
    // iteration: run the anchor once, then repeatedly run the recursive term(s) over only
    // the rows produced by the previous step until no new rows appear. UNION deduplicates
    // (which also terminates cycles); UNION ALL keeps every row.
    private SourceData EvaluateRecursiveCte(
        CommonTableExpression commonTableExpression,
        SqlValue[] parameters,
        QueryContext cteContext,
        SourceRow? outerRow)
    {
        var name = commonTableExpression.Name;
        if (commonTableExpression.Query is not CompoundSelectStatement compound)
            throw new EmbeddedSqlException($"circular reference: {name}");

        var firstRecursiveIndex = -1;
        for (var index = 0; index < compound.Terms.Count; index++)
        {
            if (CountAllReferences(compound.Terms[index], name) > 0)
            {
                firstRecursiveIndex = index;
                break;
            }
        }

        // A recursive reference with no preceding anchor (including a recursive first term)
        // has nothing to seed the iteration and matches SQLite's "circular reference".
        if (firstRecursiveIndex <= 0)
            throw new EmbeddedSqlException($"circular reference: {name}");

        // The operator joining the anchor block to the recursive block, and every operator
        // between recursive terms, must be a uniform UNION or UNION ALL.
        var recursiveOperator = compound.Operators[firstRecursiveIndex - 1];
        if (recursiveOperator is not (CompoundOperator.Union or CompoundOperator.UnionAll))
            throw new EmbeddedSqlException($"circular reference: {name}");
        for (var index = firstRecursiveIndex; index < compound.Operators.Count; index++)
        {
            if (compound.Operators[index] != recursiveOperator)
                throw new EmbeddedSqlException("recursive table may not use mixed UNION and UNION ALL operators");
        }

        var recursiveTerms = new List<SelectStatement>();
        for (var index = firstRecursiveIndex; index < compound.Terms.Count; index++)
        {
            var term = ValidateRecursiveTerm(compound.Terms[index], name);
            recursiveTerms.Add(term);
        }

        var anchor = MaterializeQueryResult(
            EvaluateRecursiveAnchor(compound, firstRecursiveIndex, parameters, cteContext, outerRow));
        var columns = ResolveCommonTableExpressionColumns(commonTableExpression, anchor.Columns);
        var collations = GetQueryOutputCollations(commonTableExpression.Query, cteContext);
        var deduplicate = recursiveOperator == CompoundOperator.Union;

        // Route the linear single-term subset -- one already-validated recursive SELECT that scans the
        // CTE exactly once as its sole source, with no DISTINCT and no ORDER BY, over a non-empty anchor
        // that fits the row guard -- entirely through the recursive-worktable bytecode. The anchor rows
        // seed the FIFO frontier and the recursive term becomes the per-frontier-row transform, so the
        // Step/Expand loop reproduces the tree-walking loop's breadth-first working-set iteration exactly
        // (same rows, same order, same UNION de-duplication). Every other shape -- multiple recursive
        // terms, a joined/derived recursive source, a DISTINCT or ORDER BY recursive term, a compound with
        // its own ORDER BY/LIMIT/OFFSET, or an empty/oversized anchor -- stays on the loop below.
        if (recursiveTerms.Count == 1
            && anchor.Rows.Count >= 1
            && anchor.Rows.Count <= RecursiveCteRowLimit
            && compound.OrderBy.Count == 0
            && compound.Limit is null
            && compound.Offset is null
            && IsLinearRecursiveTerm(recursiveTerms[0], name))
        {
            return RunRecursiveCteViaVdbe(
                name,
                anchor.Rows,
                columns,
                collations,
                recursiveTerms[0],
                deduplicate,
                parameters,
                cteContext,
                outerRow);
        }

        var result = new List<SourceRow>();
        var seen = deduplicate ? new HashSet<string>(StringComparer.Ordinal) : null;
        var workingSet = new List<SourceRow>();
        foreach (var row in anchor.Rows)
        {
            var values = row.ToArray();
            if (seen is not null && !seen.Add(BuildRowKey(values)))
                continue;

            var sourceRow = new SourceRow(columns, values);
            result.Add(sourceRow);
            workingSet.Add(sourceRow);
        }

        while (workingSet.Count > 0)
        {
            var iterationContext = cteContext with
            {
                CommonTableExpressions = new Dictionary<string, SourceData>(
                    cteContext.CommonTableExpressions,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [name] = new SourceData(columns, workingSet, collations),
                },
            };

            var produced = new List<SourceRow>();
            foreach (var term in recursiveTerms)
            {
                var termResult = MaterializeQueryResult(
                    ExecuteSelect(term, parameters, iterationContext, outerRow));
                if (termResult.Columns.Length != columns.Length)
                    throw new EmbeddedSqlException("SELECTs to the left and right of a compound operator do not have the same number of result columns");

                foreach (var row in termResult.Rows)
                {
                    var values = row.ToArray();
                    if (seen is not null && !seen.Add(BuildRowKey(values)))
                        continue;

                    var sourceRow = new SourceRow(columns, values);
                    result.Add(sourceRow);
                    produced.Add(sourceRow);
                    if (result.Count > RecursiveCteRowLimit)
                        throw new EmbeddedSqlException(
                            $"recursive query for {name} exceeded the maximum of {RecursiveCteRowLimit} rows");
                }
            }

            workingSet = produced;
        }

        return new SourceData(columns, result, collations);
    }

    // Whether a recursive term is the linear shape the recursive-worktable bytecode lowers exactly: its
    // sole FROM source is the recursive CTE itself (so every generation scans the CTE once, row by row),
    // and it carries neither DISTINCT nor ORDER BY -- both of which would let whole-working-set evaluation
    // diverge from the per-frontier-row expansion the worktable performs. The caller has already run
    // ValidateRecursiveTerm, so aggregates, GROUP BY/HAVING, window functions, LIMIT/OFFSET, and any second
    // reference to the CTE are already excluded; this only adds the single-source / no-DISTINCT / no-ORDER
    // BY conditions that make per-row expansion order- and multiset-identical to the tree-walking loop.
    private static bool IsLinearRecursiveTerm(SelectStatement term, string name)
        => term.Source is NamedTableSource named
            && string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase)
            && !term.Distinct
            && term.OrderBy.Count == 0;

    // Runs a routed linear recursive CTE on the resumable state machine and materializes its result as a
    // SourceData whose columns are the CTE's resolved columns -- byte-identical to what the tree-walking
    // loop produces, but with the FIFO Step/Expand recursion executing as real, observably looping
    // bytecode. The row guard surfaces as the evaluator's own "exceeded the maximum" diagnostic so a
    // runaway UNION ALL fails with the identical message whether it routed or not.
    private SourceData RunRecursiveCteViaVdbe(
        string name,
        IReadOnlyList<SqlValue[]> anchorRows,
        string[] columns,
        IReadOnlyList<string?> collations,
        SelectStatement recursiveTerm,
        bool deduplicate,
        SqlValue[] parameters,
        QueryContext cteContext,
        SourceRow? outerRow)
    {
        var program = BuildRecursiveCteProgram(
            name, anchorRows, columns, collations, recursiveTerm, deduplicate, parameters, cteContext, outerRow);

        var rows = new List<SourceRow>();
        try
        {
            using var runtime = new ResumableStatement(program);
            while (true)
            {
                switch (runtime.StepResumable())
                {
                    case ResumableStatementStepResult.Row:
                        rows.Add(new SourceRow(columns, MaterializeQueryRow(runtime.CurrentRow!)));
                        break;
                    case ResumableStatementStepResult.Done:
                        return new SourceData(columns, rows, collations);
                    default:
                        throw new EmbeddedSqlException("Recursive program yielded during evaluation.");
                }
            }
        }
        catch (RecursiveWorkTableOverflowException)
        {
            throw new EmbeddedSqlException(
                $"recursive query for {name} exceeded the maximum of {RecursiveCteRowLimit} rows");
        }
    }

    // Lowers a linear recursive CTE onto a RecursiveCteProgramBuilder program: the anchor rows become the
    // constant seed generation and the recursive term becomes the VdbeRecursiveTransform. The transform is
    // the tree-walking evaluator's own recursive-term evaluation restricted to a single frontier row -- the
    // CTE is bound to a one-row working set and ExecuteSelect projects/filters exactly as it would over the
    // whole set -- so value, NULL, collation, and correlation semantics are reused rather than re-derived,
    // matching how every other direct builder delegates its leaf semantics. Because a linear term scans the
    // CTE exactly once (guaranteed by IsLinearRecursiveTerm plus ValidateRecursiveTerm), expanding one row
    // at a time and draining FIFO yields the identical multiset and order as whole-working-set evaluation.
    // Both guards are pinned to the evaluator's row cap: the row guard reproduces its "exceeded the maximum"
    // bound (seeds count as admitted rows exactly as anchors count toward the loop's result), and the depth
    // guard is set to the same cap so it can never fire before the row guard (a chain of depth d needs more
    // than d admitted rows), leaving termination to de-duplication, the row cap, or the transform running dry
    // -- exactly as the tree-walking loop terminates.
    private VdbeProgram BuildRecursiveCteProgram(
        string name,
        IReadOnlyList<SqlValue[]> anchorRows,
        string[] columns,
        IReadOnlyList<string?> collations,
        SelectStatement recursiveTerm,
        bool deduplicate,
        SqlValue[] parameters,
        QueryContext cteContext,
        SourceRow? outerRow)
    {
        var width = columns.Length;

        // One dictionary/context reused across expansions: each call rebinds only the CTE's single-row
        // working set, which ExecuteSelect consumes synchronously before the next dequeue, so nothing
        // mutable leaks between frontier rows.
        var iterationTables = new Dictionary<string, SourceData>(
            cteContext.CommonTableExpressions, StringComparer.OrdinalIgnoreCase);
        var iterationContext = cteContext with { CommonTableExpressions = iterationTables };

        IReadOnlyList<SqlValue[]> Transform(SqlValue[] frontierRow)
        {
            iterationTables[name] = new SourceData(
                columns,
                [new SourceRow(columns, frontierRow)],
                collations);
            var termResult = MaterializeQueryResult(
                ExecuteSelect(recursiveTerm, parameters, iterationContext, outerRow));
            if (termResult.Columns.Length != width)
            {
                throw new EmbeddedSqlException(
                    "SELECTs to the left and right of a compound operator do not have the same number of result columns");
            }

            var children = new SqlValue[termResult.Rows.Count][];
            for (var index = 0; index < termResult.Rows.Count; index++)
                children[index] = MaterializeQueryRow(termResult.Rows[index]);

            return children;
        }

        if (deduplicate)
        {
            return RecursiveCteProgramBuilder.BuildUnionDistinct(
                anchorRows, Transform, RecursiveRowsEqual, RecursiveCteRowLimit, RecursiveCteRowLimit);
        }

        return RecursiveCteProgramBuilder.BuildUnionAll(
            anchorRows, Transform, RecursiveCteRowLimit, RecursiveCteRowLimit);
    }

    // UNION (DISTINCT) recursion de-duplicates on the same ordinal string row key the tree-walking loop's
    // `seen` set uses, so the routed worktable admits and drops exactly the rows the evaluator would --
    // including treating two NULLs as equal and keeping integers and reals with the same magnitude distinct.
    private static bool RecursiveRowsEqual(SqlValue[] left, SqlValue[] right)
        => string.Equals(BuildRowKey(left), BuildRowKey(right), StringComparison.Ordinal);

    // Structural analysis of a recursive CTE body used only by EXPLAIN: returns true (with the single
    // recursive term, the anchor/recursive split point, and whether the operator de-duplicates) exactly
    // when the body is the routable linear shape. It mirrors EvaluateRecursiveCte's validation as pure
    // predicates -- it never throws -- so any non-routable or malformed shape simply declines and EXPLAIN
    // falls through to its standard "only lowered statements" error instead of a bytecode dump.
    private bool TryAnalyzeLinearRecursiveCte(
        CompoundSelectStatement compound,
        string name,
        out SelectStatement recursiveTerm,
        out int firstRecursiveIndex,
        out bool deduplicate)
    {
        recursiveTerm = null!;
        firstRecursiveIndex = -1;
        deduplicate = false;

        for (var index = 0; index < compound.Terms.Count; index++)
        {
            if (CountAllReferences(compound.Terms[index], name) > 0)
            {
                firstRecursiveIndex = index;
                break;
            }
        }

        // Need at least one anchor term before the recursive block, exactly one recursive term (the last),
        // no compound-level ORDER BY/LIMIT/OFFSET, and a uniform UNION or UNION ALL operator chain.
        if (firstRecursiveIndex <= 0
            || compound.Terms.Count - firstRecursiveIndex != 1
            || compound.OrderBy.Count != 0
            || compound.Limit is not null
            || compound.Offset is not null)
        {
            return false;
        }

        var recursiveOperator = compound.Operators[firstRecursiveIndex - 1];
        if (recursiveOperator is not (CompoundOperator.Union or CompoundOperator.UnionAll))
            return false;
        for (var index = firstRecursiveIndex; index < compound.Operators.Count; index++)
        {
            if (compound.Operators[index] != recursiveOperator)
                return false;
        }

        if (compound.Terms[firstRecursiveIndex] is not SelectStatement select
            || !IsLinearRecursiveTerm(select, name)
            || CountDirectFromReferences(select.Source, name) != 1
            || CountAllReferences(select, name) != 1
            || select.GroupBy.Count > 0
            || select.Having is not null
            || select.Limit is not null
            || select.Offset is not null
            || select.Projections.Any(projection => ContainsAggregate(projection.Expression))
            || CollectSelectWindowFunctions(select).Count > 0)
        {
            return false;
        }

        recursiveTerm = select;
        deduplicate = recursiveOperator == CompoundOperator.Union;
        return true;
    }

    // Whether a query is a bare `SELECT * FROM <name>` with no DISTINCT, WHERE, GROUP BY/HAVING, ORDER BY,
    // or LIMIT/OFFSET -- the pass-through outer query whose output is exactly the CTE's materialized rows,
    // so a recursive-worktable program is a faithful whole-statement lowering EXPLAIN may describe.
    private static bool IsBareSelectStarFrom(QueryStatement query, string name)
        => query is SelectStatement select
            && !select.Distinct
            && select.Projections.Count == 1
            && select.Projections[0].Expression is StarExpression
            && select.Where is null
            && select.GroupBy.Count == 0
            && select.Having is null
            && select.OrderBy.Count == 0
            && select.Limit is null
            && select.Offset is null
            && select.Source is NamedTableSource named
            && string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase);

    // EXPLAIN lowering for the routed recursive-CTE subset. Only a WITH whose single common table
    // expression is a routable linear recursion and whose outer query is a bare `SELECT * FROM cte` is
    // described, so the emitted worktable program's ResultRows are exactly the statement's output rows and
    // the dump is a faithful whole-statement lowering. Every other shape declines so EXPLAIN keeps
    // reporting only genuinely lowered programs.
    private bool TryBuildRecursiveCteExplainProgram(
        WithSelectStatement with,
        SqlValue[] parameters,
        QueryContext context,
        out VdbeProgram program)
    {
        program = null!;
        if (with.CommonTableExpressions.Count != 1)
            return false;

        var cte = with.CommonTableExpressions[0];
        if (!IsBareSelectStarFrom(with.Query, cte.Name)
            || CountAllReferences(cte.Query, cte.Name) == 0
            || cte.Query is not CompoundSelectStatement compound
            || !TryAnalyzeLinearRecursiveCte(compound, cte.Name, out var recursiveTerm, out var firstRecursiveIndex, out var deduplicate))
        {
            return false;
        }

        var cteContext = context with
        {
            CommonTableExpressions = new Dictionary<string, SourceData>(
                context.CommonTableExpressions, StringComparer.OrdinalIgnoreCase),
        };

        var anchor = EvaluateRecursiveAnchor(compound, firstRecursiveIndex, parameters, cteContext, outerRow: null);

        // Only describe when the anchor is non-empty and within the row guard and the declared column count
        // matches, i.e. exactly the shapes the execution path would route without raising a shape error.
        if (anchor.Rows.Count < 1
            || anchor.Rows.Count > RecursiveCteRowLimit
            || (cte.Columns is not null && cte.Columns.Count != anchor.Columns.Length))
        {
            return false;
        }

        var columns = ResolveCommonTableExpressionColumns(cte, anchor.Columns);
        var collations = GetQueryOutputCollations(cte.Query, cteContext);
        program = BuildRecursiveCteProgram(
            cte.Name,
            anchor.Rows,
            columns,
            collations,
            recursiveTerm,
            deduplicate,
            parameters,
            cteContext,
            outerRow: null);
        return true;
    }

    // Evaluates the anchor block (all terms before the first recursive term). A single
    // anchor term runs directly; multiple anchors run as their own compound so their
    // UNION/INTERSECT/EXCEPT semantics are preserved.
    private ExecutionResult EvaluateRecursiveAnchor(
        CompoundSelectStatement compound,
        int firstRecursiveIndex,
        SqlValue[] parameters,
        QueryContext cteContext,
        SourceRow? outerRow)
    {
        if (firstRecursiveIndex == 1)
            return ExecuteCompoundTerm(compound.Terms[0], parameters, cteContext, outerRow);

        var anchor = new CompoundSelectStatement(
            compound.Terms.Take(firstRecursiveIndex).ToArray(),
            compound.Operators.Take(firstRecursiveIndex - 1).ToArray(),
            [],
            null,
            null);
        return ExecuteCompoundSelect(anchor, parameters, cteContext, outerRow);
    }

    // Validates a single recursive term and returns it as a SELECT. Rejects constructs
    // SQLite forbids in a recursive term with the same messages it produces.
    private SelectStatement ValidateRecursiveTerm(QueryStatement term, string name)
    {
        if (term is not SelectStatement select)
            throw new EmbeddedSqlException($"circular reference: {name}");

        var directReferences = CountDirectFromReferences(select.Source, name);
        var allReferences = CountAllReferences(select, name);
        if (directReferences > 1)
            throw new EmbeddedSqlException($"multiple references to recursive table: {name}");

        // Any reference that is not a single top-level FROM entry (e.g. inside a subquery,
        // a derived table, or joined to itself) is not a supported linear recursion.
        if (directReferences != 1 || allReferences != 1)
            throw new EmbeddedSqlException($"circular reference: {name}");

        if (select.GroupBy.Count > 0
            || select.Having is not null
            || select.Projections.Any(projection => ContainsAggregate(projection.Expression)))
        {
            throw new EmbeddedSqlException("recursive aggregate queries not supported");
        }

        if (CollectSelectWindowFunctions(select).Count > 0)
            throw new EmbeddedSqlException("cannot use window functions in recursive queries");
        if (select.Limit is not null || select.Offset is not null)
            throw new EmbeddedSqlException("LIMIT and OFFSET are not supported in recursive queries");

        return select;
    }

    private static string BuildRowKey(IReadOnlyList<SqlValue> values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder.Append((int)value.Kind).Append(':');
            switch (value.Kind)
            {
                case SqlValueKind.Null:
                    break;
                case SqlValueKind.Integer:
                    builder.Append(value.AsInteger());
                    break;
                case SqlValueKind.Real:
                    builder.Append(value.AsReal().ToString("R", CultureInfo.InvariantCulture));
                    break;
                case SqlValueKind.Text:
                    builder.Append(value.AsText());
                    break;
                case SqlValueKind.Blob:
                    builder.Append(Convert.ToHexString(value.AsBlob().Span));
                    break;
            }

            builder.Append('|');
        }

        return builder.ToString();
    }

    // Counts references to a CTE name that appear as a table in the top level of a FROM
    // clause (walking only the join tree, never descending into derived tables/subqueries).
    private static int CountDirectFromReferences(TableSource? source, string name)
    {
        return source switch
        {
            null => 0,
            NamedTableSource named => string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            JoinTableSource join => CountDirectFromReferences(join.Left, name) + CountDirectFromReferences(join.Right, name),
            _ => 0,
        };
    }

    private static int CountDmlReferences(ParsedStatement statement, string name)
    {
        return statement switch
        {
            InsertStatement insert => insert.Rows.Sum(row => row.Sum(expression => CountReferencesInExpression(expression, name)))
                + (insert.Source is null ? 0 : CountAllReferences(insert.Source, name))
                + (insert.Returning?.Sum(projection => CountReferencesInExpression(projection.Expression, name)) ?? 0),
            UpdateStatement update => update.Assignments.Sum(assignment => CountReferencesInExpression(assignment.Value, name))
                + (update.Where is null ? 0 : CountReferencesInExpression(update.Where, name))
                + update.EffectiveOrderBy.Sum(term => CountReferencesInExpression(term.Expression, name))
                + (update.Limit is null ? 0 : CountReferencesInExpression(update.Limit, name))
                + (update.Offset is null ? 0 : CountReferencesInExpression(update.Offset, name))
                + (update.Returning?.Sum(projection => CountReferencesInExpression(projection.Expression, name)) ?? 0),
            DeleteStatement delete => (delete.Where is null ? 0 : CountReferencesInExpression(delete.Where, name))
                + delete.EffectiveOrderBy.Sum(term => CountReferencesInExpression(term.Expression, name))
                + (delete.Limit is null ? 0 : CountReferencesInExpression(delete.Limit, name))
                + (delete.Offset is null ? 0 : CountReferencesInExpression(delete.Offset, name))
                + (delete.Returning?.Sum(projection => CountReferencesInExpression(projection.Expression, name)) ?? 0),
            _ => 0,
        };
    }

    // Deep count of references to a CTE name anywhere in a query, including derived tables
    // and subquery expressions. A nested WITH that redefines the name shadows it, so
    // references beneath such a clause are not counted.
    private static int CountAllReferences(QueryStatement query, string name)
    {
        switch (query)
        {
            case SelectStatement select:
                var count = CountReferencesInTableSource(select.Source, name);
                if (select.Where is not null)
                    count += CountReferencesInExpression(select.Where, name);
                count += select.Projections.Sum(projection => CountReferencesInExpression(projection.Expression, name));
                count += select.GroupBy.Sum(expression => CountReferencesInExpression(expression, name));
                if (select.Having is not null)
                    count += CountReferencesInExpression(select.Having, name);
                count += select.OrderBy.Sum(term => CountReferencesInExpression(term.Expression, name));
                if (select.Limit is not null)
                    count += CountReferencesInExpression(select.Limit, name);
                if (select.Offset is not null)
                    count += CountReferencesInExpression(select.Offset, name);
                return count;
            case CompoundSelectStatement compound:
                return compound.Terms.Sum(term => CountAllReferences(term, name));
            case ValuesClause values:
                return values.Rows.Sum(row => row.Sum(expression => CountReferencesInExpression(expression, name)));
            case WithSelectStatement with:
                if (with.CommonTableExpressions.Any(cte => string.Equals(cte.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return 0;
                return with.CommonTableExpressions.Sum(cte => CountAllReferences(cte.Query, name))
                    + CountAllReferences(with.Query, name);
            default:
                return 0;
        }
    }

    private static int CountReferencesInTableSource(TableSource? source, string name)
    {
        return source switch
        {
            null => 0,
            NamedTableSource named => string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            DerivedTableSource derived => CountAllReferences(derived.Query, name),
            JoinTableSource join => CountReferencesInTableSource(join.Left, name)
                + CountReferencesInTableSource(join.Right, name)
                + (join.Condition is null ? 0 : CountReferencesInExpression(join.Condition, name)),
            _ => 0,
        };
    }

    private static int CountReferencesInExpression(Expression expression, string name)
    {
        return expression switch
        {
            ScalarSubqueryExpression subquery => CountAllReferences(subquery.Query, name),
            ExistsExpression exists => CountAllReferences(exists.Query, name),
            InSubqueryExpression inSubquery => CountReferencesInExpression(inSubquery.Value, name)
                + CountAllReferences(inSubquery.Query, name),
            FunctionExpression function => function.Arguments.Sum(argument => CountReferencesInExpression(argument, name))
                + (function.Filter is null ? 0 : CountReferencesInExpression(function.Filter, name)),
            BinaryExpression binary => CountReferencesInExpression(binary.Left, name)
                + CountReferencesInExpression(binary.Right, name),
            UnaryExpression unary => CountReferencesInExpression(unary.Operand, name),
            CollationExpression collation => CountReferencesInExpression(collation.Expression, name),
            CastExpression cast => CountReferencesInExpression(cast.Expression, name),
            CaseExpression @case => (@case.Operand is null ? 0 : CountReferencesInExpression(@case.Operand, name))
                + @case.Clauses.Sum(clause => CountReferencesInExpression(clause.When, name) + CountReferencesInExpression(clause.Then, name))
                + (@case.Else is null ? 0 : CountReferencesInExpression(@case.Else, name)),
            LikeExpression like => CountReferencesInExpression(like.Value, name)
                + CountReferencesInExpression(like.Pattern, name)
                + (like.Escape is null ? 0 : CountReferencesInExpression(like.Escape, name)),
            GlobExpression glob => CountReferencesInExpression(glob.Value, name)
                + CountReferencesInExpression(glob.Pattern, name),
            InExpression @in => CountReferencesInExpression(@in.Value, name)
                + @in.Values.Sum(value => CountReferencesInExpression(value, name)),
            BetweenExpression between => CountReferencesInExpression(between.Value, name)
                + CountReferencesInExpression(between.Lower, name)
                + CountReferencesInExpression(between.Upper, name),
            _ => 0,
        };
    }

    private static string[] ResolveCommonTableExpressionColumns(
        CommonTableExpression commonTableExpression,
        IReadOnlyList<string> resultColumns)
    {
        if (commonTableExpression.Columns is null)
            return resultColumns.ToArray();
        if (commonTableExpression.Columns.Count != resultColumns.Count)
        {
            throw new EmbeddedSqlException(
                $"table {commonTableExpression.Name} has {resultColumns.Count} values for {commonTableExpression.Columns.Count} columns");
        }

        return commonTableExpression.Columns.ToArray();
    }

    private List<SqlValue[]> ApplyUnion(
        IReadOnlyList<SqlValue[]> left,
        IReadOnlyList<SqlValue[]> right,
        IReadOnlyList<string?> collations)
    {
        var result = new List<SqlValue[]>(left.Count + right.Count);
        AddDistinctRows(result, left, collations);
        AddDistinctRows(result, right, collations);
        return result;
    }

    private List<SqlValue[]> ApplyIntersect(
        IReadOnlyList<SqlValue[]> left,
        IReadOnlyList<SqlValue[]> right,
        IReadOnlyList<string?> collations)
    {
        var result = new List<SqlValue[]>();
        foreach (var row in left)
        {
            if (result.Any(candidate => RowsEqual(candidate, row, collations))
                || !right.Any(candidate => RowsEqual(candidate, row, collations)))
                continue;

            result.Add(row.ToArray());
        }

        return result;
    }

    private List<SqlValue[]> ApplyExcept(
        IReadOnlyList<SqlValue[]> left,
        IReadOnlyList<SqlValue[]> right,
        IReadOnlyList<string?> collations)
    {
        var result = new List<SqlValue[]>();
        foreach (var row in left)
        {
            if (result.Any(candidate => RowsEqual(candidate, row, collations))
                || right.Any(candidate => RowsEqual(candidate, row, collations)))
                continue;

            result.Add(row.ToArray());
        }

        return result;
    }

    private void AddDistinctRows(
        List<SqlValue[]> destination,
        IEnumerable<SqlValue[]> source,
        IReadOnlyList<string?> collations)
    {
        foreach (var row in source)
        {
            if (!destination.Any(candidate => RowsEqual(candidate, row, collations)))
                destination.Add(row.ToArray());
        }
    }

    private bool RowsEqual(
        IReadOnlyList<SqlValue> left,
        IReadOnlyList<SqlValue> right,
        IReadOnlyList<string?>? collations = null)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Kind == SqlValueKind.Null || right[index].Kind == SqlValueKind.Null)
            {
                if (left[index].Kind != right[index].Kind)
                    return false;
                continue;
            }

            if (Compare(left[index], right[index], collations?[index]) != 0)
                return false;
        }

        return true;
    }

    private void SortCompoundRows(
        List<SqlValue[]> rows,
        CompoundSelectStatement statement,
        IReadOnlyList<string> columns,
        QueryContext context)
    {
        var outputCollations = GetQueryOutputCollations(statement.Terms[0], context);
        var orderBy = statement.OrderBy.Select(term =>
        {
            var index = ResolveCompoundOrderByIndex(term, statement.Terms, columns);
            // An explicit ORDER BY collation overrides the result expression's collation.
            // Projection collations only exist on SELECT terms; a leading VALUES term has none.
            var projectionCollation = outputCollations.ElementAtOrDefault(index);
            return (index, term, GetCollation(term.Expression) ?? projectionCollation);
        }).ToArray();
        rows.Sort((left, right) =>
        {
            foreach (var term in orderBy)
            {
                var comparison = CompareForOrdering(
                    left[term.index],
                    right[term.index],
                    term.term,
                    term.Item3);
                if (comparison == 0)
                    continue;

                return comparison;
            }

            return 0;
        });
    }

    private void ValidateCompoundOrderByCollations(
        CompoundSelectStatement statement,
        QueryContext context)
    {
        if (statement.OrderBy.Count == 0)
            return;

        var columns = DescribeQuery(statement.Terms[0], context);
        var outputCollations = GetQueryOutputCollations(statement.Terms[0], context);
        foreach (var term in statement.OrderBy)
        {
            var index = ResolveCompoundOrderByIndex(term, statement.Terms, columns);
            ValidateCollation(GetCollation(term.Expression) ?? outputCollations.ElementAtOrDefault(index));
        }
    }

    private static int ResolveCompoundOrderByIndex(
        OrderByTerm orderBy,
        IReadOnlyList<QueryStatement> terms,
        IReadOnlyList<string> columns)
    {
        var expression = orderBy.Expression;
        if (orderBy.Ordinal is { } ordinal)
        {
            if (ordinal >= 1 && ordinal <= columns.Count)
                return (int)ordinal - 1;

            throw new EmbeddedSqlException(
                $"ORDER BY position {ordinal} is out of range for {columns.Count} result columns");
        }

        var reference = UnwrapCollation(expression);
        var selectTerms = terms.OfType<SelectStatement>().ToArray();
        if (reference is ColumnExpression column)
        {
            for (var index = 0; index < columns.Count; index++)
            {
                if (string.Equals(columns[index], column.Name, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            foreach (var term in selectTerms)
            {
                for (var index = 0; index < term.Projections.Count; index++)
                {
                    if (string.Equals(term.Projections[index].Alias, column.Name, StringComparison.OrdinalIgnoreCase))
                        return index;
                }
            }
        }

        foreach (var term in selectTerms)
        {
            for (var index = 0; index < term.Projections.Count; index++)
            {
                if (term.Projections[index].Expression.Equals(expression)
                    || term.Projections[index].Expression.Equals(reference))
                    return index;
            }
        }

        throw new EmbeddedSqlException("ORDER BY term does not match any column in the result set");
    }

    private SqlValue[][] ApplyDistinctLimit(
        IEnumerable<SqlValue[]> source,
        bool distinct,
        long offset,
        long? limit,
        IReadOnlyList<string?>? collations = null)
    {
        var rows = source.ToList();
        if (distinct)
        {
            var distinctRows = new List<SqlValue[]>(rows.Count);
            foreach (var row in rows)
            {
                if (!distinctRows.Any(candidate => RowsEqual(candidate, row, collations)))
                    distinctRows.Add(row);
            }

            rows = distinctRows;
        }

        if (offset >= rows.Count)
            return [];
        if (offset > 0)
            rows.RemoveRange(0, (int)offset);
        if (limit is >= 0 && limit.Value < rows.Count)
            rows.RemoveRange((int)limit.Value, rows.Count - (int)limit.Value);

        return rows.ToArray();
    }

    private string GetGroupKey(
        IReadOnlyList<Expression> groupBy,
        SqlValue[] parameters,
        SourceRow row,
        QueryContext context)
    {
        var key = new System.Text.StringBuilder();
        foreach (var expression in groupBy)
        {
            var value = Evaluate(expression, parameters, row, context);
            switch (value.Kind)
            {
                case SqlValueKind.Null:
                    key.Append("N;");
                    break;
                case SqlValueKind.Integer:
                    key.Append("N:").Append(value.AsInteger().ToString(CultureInfo.InvariantCulture)).Append(';');
                    break;
                case SqlValueKind.Real:
                    key.Append("N:").Append(value.AsReal() == 0
                        ? "0"
                        : value.AsReal().ToString("R", CultureInfo.InvariantCulture)).Append(';');
                    break;
                case SqlValueKind.Text:
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(value.AsText());
                        key.Append("T:").Append(bytes.Length).Append(':').Append(Convert.ToBase64String(bytes)).Append(';');
                        break;
                    }
                case SqlValueKind.Blob:
                    key.Append("B:").Append(value.AsBlob().Length).Append(':')
                        .Append(Convert.ToBase64String(value.AsBlob().Span)).Append(';');
                    break;
                default:
                    throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}.");
            }
        }

        return key.ToString();
    }

    private static string[] GetSourceColumns(TableSource? source, QueryContext context)
    {
        return source switch
        {
            null => [],
            NamedTableSource named when IsSchemaTable(named.Name) => ["type", "name", "tbl_name", "rootpage", "sql"],
            NamedTableSource named when context.CommonTableExpressions.TryGetValue(named.Name, out var commonTableExpression)
                => commonTableExpression.Columns,
            NamedTableSource named when TryGetView(context, named.Name, out var view)
                => ResolveViewColumns(view, EnterView(context, view.Name)),
            NamedTableSource named => GetTable(named, context.Tables).Columns,
            GenerateSeriesSource => ["value"],
            DerivedTableSource derived => DescribeQuery(derived.Query, context),
            JoinTableSource join => GetSourceColumns(join.Left, context)
                .Concat(GetSourceColumns(join.Right, context))
                .ToArray(),
            _ => throw new EmbeddedSqlException($"Unsupported table source {source.GetType().Name}."),
        };
    }

    private static IReadOnlyList<OutputColumn> GetOutputColumns(TableSource? source, QueryContext context)
    {
        switch (source)
        {
            case null:
                return [];
            case NamedTableSource named when IsSchemaTable(named.Name):
                return BuildOutputColumns(named.Alias ?? named.Name, ["type", "name", "tbl_name", "rootpage", "sql"]);
            case NamedTableSource named when context.CommonTableExpressions.TryGetValue(named.Name, out var commonTableExpression):
                return BuildOutputColumns(named.Alias ?? named.Name, commonTableExpression.Columns);
            case NamedTableSource named when TryGetView(context, named.Name, out var view):
                return BuildOutputColumns(named.Alias ?? view.Name, ResolveViewColumns(view, EnterView(context, view.Name)));
            case NamedTableSource named:
                return BuildOutputColumns(named.Alias ?? named.Name, GetTable(named, context.Tables).Columns);
            case GenerateSeriesSource:
                return [new OutputColumn(null, "value", 0)];
            case DerivedTableSource derived:
                return BuildOutputColumns(derived.Alias, DescribeQuery(derived.Query, context));
            case JoinTableSource join:
                return GetJoinOutputColumns(join, context);
            default:
                throw new EmbeddedSqlException($"Unsupported table source {source.GetType().Name}.");
        }
    }

    private static IReadOnlyList<OutputColumn> GetRawOutputColumns(TableSource? source, QueryContext context)
    {
        if (source is not JoinTableSource join)
            return GetOutputColumns(source, context);

        var left = GetRawOutputColumns(join.Left, context);
        var leftWidth = GetSourceColumns(join.Left, context).Length;
        var right = GetRawOutputColumns(join.Right, context);
        return left.Concat(right.Select(column => column with { Index = column.Index + leftWidth })).ToArray();
    }

    private static IReadOnlyList<OutputColumn> BuildOutputColumns(string? qualifier, IReadOnlyList<string> columns)
    {
        var result = new OutputColumn[columns.Count];
        for (var index = 0; index < columns.Count; index++)
            result[index] = new OutputColumn(qualifier, columns[index], index);

        return result;
    }

    private static IReadOnlyList<OutputColumn> GetJoinOutputColumns(JoinTableSource join, QueryContext context)
    {
        var left = GetOutputColumns(join.Left, context);
        var right = GetOutputColumns(join.Right, context);
        var leftWidth = GetSourceColumns(join.Left, context).Length;

        IReadOnlyList<string> coalescedNames;
        if (join.UsingColumns is { } usingColumns)
        {
            foreach (var name in usingColumns)
            {
                if (!left.Any(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
                    || !right.Any(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new EmbeddedSqlException($"cannot join using column {name} - column not present in both tables");
                }
            }

            coalescedNames = usingColumns;
        }
        else if (join.Natural)
        {
            var rightNames = new HashSet<string>(right.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
            coalescedNames = left.Where(column => rightNames.Contains(column.Name)).Select(column => column.Name).ToArray();
        }
        else
        {
            coalescedNames = [];
        }

        var result = new List<OutputColumn>(left.Count + right.Count);
        if (coalescedNames.Count == 0)
        {
            result.AddRange(left);
            foreach (var column in right)
            {
                result.Add(column with
                {
                    Index = column.Index + leftWidth,
                    CoalesceIndex = column.CoalesceIndex is { } coalesceIndex
                        ? coalesceIndex + leftWidth
                        : null,
                });
            }

            return result;
        }

        var coalescedSet = new HashSet<string>(coalescedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var column in left)
        {
            if (coalescedSet.Contains(column.Name))
            {
                var match = right.First(candidate => string.Equals(candidate.Name, column.Name, StringComparison.OrdinalIgnoreCase));
                result.Add(column with { CoalesceIndex = match.Index + leftWidth });
            }
            else
            {
                result.Add(column);
            }
        }

        foreach (var column in right)
        {
            if (!coalescedSet.Contains(column.Name))
            {
                result.Add(column with
                {
                    Index = column.Index + leftWidth,
                    CoalesceIndex = column.CoalesceIndex is { } coalesceIndex
                        ? coalesceIndex + leftWidth
                        : null,
                });
            }
        }

        return result;
    }

    private SourceData GetSourceRows(
        TableSource? source,
        SqlValue[] parameters,
        QueryContext context,
        long? maximumRows,
        SourceRow? outerRow)
    {
        if (source is null)
            return new SourceData([], [new SourceRow([], [], Parent: outerRow)]);

        return source switch
        {
            NamedTableSource named when context.CommonTableExpressions.TryGetValue(named.Name, out var commonTableExpression)
                => GetCommonTableExpressionRows(named, commonTableExpression, outerRow, maximumRows),
            NamedTableSource named when TryGetView(context, named.Name, out var view)
                => GetViewRows(named, view, parameters, context, maximumRows, outerRow),
            NamedTableSource named => GetNamedTableRows(named, context, maximumRows, outerRow),
            GenerateSeriesSource series => GetSeriesRows(series, parameters, context, maximumRows, outerRow),
            DerivedTableSource derived => GetDerivedTableRows(derived, parameters, context, outerRow, maximumRows),
            JoinTableSource join => GetJoinRows(join, parameters, context, maximumRows, outerRow),
            _ => throw new EmbeddedSqlException($"Unsupported table source {source.GetType().Name}."),
        };
    }

    private SourceData GetJoinRows(
        JoinTableSource source,
        SqlValue[] parameters,
        QueryContext context,
        long? maximumRows,
        SourceRow? outerRow)
    {
        var left = GetSourceRows(source.Left, parameters, context, maximumRows: null, outerRow);
        var right = GetSourceRows(source.Right, parameters, context, maximumRows: null, outerRow);
        var leftQualifiedColumns = GetQualifiedColumns(source.Left, context);
        var rightQualifiedColumns = GetQualifiedColumns(source.Right, context);
        var columns = left.Columns.Concat(right.Columns).ToArray();
        var outputColumns = GetOutputColumns(source, context);
        var leftWidth = left.Columns.Length;
        var joinPairs = BuildJoinPairs(source, context);

        var rows = new List<SourceRow>();
        var rightMatched = new bool[right.Rows.Count];
        foreach (var leftRow in left.Rows)
        {
            var matched = false;
            for (var rightIndex = 0; rightIndex < right.Rows.Count; rightIndex++)
            {
                var rightRow = right.Rows[rightIndex];
                var values = leftRow.Values.Concat(rightRow.Values).ToArray();
                var row = new SourceRow(
                    columns,
                    values,
                    CombineQualifiedColumns(leftRow.QualifiedColumns, rightRow.QualifiedColumns, leftWidth),
                    outerRow,
                    outputColumns);
                if (!JoinConditionMatches(source, joinPairs, row, leftRow, rightRow, parameters, context))
                    continue;

                matched = true;
                rightMatched[rightIndex] = true;
                rows.Add(row);
                if (maximumRows is not null && rows.Count >= maximumRows.Value)
                    return new SourceData(columns, rows);
            }

            if (!matched && source.Kind is JoinKind.Left or JoinKind.Full)
            {
                var values = leftRow.Values
                    .Concat(Enumerable.Repeat(SqlValue.Null, right.Columns.Length))
                    .ToArray();
                rows.Add(new SourceRow(
                    columns,
                    values,
                    CombineQualifiedColumns(leftRow.QualifiedColumns, rightQualifiedColumns, leftWidth),
                    outerRow,
                    outputColumns));
                if (maximumRows is not null && rows.Count >= maximumRows.Value)
                    return new SourceData(columns, rows);
            }
        }

        if (source.Kind is JoinKind.Right or JoinKind.Full)
        {
            for (var rightIndex = 0; rightIndex < right.Rows.Count; rightIndex++)
            {
                if (rightMatched[rightIndex])
                    continue;

                var rightRow = right.Rows[rightIndex];
                var values = Enumerable.Repeat(SqlValue.Null, leftWidth)
                    .Concat(rightRow.Values)
                    .ToArray();
                rows.Add(new SourceRow(
                    columns,
                    values,
                    CombineQualifiedColumns(leftQualifiedColumns, rightRow.QualifiedColumns, leftWidth),
                    outerRow,
                    outputColumns));
                if (maximumRows is not null && rows.Count >= maximumRows.Value)
                    return new SourceData(columns, rows);
            }
        }

        return new SourceData(columns, rows);
    }

    private static IReadOnlyList<(OutputColumn Left, OutputColumn Right)> BuildJoinPairs(
        JoinTableSource source,
        QueryContext context)
    {
        var leftColumns = GetOutputColumns(source.Left, context);
        var rightColumns = GetOutputColumns(source.Right, context);
        IReadOnlyList<string> names;
        if (source.UsingColumns is { } usingColumns)
        {
            names = usingColumns;
        }
        else if (source.Natural)
        {
            var rightSet = new HashSet<string>(rightColumns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
            names = leftColumns
                .Select(column => column.Name)
                .Where(rightSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            return [];
        }

        var pairs = new List<(OutputColumn Left, OutputColumn Right)>(names.Count);
        foreach (var name in names)
        {
            var leftIndex = FindOutputColumnIndex(leftColumns, name);
            var rightIndex = FindOutputColumnIndex(rightColumns, name);
            if (leftIndex < 0 || rightIndex < 0)
                throw new EmbeddedSqlException($"cannot join using column {name} - column not present in both tables");

            pairs.Add((leftColumns[leftIndex], rightColumns[rightIndex]));
        }

        return pairs;
    }

    private static int FindOutputColumnIndex(IReadOnlyList<OutputColumn> columns, string name)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index].Name, name, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private bool JoinConditionMatches(
        JoinTableSource source,
        IReadOnlyList<(OutputColumn Left, OutputColumn Right)> joinPairs,
        SourceRow combinedRow,
        SourceRow leftRow,
        SourceRow rightRow,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (source.Condition is not null)
            return IsTrue(Evaluate(source.Condition, parameters, combinedRow, context));

        foreach (var (leftColumn, rightColumn) in joinPairs)
        {
            if (!IsTrue(EvaluateBinaryValues(
                    BinaryOperator.Equal,
                    GetOutputValue(leftRow, leftColumn),
                    GetOutputValue(rightRow, rightColumn))))
                return false;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, int> CombineQualifiedColumns(
        IReadOnlyDictionary<string, int>? left,
        IReadOnlyDictionary<string, int>? right,
        int rightOffset)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (left is not null)
        {
            foreach (var (name, index) in left)
                columns.TryAdd(name, index);
        }
        if (right is not null)
        {
            foreach (var (name, index) in right)
                columns.TryAdd(name, rightOffset + index);
        }

        return columns;
    }

    private static IReadOnlyDictionary<string, int> GetQualifiedColumns(
        TableSource source,
        QueryContext context)
    {
        return source switch
        {
            NamedTableSource named when IsSchemaTable(named.Name) => BuildQualifiedColumns(
                named.Alias ?? named.Name,
                ["type", "name", "tbl_name", "rootpage", "sql"]),
            NamedTableSource named when context.CommonTableExpressions.TryGetValue(named.Name, out var commonTableExpression)
                => BuildQualifiedColumns(named.Alias ?? named.Name, commonTableExpression.Columns),
            NamedTableSource named when TryGetView(context, named.Name, out var view)
                => BuildQualifiedColumns(named.Alias ?? view.Name, ResolveViewColumns(view, EnterView(context, view.Name))),
            NamedTableSource named => BuildQualifiedColumns(
                named.Alias ?? named.Name,
                GetTable(named, context.Tables).Columns),
            GenerateSeriesSource => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            DerivedTableSource derived when derived.Alias is not null
                => BuildQualifiedColumns(derived.Alias, DescribeQuery(derived.Query, context)),
            DerivedTableSource => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            JoinTableSource join => CombineQualifiedColumns(
                GetQualifiedColumns(join.Left, context),
                GetQualifiedColumns(join.Right, context),
                GetSourceColumns(join.Left, context).Length),
            _ => throw new EmbeddedSqlException($"Unsupported table source {source.GetType().Name}."),
        };
    }

    private static IReadOnlyDictionary<string, int> BuildQualifiedColumns(
        string qualifier,
        IReadOnlyList<string> columns)
    {
        var qualifiedColumns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columns.Count; index++)
            qualifiedColumns.TryAdd($"{qualifier}.{columns[index]}", index);

        return qualifiedColumns;
    }

    private static EmbeddedTable GetTable(NamedTableSource source, Dictionary<string, EmbeddedTable> tables)
    {
        if (!tables.TryGetValue(source.Name, out var table))
            throw new EmbeddedSqlException($"no such table: {source.Name}");

        return table;
    }

    private static bool IsSchemaTable(string name)
        => string.Equals(name, "sqlite_master", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "sqlite_schema", StringComparison.OrdinalIgnoreCase);

    private static SourceData GetNamedTableRows(
        NamedTableSource source,
        QueryContext context,
        long? maximumRows,
        SourceRow? outerRow)
    {
        if (IsSchemaTable(source.Name))
            return GetSchemaTableRows(source, context, outerRow);

        var table = GetTable(source, context.Tables);
        var qualifier = source.Alias ?? source.Name;
        var qualifiedColumns = BuildQualifiedColumns(qualifier, table.Columns);
        var count = table.Rows.Count;
        if (maximumRows is { } maximum && maximum < count)
            count = (int)maximum;

        var sourceRows = new SourceRow[count];
        for (var index = 0; index < count; index++)
        {
            var rowid = index < table.RowIds.Count ? table.RowIds[index] : index + 1;
            sourceRows[index] = new SourceRow(
                table.Columns,
                table.Rows[index],
                qualifiedColumns,
                outerRow,
                RowId: table.HasRowid ? rowid : null,
                RowIdQualifier: qualifier);
        }

        return new SourceData(table.Columns, sourceRows);
    }

    private static SourceData GetSchemaTableRows(
        NamedTableSource source,
        QueryContext context,
        SourceRow? outerRow)
    {
        var tables = context.Tables;
        var columns = new[] { "type", "name", "tbl_name", "rootpage", "sql" };
        var qualifiedColumns = BuildQualifiedColumns(source.Alias ?? source.Name, columns);
        var rows = new List<SourceRow>();
        foreach (var entry in tables.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new SourceRow(
                columns,
                [
                    SqlValue.Text("table"),
                    SqlValue.Text(entry.Key),
                    SqlValue.Text(entry.Key),
                    SqlValue.Integer(0),
                    SqlValue.Text(BuildCreateTableSql(entry.Key, entry.Value)),
                ],
                qualifiedColumns,
                outerRow));

            foreach (var index in entry.Value.Indexes)
            {
                rows.Add(new SourceRow(
                    columns,
                    [
                        SqlValue.Text("index"),
                        SqlValue.Text(index.Name),
                        SqlValue.Text(entry.Key),
                        SqlValue.Integer(0),
                        index.Origin == EmbeddedIndexOrigin.Explicit
                            ? SqlValue.Text(BuildCreateIndexSql(entry.Key, index))
                            : SqlValue.Null,
                    ],
                    qualifiedColumns,
                    outerRow));
            }
        }

        if (context.Views is not null)
        {
            foreach (var entry in context.Views.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new SourceRow(
                    columns,
                    [
                        SqlValue.Text("view"),
                        SqlValue.Text(entry.Key),
                        SqlValue.Text(entry.Key),
                        SqlValue.Integer(0),
                        SqlValue.Text(entry.Value.Sql),
                    ],
                    qualifiedColumns,
                    outerRow));
            }
        }

        if (context.Triggers is not null)
        {
            foreach (var entry in context.Triggers.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new SourceRow(
                    columns,
                    [
                        SqlValue.Text("trigger"),
                        SqlValue.Text(entry.Key),
                        SqlValue.Text(entry.Value.TableName),
                        SqlValue.Integer(0),
                        SqlValue.Text(entry.Value.Sql),
                    ],
                    qualifiedColumns,
                    outerRow));
            }
        }

        return new SourceData(columns, rows);
    }

    private static SourceData GetCommonTableExpressionRows(
        NamedTableSource source,
        SourceData commonTableExpression,
        SourceRow? outerRow,
        long? maximumRows)
    {
        var rows = commonTableExpression.Rows.AsEnumerable();
        if (maximumRows is { } maximum && maximum < commonTableExpression.Rows.Count)
            rows = rows.Take((int)maximum);

        var qualifiedColumns = BuildQualifiedColumns(source.Alias ?? source.Name, commonTableExpression.Columns);
        return new SourceData(
            commonTableExpression.Columns,
            rows.Select(row => new SourceRow(
                commonTableExpression.Columns,
                row.Values.ToArray(),
                qualifiedColumns,
                outerRow)).ToArray());
    }

    private SourceData GetViewRows(
        NamedTableSource source,
        ViewDefinition view,
        SqlValue[] parameters,
        QueryContext context,
        long? maximumRows,
        SourceRow? outerRow)
    {
        var viewContext = EnterView(context, view.Name);
        var result = MaterializeQueryResult(
            ExecuteQuery(view.Query, parameters, viewContext, outerRow));
        var columns = ApplyViewColumnNames(view, result.Columns);
        var rows = result.Rows.AsEnumerable();
        if (maximumRows is { } maximum && maximum < result.Rows.Count)
            rows = rows.Take((int)maximum);

        var qualifiedColumns = BuildQualifiedColumns(source.Alias ?? view.Name, columns);
        return new SourceData(
            columns,
            rows.Select(row => new SourceRow(columns, row.ToArray(), qualifiedColumns, outerRow)).ToArray());
    }

    private static bool TryGetView(QueryContext context, string name, out ViewDefinition view)
    {
        if (context.Views is not null && context.Views.TryGetValue(name, out var found))
        {
            view = found;
            return true;
        }

        view = null!;
        return false;
    }

    // Enters a view's resolution scope, guarding against direct or mutual recursion and
    // isolating the body from the enclosing query's CTEs while retaining the catalog.
    private static QueryContext EnterView(QueryContext context, string viewName)
    {
        EnsureNoViewRecursion(context, viewName);
        var expanding = context.ExpandingViews is null
            ? new List<string>()
            : new List<string>(context.ExpandingViews);
        expanding.Add(viewName);
        return context with
        {
            CommonTableExpressions = new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
            ExpandingViews = expanding,
        };
    }

    private static void EnsureNoViewRecursion(QueryContext context, string viewName)
    {
        if (context.ExpandingViews is not null
            && context.ExpandingViews.Any(name => string.Equals(name, viewName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new EmbeddedSqlException($"view {viewName} is circularly defined");
        }
    }

    private static string[] ResolveViewColumns(ViewDefinition view, QueryContext viewContext)
        => ApplyViewColumnNames(view, DescribeQuery(view.Query, viewContext));

    private static string[] ApplyViewColumnNames(ViewDefinition view, string[] queryColumns)
    {
        if (view.Columns is null)
            return queryColumns;
        if (view.Columns.Count != queryColumns.Length)
        {
            throw new EmbeddedSqlException(
                $"expected {view.Columns.Count} columns for {view.Name} but got {queryColumns.Length}");
        }

        return view.Columns.ToArray();
    }

    private SourceData GetDerivedTableRows(
        DerivedTableSource source,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        long? maximumRows)
    {
        var result = MaterializeQueryResult(
            ExecuteQuery(source.Query, parameters, context, outerRow));
        var rows = result.Rows.AsEnumerable();
        if (maximumRows is { } maximum && maximum < result.Rows.Count)
            rows = rows.Take((int)maximum);

        var qualifiedColumns = source.Alias is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : BuildQualifiedColumns(source.Alias, result.Columns);
        return new SourceData(
            result.Columns,
            rows.Select(row => new SourceRow(result.Columns, row.ToArray(), qualifiedColumns, outerRow)).ToArray());
    }

    internal static string BuildCreateTableSql(string name, EmbeddedTable table)
    {
        var columns = table.ColumnDefinitions.Select(column =>
        {
            var definition = QuoteIdentifier(column.Name);
            if (!string.IsNullOrEmpty(column.DeclaredType))
                definition += " " + column.DeclaredType;
            if (column.Collation is { } collation)
            {
                definition += FormatConstraintName(column.CollationConstraintName)
                    + " COLLATE "
                    + collation;
            }

            // A generated column carries its expression instead of PRIMARY KEY / DEFAULT
            // markers (which SQLite forbids on generated columns); NOT NULL and UNIQUE still apply.
            if (column.IsGenerated)
            {
                definition += FormatConstraintName(column.GenerationConstraintName)
                    + (column.GenerationAlways ? " GENERATED ALWAYS" : string.Empty)
                    + $" AS ({column.GenerationSql}) "
                    + (column.GeneratedStored ? "STORED" : "VIRTUAL");
                if (column.NotNull)
                {
                    definition += FormatConstraintName(column.NotNullConstraintName)
                        + " NOT NULL"
                        + FormatConflictClause(column.NotNullConflictAlgorithm);
                }
                if (column.ExplicitNull)
                {
                    definition += FormatConstraintName(column.NullConstraintName) + " NULL";
                }
                if (column.Unique)
                {
                    definition += FormatConstraintName(column.UniqueConstraintName)
                        + " UNIQUE"
                        + FormatConflictClause(column.UniqueConflictAlgorithm);
                }
                foreach (var check in column.CheckConstraints)
                    definition += FormatCheckConstraint(check);
                return definition;
            }

            if (column.PrimaryKey)
            {
                definition += FormatConstraintName(column.PrimaryKeyConstraintName)
                    + (column.PrimaryKeyDescending ? " PRIMARY KEY DESC" : " PRIMARY KEY")
                    + FormatConflictClause(column.PrimaryKeyConflictAlgorithm);
            }
            if (column.NotNull)
            {
                definition += FormatConstraintName(column.NotNullConstraintName)
                    + " NOT NULL"
                    + FormatConflictClause(column.NotNullConflictAlgorithm);
            }
            if (column.ExplicitNull)
            {
                definition += FormatConstraintName(column.NullConstraintName) + " NULL";
            }
            if (column.Unique)
            {
                definition += FormatConstraintName(column.UniqueConstraintName)
                    + " UNIQUE"
                    + FormatConflictClause(column.UniqueConflictAlgorithm);
            }
            if (column.HasDefault)
            {
                definition += FormatConstraintName(column.DefaultConstraintName)
                    + " DEFAULT "
                    + (column.DefaultSql
                        ?? FormatSqlLiteral(column.DefaultValue
                            ?? throw new InvalidOperationException("Default metadata is incomplete.")));
            }
            if (column.ForeignKey is { } foreignKey)
            {
                definition += FormatConstraintName(column.ForeignKeyConstraintName)
                    + $" REFERENCES {QuoteIdentifier(foreignKey.ParentTable)}"
                    + $" ({QuoteIdentifier(foreignKey.ParentColumn)})";
            }
            foreach (var check in column.CheckConstraints)
                definition += FormatCheckConstraint(check);
            return definition;
        }).ToList();

        var tableKeyConstraints = new List<(int DeclarationOrder, int FallbackOrder, string Sql)>();
        if (table.TableLevelPrimaryKey is { } tablePrimaryKey)
        {
            var keyColumns = tablePrimaryKey.Select(keyColumn =>
                QuoteIdentifier(keyColumn.Name)
                + (keyColumn.Collation is { } collation ? " COLLATE " + collation : string.Empty)
                + (keyColumn.Descending ? " DESC" : string.Empty));
            tableKeyConstraints.Add((
                table.TablePrimaryKeyDeclarationOrder ?? -1,
                0,
                FormatConstraintName(table.TablePrimaryKeyConstraintName).TrimStart()
                + (table.TablePrimaryKeyConstraintName is null ? string.Empty : " ")
                + $"PRIMARY KEY ({string.Join(", ", keyColumns)})"
                + FormatConflictClause(table.TablePrimaryKeyConflictAlgorithm)));
        }

        for (var index = 0; index < table.TableUniqueConstraints.Count; index++)
        {
            var unique = table.TableUniqueConstraints[index];
            var keyColumns = unique.Columns.Select(keyColumn =>
                QuoteIdentifier(keyColumn.Name)
                + (keyColumn.Collation is { } collation ? " COLLATE " + collation : string.Empty)
                + (keyColumn.Descending ? " DESC" : string.Empty));
            tableKeyConstraints.Add((
                unique.DeclarationOrder,
                index + 1,
                FormatConstraintName(unique.Name).TrimStart()
                + (unique.Name is null ? string.Empty : " ")
                + $"UNIQUE ({string.Join(", ", keyColumns)})"
                + FormatConflictClause(unique.ConflictAlgorithm)));
        }

        columns.AddRange(tableKeyConstraints
            .OrderBy(constraint => constraint.DeclarationOrder)
            .ThenBy(constraint => constraint.FallbackOrder)
            .Select(constraint => constraint.Sql));

        foreach (var check in table.CheckConstraints)
            columns.Add(FormatCheckConstraint(check).TrimStart());

        var withoutRowid = table.WithoutRowid ? " WITHOUT ROWID" : string.Empty;
        return $"CREATE TABLE {QuoteIdentifier(name)} ({string.Join(", ", columns)}){withoutRowid}";
    }

    private static string FormatConstraintName(string? name)
        => name is null ? string.Empty : " CONSTRAINT " + QuoteIdentifier(name);

    private static string FormatCheckConstraint(CheckConstraint check)
        => FormatConstraintName(check.Name)
            + $" CHECK ({check.Sql})"
            + FormatConflictClause(check.ConflictAlgorithm);

    private static string FormatConflictClause(InsertConflictAlgorithm? algorithm)
        => algorithm is null ? string.Empty : " ON CONFLICT " + algorithm.Value.ToString().ToUpperInvariant();

    private static string BuildCreateIndexSql(string tableName, EmbeddedIndex index)
    {
        var columns = index.Columns.Select(column =>
        {
            var definition = QuoteIdentifier(column.Name);
            if (column.Collation is { } collation)
                definition += " COLLATE " + collation;
            if (column.Descending)
                definition += " DESC";
            return definition;
        });
        var unique = index.Unique ? "UNIQUE " : string.Empty;
        return $"CREATE {unique}INDEX {QuoteIdentifier(index.Name)} ON {QuoteIdentifier(tableName)} ({string.Join(", ", columns)})";
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string FormatSqlLiteral(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => "NULL",
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Text => "'" + value.AsText().Replace("'", "''", StringComparison.Ordinal) + "'",
            SqlValueKind.Blob => "X'" + Convert.ToHexString(value.AsBlob().Span) + "'",
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };
    }

    private SourceData GetSeriesRows(
        GenerateSeriesSource source,
        SqlValue[] parameters,
        QueryContext context,
        long? maximumRows,
        SourceRow? outerRow)
    {
        var start = RequireInteger(Evaluate(source.Start, parameters, outerRow, context));
        var stop = RequireInteger(Evaluate(source.Stop, parameters, outerRow, context));
        var step = RequireInteger(Evaluate(source.Step, parameters, outerRow, context));
        if (step == 0)
            throw new EmbeddedSqlException("generate_series() step must not be zero");

        var rows = new List<SourceRow>();
        if (step > 0)
        {
            for (var current = start; current <= stop && (maximumRows is null || rows.Count < maximumRows.Value);)
            {
                rows.Add(new SourceRow(["value"], [SqlValue.Integer(current)], Parent: outerRow));
                if (current > long.MaxValue - step)
                    break;

                current += step;
            }
        }
        else
        {
            for (var current = start; current >= stop && (maximumRows is null || rows.Count < maximumRows.Value);)
            {
                rows.Add(new SourceRow(["value"], [SqlValue.Integer(current)], Parent: outerRow));
                if (current < long.MinValue - step)
                    break;

                current += step;
            }
        }

        return new SourceData(["value"], rows);
    }

    private static SqlValue GetOutputValue(SourceRow row, OutputColumn column)
    {
        var value = row.Values[column.Index];
        if (column.CoalesceIndex is { } coalesceIndex && value.Kind == SqlValueKind.Null)
            return row.Values[coalesceIndex];

        return value;
    }

    private static string[] GetColumnNames(
        IReadOnlyList<Projection> projections,
        IReadOnlyList<OutputColumn> outputColumns,
        IReadOnlyList<OutputColumn>? rawOutputColumns = null)
    {
        var names = new List<string>();
        foreach (var projection in projections)
        {
            if (projection.Expression is StarExpression)
            {
                if (outputColumns.Count == 0)
                    throw new EmbeddedSqlException("SELECT * requires a row source");

                names.AddRange(outputColumns.Select(column => column.Name));
                continue;
            }

            if (projection.Expression is QualifiedStarExpression qualifiedStar)
            {
                var rawMatches = (rawOutputColumns ?? outputColumns)
                    .Where(column => string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (rawMatches.Length == 0)
                    throw new EmbeddedSqlException($"no such table: {qualifiedStar.Qualifier}");

                names.AddRange(rawMatches.Select(column => column.Name));
                continue;
            }

            names.Add(projection.Alias ?? GetExpressionName(projection.Expression));
        }

        return names.ToArray();
    }

    internal static string[] DescribeQuery(QueryStatement statement, QueryContext context)
    {
        return statement switch
        {
            SelectStatement select => GetColumnNames(
                select.Projections,
                GetOutputColumns(select.Source, context),
                GetRawOutputColumns(select.Source, context)),
            CompoundSelectStatement compound => DescribeQuery(compound.Terms[0], context),
            WithSelectStatement with => DescribeWithSelect(with, context),
            ValuesClause values => DescribeValues(values),
            _ => throw new EmbeddedSqlException($"Unsupported query type {statement.GetType().Name}."),
        };
    }

    private static string[] DescribeValues(ValuesClause values)
    {
        var columns = new string[values.Rows[0].Count];
        for (var index = 0; index < columns.Length; index++)
            columns[index] = $"column{index + 1}";
        return columns;
    }

    private static string[] DescribeWithSelect(WithSelectStatement statement, QueryContext context)
    {
        var commonTableExpressions = new Dictionary<string, SourceData>(
            context.CommonTableExpressions,
            StringComparer.OrdinalIgnoreCase);
        var namesInCurrentClause = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var commonTableExpression in statement.CommonTableExpressions)
        {
            if (!namesInCurrentClause.Add(commonTableExpression.Name))
                throw new EmbeddedSqlException($"duplicate WITH table name: {commonTableExpression.Name}");

            var columns = ResolveCommonTableExpressionColumns(
                commonTableExpression,
                DescribeQuery(
                    commonTableExpression.Query,
                    context with { CommonTableExpressions = commonTableExpressions }));
            commonTableExpressions[commonTableExpression.Name] = new SourceData(columns, []);
        }

        return DescribeQuery(
            statement.Query,
            context with { CommonTableExpressions = commonTableExpressions });
    }

    private static string GetExpressionName(Expression expression)
    {
        return expression switch
        {
            ColumnExpression column => column.Name[(column.Name.LastIndexOf('.') + 1)..],
            ParameterExpression parameter => $"?{parameter.Index}",
            FunctionExpression function => function.Name,
            _ => expression.ToString() ?? string.Empty,
        };
    }

    private SqlValue Evaluate(
        Expression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var result = expression switch
        {
            LiteralExpression literal => literal.Value,
            CurrentTimeExpression current => current.Kind switch
            {
                CurrentTimeKind.Date => SqliteDateTime.Execute([], SqliteDateTime.Func.Date),
                CurrentTimeKind.Time => SqliteDateTime.Execute([], SqliteDateTime.Func.Time),
                CurrentTimeKind.Timestamp => SqliteDateTime.Execute([], SqliteDateTime.Func.DateTime),
                _ => throw new InvalidOperationException($"Unknown current-time kind {current.Kind}."),
            },
            ParameterExpression parameter => ReadParameter(parameters, parameter.Index),
            ColumnExpression column => row?.GetValue(column)
                ?? throw new EmbeddedSqlException($"no such column: {column.Name}"),
            FunctionExpression function => EvaluateScalarFunction(function, parameters, row, context),
            ScalarSubqueryExpression subquery => EvaluateScalarSubquery(subquery, parameters, row, context),
            ExistsExpression exists => EvaluateExists(exists, parameters, row, context),
            CollationExpression collation => Evaluate(collation.Expression, parameters, row, context),
            CastExpression cast => EvaluateCast(cast, parameters, row, context),
            CaseExpression @case => EvaluateCase(@case, parameters, row, context),
            LikeExpression like => EvaluateLike(like, parameters, row, context),
            GlobExpression glob => EvaluateGlob(glob, parameters, row, context),
            InExpression @in => EvaluateIn(@in, parameters, row, context),
            InSubqueryExpression @in => EvaluateInSubquery(@in, parameters, row, context),
            BetweenExpression between => EvaluateBetween(between, parameters, row, context),
            UnaryExpression unary => EvaluateUnary(unary, parameters, row, context),
            BinaryExpression binary => EvaluateBinary(binary, parameters, row, context),
            QualifiedStarExpression => throw new EmbeddedSqlException("row value misused"),
            _ => throw new EmbeddedSqlException($"Unsupported expression type {expression.GetType().Name}."),
        };
        context.CancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private SqlValue EvaluateScalarSubquery(
        ScalarSubqueryExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var result = ExecuteQuery(expression.Query, parameters, context, row);
        RequireSingleColumnSubquery(result);
        return result.Rows.Count == 0 ? SqlValue.Null : result.Rows[0][0];
    }

    private SqlValue EvaluateExists(
        ExistsExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var exists = ExecuteQuery(expression.Query, parameters, context, row).Rows.Count > 0;
        return SqlValue.Integer(exists == expression.Negated ? 0 : 1);
    }

    private SqlValue EvaluateInSubquery(
        InSubqueryExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var value = Evaluate(expression.Value, parameters, row, context);
        var result = ExecuteQuery(expression.Query, parameters, context, row);
        RequireSingleColumnSubquery(result);
        return EvaluateInValues(value, result.Rows.Select(resultRow => resultRow[0]), expression.Negated);
    }

    private static void RequireSingleColumnSubquery(ExecutionResult result)
    {
        if (result.Columns.Length != 1)
            throw new EmbeddedSqlException($"sub-select returns {result.Columns.Length} columns - expected 1");
    }

    private SqlValue EvaluateBinary(
        BinaryExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var left = Evaluate(expression.Left, parameters, row, context);
        var right = Evaluate(expression.Right, parameters, row, context);
        return EvaluateBinaryValues(
            expression.Operator,
            left,
            right,
            GetCollation(expression.Left) ?? GetCollation(expression.Right));
    }

    private SqlValue EvaluateBinaryValues(
        BinaryOperator operation,
        SqlValue left,
        SqlValue right,
        string? collation = null)
    {
        if (operation is BinaryOperator.Is or BinaryOperator.IsNot)
        {
            var equal = left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null
                ? left.Kind == right.Kind
                : Compare(left, right, collation) == 0;
            return SqlValue.Integer((operation == BinaryOperator.Is) == equal ? 1 : 0);
        }
        if (operation is BinaryOperator.And or BinaryOperator.Or)
            return ApplyLogical(operation, left, right);

        if (left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        return operation switch
        {
            BinaryOperator.Add => ApplyNumeric(left, right, static (a, b) => checked(a + b), static (a, b) => a + b),
            BinaryOperator.Subtract => ApplyNumeric(left, right, static (a, b) => checked(a - b), static (a, b) => a - b),
            BinaryOperator.Multiply => ApplyNumeric(left, right, static (a, b) => checked(a * b), static (a, b) => a * b),
            BinaryOperator.Divide => ApplyDivision(left, right),
            BinaryOperator.Modulo => ApplyModulo(left, right),
            BinaryOperator.Concatenate => ApplyConcatenation(left, right),
            BinaryOperator.JsonArrow => SqliteJson.JsonArrow(left, right, textResult: false),
            BinaryOperator.JsonArrowText => SqliteJson.JsonArrow(left, right, textResult: true),
            BinaryOperator.Equal => SqlValue.Integer(Compare(left, right, collation) == 0 ? 1 : 0),
            BinaryOperator.NotEqual => SqlValue.Integer(Compare(left, right, collation) != 0 ? 1 : 0),
            BinaryOperator.LessThan => SqlValue.Integer(Compare(left, right, collation) < 0 ? 1 : 0),
            BinaryOperator.LessThanOrEqual => SqlValue.Integer(Compare(left, right, collation) <= 0 ? 1 : 0),
            BinaryOperator.GreaterThan => SqlValue.Integer(Compare(left, right, collation) > 0 ? 1 : 0),
            BinaryOperator.GreaterThanOrEqual => SqlValue.Integer(Compare(left, right, collation) >= 0 ? 1 : 0),
            _ => throw new EmbeddedSqlException($"Unsupported binary operator {operation}."),
        };
    }

    private SqlValue EvaluateUnary(
        UnaryExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var value = Evaluate(expression.Operand, parameters, row, context);
        return expression.Operator switch
        {
            UnaryOperator.Not => value.Kind == SqlValueKind.Null
                ? SqlValue.Null
                : SqlValue.Integer(IsTrue(value) ? 0 : 1),
            _ => throw new EmbeddedSqlException($"Unsupported unary operator {expression.Operator}."),
        };
    }

    private SqlValue EvaluateCast(
        CastExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
        => CastValue(Evaluate(expression.Expression, parameters, row, context), expression.TypeName);

    private SqlValue EvaluateCase(
        CaseExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var operand = expression.Operand is null
            ? (SqlValue?)null
            : Evaluate(expression.Operand, parameters, row, context);
        foreach (var clause in expression.Clauses)
        {
            var when = Evaluate(clause.When, parameters, row, context);
            var matches = operand is null
                ? IsTrue(when)
                : operand.Value.Kind != SqlValueKind.Null
                    && when.Kind != SqlValueKind.Null
                    && Compare(operand.Value, when) == 0;
            if (matches)
                return Evaluate(clause.Then, parameters, row, context);
        }

        return expression.Else is null
            ? SqlValue.Null
            : Evaluate(expression.Else, parameters, row, context);
    }

    private static SqlValue CastValue(SqlValue value, string typeName)
    {
        if (value.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        typeName = typeName.ToUpperInvariant();
        if (typeName.Contains("INT", StringComparison.Ordinal))
        {
            var numeric = ApplyNumericAffinity(value);
            return numeric.Kind switch
            {
                SqlValueKind.Integer => numeric,
                SqlValueKind.Real => SqlValue.Integer((long)Math.Clamp(
                    Math.Truncate(numeric.AsReal()),
                    long.MinValue,
                    long.MaxValue)),
                _ => throw new InvalidOperationException($"Unexpected numeric value {numeric.Kind}."),
            };
        }
        if (typeName is "REAL" or "FLOAT" or "DOUBLE"
            || typeName.Contains("REAL", StringComparison.Ordinal)
            || typeName.Contains("FLOA", StringComparison.Ordinal)
            || typeName.Contains("DOUB", StringComparison.Ordinal))
        {
            var numeric = ApplyNumericAffinity(value);
            return numeric.Kind == SqlValueKind.Real
                ? numeric
                : SqlValue.Real(numeric.AsInteger());
        }
        if (typeName is "NUMERIC" or "DECIMAL" or "BOOLEAN" or "DATE" or "DATETIME"
            || typeName.Contains("NUM", StringComparison.Ordinal)
            || typeName.Contains("DEC", StringComparison.Ordinal)
            || typeName.Contains("BOOL", StringComparison.Ordinal)
            || typeName.Contains("DATE", StringComparison.Ordinal))
        {
            return ApplyNumericAffinity(value);
        }
        if (typeName is "BLOB" or "NONE")
            return value.Kind == SqlValueKind.Blob
                ? value
                : SqlValue.Blob(System.Text.Encoding.UTF8.GetBytes(ToSqlText(value)));

        return value.Kind == SqlValueKind.Text
            ? value
            : SqlValue.Text(ToSqlText(value));
    }

    private SqlValue EvaluateLike(
        LikeExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var value = Evaluate(expression.Value, parameters, row, context);
        var pattern = Evaluate(expression.Pattern, parameters, row, context);
        var escape = expression.Escape is null
            ? (SqlValue?)null
            : Evaluate(expression.Escape, parameters, row, context);
        return EvaluateLikeValues(value, pattern, escape, expression.Negated);
    }

    private static SqlValue EvaluateLikeValues(SqlValue value, SqlValue pattern, SqlValue? escapeValue, bool negated)
    {
        if (value.Kind == SqlValueKind.Null || pattern.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        char? escape = null;
        if (escapeValue is not null)
        {
            if (escapeValue.Value.Kind == SqlValueKind.Null)
                return SqlValue.Null;

            var escapeText = ToSqlText(escapeValue.Value);
            if (escapeText.Length != 1)
                throw new EmbeddedSqlException("ESCAPE expression must be a single character");
            escape = escapeText[0];
        }

        var matches = IsLikeMatch(ToSqlText(value), ToSqlText(pattern), escape);
        return SqlValue.Integer(matches == negated ? 0 : 1);
    }

    private SqlValue EvaluateGlob(
        GlobExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var value = Evaluate(expression.Value, parameters, row, context);
        var pattern = Evaluate(expression.Pattern, parameters, row, context);
        return EvaluateGlobValues(value, pattern, expression.Negated);
    }

    private static SqlValue EvaluateGlobValues(SqlValue value, SqlValue pattern, bool negated)
    {
        if (value.Kind == SqlValueKind.Null || pattern.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var matches = IsGlobMatch(ToSqlText(value), ToSqlText(pattern));
        return SqlValue.Integer(matches == negated ? 0 : 1);
    }

    private static int[] ToCodePoints(string text)
    {
        var result = new List<int>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index])
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                result.Add(char.ConvertToUtf32(text[index], text[index + 1]));
                index++;
            }
            else
            {
                result.Add(text[index]);
            }
        }

        return result.ToArray();
    }

    private static bool IsGlobMatch(string text, string pattern)
    {
        return GlobMatch(ToCodePoints(pattern), 0, ToCodePoints(text), 0);
    }

    private static bool GlobMatch(int[] pattern, int patternIndex, int[] text, int textIndex)
    {
        while (patternIndex < pattern.Length)
        {
            var patternChar = pattern[patternIndex];
            if (patternChar == '*')
            {
                patternIndex++;
                while (patternIndex < pattern.Length && (pattern[patternIndex] == '*' || pattern[patternIndex] == '?'))
                {
                    if (pattern[patternIndex] == '?')
                    {
                        if (textIndex >= text.Length)
                            return false;

                        textIndex++;
                    }

                    patternIndex++;
                }

                if (patternIndex == pattern.Length)
                    return true;

                for (var scan = textIndex; scan <= text.Length; scan++)
                {
                    if (GlobMatch(pattern, patternIndex, text, scan))
                        return true;
                }

                return false;
            }

            if (patternChar == '?')
            {
                if (textIndex >= text.Length)
                    return false;

                textIndex++;
                patternIndex++;
                continue;
            }

            if (patternChar == '[')
            {
                if (textIndex >= text.Length)
                    return false;

                var (matched, nextIndex) = MatchGlobSet(pattern, patternIndex, text[textIndex]);
                if (nextIndex < 0)
                    return false;

                if (!matched)
                    return false;

                textIndex++;
                patternIndex = nextIndex;
                continue;
            }

            if (textIndex >= text.Length || text[textIndex] != patternChar)
                return false;

            textIndex++;
            patternIndex++;
        }

        return textIndex == text.Length;
    }

    private static (bool Matched, int NextIndex) MatchGlobSet(int[] pattern, int start, int ch)
    {
        var index = start + 1;
        var invert = false;
        if (index < pattern.Length && pattern[index] == '^')
        {
            invert = true;
            index++;
        }

        var matched = false;
        var first = true;
        while (index < pattern.Length)
        {
            var current = pattern[index];
            if (current == ']' && !first)
                return (invert ? !matched : matched, index + 1);

            first = false;
            if (index + 2 < pattern.Length && pattern[index + 1] == '-' && pattern[index + 2] != ']')
            {
                if (ch >= current && ch <= pattern[index + 2])
                    matched = true;

                index += 3;
            }
            else
            {
                if (ch == current)
                    matched = true;

                index++;
            }
        }

        return (false, -1);
    }

    private SqlValue EvaluateIn(
        InExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var value = Evaluate(expression.Value, parameters, row, context);
        return EvaluateInValues(
            value,
            expression.Values.Select(candidate => Evaluate(candidate, parameters, row, context)),
            expression.Negated);
    }

    private SqlValue EvaluateInValues(SqlValue value, IEnumerable<SqlValue> candidates, bool negated)
    {
        if (value.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var foundNull = false;
        foreach (var candidate in candidates)
        {
            if (candidate.Kind == SqlValueKind.Null)
            {
                foundNull = true;
                continue;
            }

            if (Compare(value, candidate) == 0)
                return SqlValue.Integer(negated ? 0 : 1);
        }

        if (foundNull)
            return SqlValue.Null;

        return SqlValue.Integer(negated ? 1 : 0);
    }

    private SqlValue EvaluateBetween(
        BetweenExpression expression,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        var value = Evaluate(expression.Value, parameters, row, context);
        var lower = Evaluate(expression.Lower, parameters, row, context);
        var upper = Evaluate(expression.Upper, parameters, row, context);
        return EvaluateBetweenValues(value, lower, upper, expression.Negated);
    }

    private SqlValue EvaluateBetweenValues(SqlValue value, SqlValue lower, SqlValue upper, bool negated)
    {
        var result = EvaluateBinaryValues(
            BinaryOperator.And,
            EvaluateBinaryValues(BinaryOperator.GreaterThanOrEqual, value, lower),
            EvaluateBinaryValues(BinaryOperator.LessThanOrEqual, value, upper));
        if (result.Kind == SqlValueKind.Null || !negated)
            return result;

        return SqlValue.Integer(result.AsInteger() == 0 ? 1 : 0);
    }

    private static bool IsLikeMatch(string value, string pattern, char? escape)
    {
        var valueIndex = 0;
        var patternIndex = 0;
        var wildcardIndex = -1;
        var wildcardMatchIndex = 0;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && escape is not null && pattern[patternIndex] == escape)
            {
                patternIndex++;
                if (patternIndex < pattern.Length && LikeCharactersEqual(value[valueIndex], pattern[patternIndex]))
                {
                    valueIndex++;
                    patternIndex++;
                    continue;
                }
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '_')
            {
                valueIndex++;
                patternIndex++;
                continue;
            }
            else if (patternIndex < pattern.Length && LikeCharactersEqual(value[valueIndex], pattern[patternIndex]))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '%')
            {
                wildcardIndex = patternIndex++;
                wildcardMatchIndex = valueIndex;
                continue;
            }

            if (wildcardIndex < 0)
                return false;

            patternIndex = wildcardIndex + 1;
            valueIndex = ++wildcardMatchIndex;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '%')
            patternIndex++;

        return patternIndex == pattern.Length;
    }

    private static bool LikeCharactersEqual(char left, char right)
    {
        return left == right
            || left <= 127 && right <= 127 && char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
    }

    private static SqlValue ApplyLogical(BinaryOperator operation, SqlValue left, SqlValue right)
    {
        if (operation == BinaryOperator.And)
        {
            if (left.Kind != SqlValueKind.Null && !IsTrue(left)
                || right.Kind != SqlValueKind.Null && !IsTrue(right))
            {
                return SqlValue.Integer(0);
            }

            return left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null
                ? SqlValue.Null
                : SqlValue.Integer(1);
        }

        if (left.Kind != SqlValueKind.Null && IsTrue(left)
            || right.Kind != SqlValueKind.Null && IsTrue(right))
        {
            return SqlValue.Integer(1);
        }

        return left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null
            ? SqlValue.Null
            : SqlValue.Integer(0);
    }

    private bool ContainsAggregate(Expression expression)
    {
        return expression switch
        {
            FunctionExpression function when function.Window is not null => false,
            FunctionExpression function when IsBuiltInAggregate(function) => true,
            FunctionExpression function => IsManagedPercentileAggregate(function.Name)
                || TryGetAggregateFunction(function.Name, function.Arguments.Count, out _)
                || function.Arguments.Any(ContainsAggregate),
            UnaryExpression unary => ContainsAggregate(unary.Operand),
            BinaryExpression binary => ContainsAggregate(binary.Left) || ContainsAggregate(binary.Right),
            CollationExpression collation => ContainsAggregate(collation.Expression),
            CastExpression cast => ContainsAggregate(cast.Expression),
            CaseExpression @case => (@case.Operand is not null && ContainsAggregate(@case.Operand))
                || @case.Clauses.Any(clause => ContainsAggregate(clause.When) || ContainsAggregate(clause.Then))
                || @case.Else is not null && ContainsAggregate(@case.Else),
            LikeExpression like => ContainsAggregate(like.Value)
                || ContainsAggregate(like.Pattern)
                || like.Escape is not null && ContainsAggregate(like.Escape),
            GlobExpression glob => ContainsAggregate(glob.Value) || ContainsAggregate(glob.Pattern),
            InExpression @in => ContainsAggregate(@in.Value) || @in.Values.Any(ContainsAggregate),
            InSubqueryExpression @in => ContainsAggregate(@in.Value),
            BetweenExpression between => ContainsAggregate(between.Value)
                || ContainsAggregate(between.Lower)
                || ContainsAggregate(between.Upper),
            _ => false,
        };
    }

    private bool IsAggregateExpression(Expression expression)
    {
        return expression switch
        {
            FunctionExpression function when function.Window is not null => false,
            FunctionExpression function when IsBuiltInAggregate(function) => true,
            FunctionExpression function => IsManagedPercentileAggregate(function.Name)
                || TryGetAggregateFunction(function.Name, function.Arguments.Count, out _),
            LiteralExpression or ParameterExpression or ScalarSubqueryExpression or ExistsExpression => true,
            BinaryExpression binary => IsAggregateExpression(binary.Left) && IsAggregateExpression(binary.Right),
            CollationExpression collation => IsAggregateExpression(collation.Expression),
            CastExpression cast => IsAggregateExpression(cast.Expression),
            CaseExpression @case => (@case.Operand is null || IsAggregateExpression(@case.Operand))
                && @case.Clauses.All(clause => IsAggregateExpression(clause.When) && IsAggregateExpression(clause.Then))
                && (@case.Else is null || IsAggregateExpression(@case.Else)),
            LikeExpression like => IsAggregateExpression(like.Value)
                && IsAggregateExpression(like.Pattern)
                && (like.Escape is null || IsAggregateExpression(like.Escape)),
            GlobExpression glob => IsAggregateExpression(glob.Value) && IsAggregateExpression(glob.Pattern),
            InExpression @in => IsAggregateExpression(@in.Value) && @in.Values.All(IsAggregateExpression),
            InSubqueryExpression @in => IsAggregateExpression(@in.Value),
            BetweenExpression between => IsAggregateExpression(between.Value)
                && IsAggregateExpression(between.Lower)
                && IsAggregateExpression(between.Upper),
            _ => false,
        };
    }

    private SourceRow? GetAggregateRepresentative(
        SelectStatement statement,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (rows.Count == 0)
            return null;

        FunctionExpression? controllingExtremum = null;
        foreach (var projection in statement.Projections)
            controllingExtremum = FindLastExtremumAggregate(projection.Expression) ?? controllingExtremum;
        foreach (var term in statement.OrderBy)
            controllingExtremum = FindLastExtremumAggregate(term.Expression) ?? controllingExtremum;
        if (statement.Having is not null)
            controllingExtremum = FindLastExtremumAggregate(statement.Having) ?? controllingExtremum;

        if (controllingExtremum is null)
            return rows[0];

        var effectiveRows = ApplyAggregateModifiers(controllingExtremum, rows, parameters, context);
        if (effectiveRows.Count == 0)
            return rows[0];

        var maximum = controllingExtremum.Name == "MAX";
        var extreme = SqlValue.Null;
        SourceRow? representative = null;
        foreach (var row in effectiveRows)
        {
            var value = Evaluate(controllingExtremum.Arguments[0], parameters, row, context);
            if (value.Kind == SqlValueKind.Null)
                continue;

            if (representative is null
                || (maximum ? Compare(value, extreme) > 0 : Compare(value, extreme) < 0))
            {
                extreme = value;
                representative = row;
            }
        }

        // SQLite visits every row for an all-NULL min/max and leaves the final row selected.
        return representative ?? effectiveRows[^1];
    }

    private static FunctionExpression? FindLastExtremumAggregate(Expression? expression)
    {
        return expression switch
        {
            FunctionExpression { Window: null, Name: "MIN" or "MAX", Arguments.Count: 1 } function
                => function,
            FunctionExpression { Window: null } function => function.Arguments
                .Reverse()
                .Select(FindLastExtremumAggregate)
                .FirstOrDefault(aggregate => aggregate is not null),
            UnaryExpression unary => FindLastExtremumAggregate(unary.Operand),
            BinaryExpression binary => FindLastExtremumAggregate(binary.Right)
                ?? FindLastExtremumAggregate(binary.Left),
            CollationExpression collation => FindLastExtremumAggregate(collation.Expression),
            CastExpression cast => FindLastExtremumAggregate(cast.Expression),
            CaseExpression @case => FindLastExtremumAggregate(@case.Else)
                ?? @case.Clauses
                    .Reverse()
                    .Select(clause => FindLastExtremumAggregate(clause.Then)
                        ?? FindLastExtremumAggregate(clause.When))
                    .FirstOrDefault(function => function is not null)
                ?? FindLastExtremumAggregate(@case.Operand),
            LikeExpression like => FindLastExtremumAggregate(like.Escape)
                ?? FindLastExtremumAggregate(like.Pattern)
                ?? FindLastExtremumAggregate(like.Value),
            GlobExpression glob => FindLastExtremumAggregate(glob.Pattern)
                ?? FindLastExtremumAggregate(glob.Value),
            InExpression @in => @in.Values
                    .Reverse()
                    .Select(FindLastExtremumAggregate)
                    .FirstOrDefault(function => function is not null)
                ?? FindLastExtremumAggregate(@in.Value),
            InSubqueryExpression @in => FindLastExtremumAggregate(@in.Value),
            BetweenExpression between => FindLastExtremumAggregate(between.Upper)
                ?? FindLastExtremumAggregate(between.Lower)
                ?? FindLastExtremumAggregate(between.Value),
            _ => null,
        };
    }

    private SqlValue EvaluateAggregate(
        Expression expression,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? representative = null)
    {
        return expression switch
        {
            FunctionExpression { Name: "COUNT" } function => EvaluateAggregateFunction(function, rows, parameters, context),
            FunctionExpression function when IsBuiltInAggregate(function)
                => EvaluateAggregateFunction(function, rows, parameters, context),
            FunctionExpression function when IsManagedPercentileAggregate(function.Name)
                => EvaluateAggregateFunction(function, rows, parameters, context),
            FunctionExpression function when TryGetAggregateFunction(function.Name, function.Arguments.Count, out _)
                => EvaluateAggregateFunction(function, rows, parameters, context),
            FunctionExpression function => EvaluateScalarFunction(
                function with
                {
                    Arguments = function.Arguments
                        .Select(argument => new LiteralExpression(
                            EvaluateAggregate(argument, rows, parameters, context, representative)))
                        .ToArray(),
                },
                parameters,
                representative,
                context),
            UnaryExpression unary => unary.Operator switch
            {
                UnaryOperator.Not => EvaluateAggregate(unary.Operand, rows, parameters, context, representative) is var value
                    && value.Kind == SqlValueKind.Null
                        ? SqlValue.Null
                        : SqlValue.Integer(IsTrue(value) ? 0 : 1),
                _ => throw new EmbeddedSqlException($"Unsupported unary operator {unary.Operator}."),
            },
            BinaryExpression binary => EvaluateBinaryValues(
                binary.Operator,
                EvaluateAggregate(binary.Left, rows, parameters, context, representative),
                EvaluateAggregate(binary.Right, rows, parameters, context, representative)),
            CollationExpression collation
                => EvaluateAggregate(collation.Expression, rows, parameters, context, representative),
            CastExpression cast => CastValue(
                EvaluateAggregate(cast.Expression, rows, parameters, context, representative),
                cast.TypeName),
            CaseExpression @case => EvaluateAggregateCase(@case, rows, parameters, context, representative),
            LikeExpression like => EvaluateLikeValues(
                EvaluateAggregate(like.Value, rows, parameters, context, representative),
                EvaluateAggregate(like.Pattern, rows, parameters, context, representative),
                like.Escape is null
                    ? null
                    : EvaluateAggregate(like.Escape, rows, parameters, context, representative),
                like.Negated),
            GlobExpression glob => EvaluateGlobValues(
                EvaluateAggregate(glob.Value, rows, parameters, context, representative),
                EvaluateAggregate(glob.Pattern, rows, parameters, context, representative),
                glob.Negated),
            InExpression @in => EvaluateInValues(
                EvaluateAggregate(@in.Value, rows, parameters, context, representative),
                @in.Values.Select(value => EvaluateAggregate(value, rows, parameters, context, representative)),
                @in.Negated),
            InSubqueryExpression @in => EvaluateInSubquery(
                @in with
                {
                    Value = new LiteralExpression(
                        EvaluateAggregate(@in.Value, rows, parameters, context, representative)),
                },
                parameters,
                representative,
                context),
            BetweenExpression between => EvaluateBetweenValues(
                EvaluateAggregate(between.Value, rows, parameters, context, representative),
                EvaluateAggregate(between.Lower, rows, parameters, context, representative),
                EvaluateAggregate(between.Upper, rows, parameters, context, representative),
                between.Negated),
            _ => rows.Count == 0 && expression is ColumnExpression
                ? SqlValue.Null
                : Evaluate(expression, parameters, representative ?? (rows.Count == 0 ? null : rows[0]), context),
        };
    }

    private SqlValue EvaluateAggregateFunction(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        var effectiveRows = ApplyAggregateModifiers(function, rows, parameters, context);
        if (string.Equals(function.Name, "COUNT", StringComparison.Ordinal))
            return EvaluateCount(function, effectiveRows, parameters, context);
        if (IsBuiltInAggregate(function))
            return EvaluateBuiltInAggregate(function, effectiveRows, parameters, context);
        if (IsManagedPercentileAggregate(function.Name))
            return EvaluatePercentileAggregate(function, effectiveRows, parameters, context);
        if (TryGetAggregateFunction(function.Name, function.Arguments.Count, out var aggregate))
            return EvaluateManagedAggregate(aggregate, function, effectiveRows, parameters, context);

        throw new EmbeddedSqlException($"Unsupported aggregate function {function.Name}().");
    }

    private IReadOnlyList<SourceRow> ApplyAggregateModifiers(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        var result = rows;
        if (function.Filter is not null)
        {
            result = result
                .Where(row => IsTrue(Evaluate(function.Filter, parameters, row, context)))
                .ToList();
        }

        if (function.Distinct)
        {
            if (function.Arguments.Count != 1)
                throw new EmbeddedSqlException("DISTINCT aggregates must have exactly one argument.");

            var collation = GetCollation(function.Arguments[0]);
            var seen = new List<SqlValue>();
            var deduplicated = new List<SourceRow>();
            foreach (var row in result)
            {
                var value = Evaluate(function.Arguments[0], parameters, row, context);
                if (seen.Any(existing => DistinctValuesEqual(existing, value, collation)))
                    continue;

                seen.Add(value);
                deduplicated.Add(row);
            }

            result = deduplicated;
        }

        return result;
    }

    private bool DistinctValuesEqual(SqlValue left, SqlValue right, string? collation)
    {
        if (left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null)
            return left.Kind == right.Kind;

        return Compare(left, right, collation) == 0;
    }

    private SqlValue EvaluateCount(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (function.CountStar || function.Arguments.Count == 0)
            return SqlValue.Integer(rows.Count);

        var count = 0L;
        foreach (var row in rows)
        {
            if (Evaluate(function.Arguments[0], parameters, row, context).Kind != SqlValueKind.Null)
                count++;
        }

        return SqlValue.Integer(count);
    }

    private static bool IsBuiltInAggregate(FunctionExpression function)
    {
        return function.Name switch
        {
            "COUNT" => function.CountStar || function.Arguments.Count is 0 or 1,
            "SUM" or "TOTAL" or "AVG" or "MIN" or "MAX" => function.Arguments.Count == 1,
            "GROUP_CONCAT" => function.Arguments.Count is 1 or 2,
            _ => false,
        };
    }

    private static bool IsManagedPercentileAggregate(string name)
    {
        return name.Equals("MEDIAN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PERCENTILE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PERCENTILE_CONT", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PERCENTILE_DISC", StringComparison.OrdinalIgnoreCase);
    }

    private SqlValue EvaluateBuiltInAggregate(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        return function.Name switch
        {
            "SUM" => EvaluateSum(function, rows, parameters, context, forceReal: false, average: false),
            "TOTAL" => EvaluateSum(function, rows, parameters, context, forceReal: true, average: false),
            "AVG" => EvaluateSum(function, rows, parameters, context, forceReal: true, average: true),
            "MIN" => EvaluateMinMax(function, rows, parameters, context, maximum: false),
            "MAX" => EvaluateMinMax(function, rows, parameters, context, maximum: true),
            "GROUP_CONCAT" => EvaluateGroupConcat(function, rows, parameters, context),
            _ => throw new EmbeddedSqlException($"Unsupported aggregate function {function.Name}()."),
        };
    }

    private SqlValue EvaluateSum(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context,
        bool forceReal,
        bool average)
    {
        RequireAggregateArgumentCount(function.Name.ToLowerInvariant(), function.Arguments, 1);
        var integerTotal = 0L;
        var realTotal = 0d;
        var hasReal = forceReal;
        var count = 0L;
        foreach (var row in rows)
        {
            var value = Evaluate(function.Arguments[0], parameters, row, context);
            if (value.Kind == SqlValueKind.Null)
                continue;

            var numeric = ApplyNumericAffinity(value);
            count++;
            if (numeric.Kind == SqlValueKind.Real)
            {
                if (!hasReal)
                {
                    realTotal = integerTotal;
                    hasReal = true;
                }

                realTotal += numeric.AsReal();
                continue;
            }

            if (hasReal)
            {
                realTotal += numeric.AsInteger();
                continue;
            }

            integerTotal = checked(integerTotal + numeric.AsInteger());
        }

        if (average)
            return count == 0 ? SqlValue.Null : SqlValue.Real(realTotal / count);
        if (count == 0)
            return forceReal ? SqlValue.Real(0) : SqlValue.Null;

        return hasReal ? SqlValue.Real(realTotal) : SqlValue.Integer(integerTotal);
    }

    private SqlValue EvaluateMinMax(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context,
        bool maximum)
    {
        RequireAggregateArgumentCount(function.Name.ToLowerInvariant(), function.Arguments, 1);
        var result = SqlValue.Null;
        foreach (var row in rows)
        {
            var value = Evaluate(function.Arguments[0], parameters, row, context);
            if (value.Kind == SqlValueKind.Null)
                continue;
            if (result.Kind == SqlValueKind.Null
                || (maximum ? Compare(value, result) > 0 : Compare(value, result) < 0))
            {
                result = value;
            }
        }

        return result;
    }

    private SqlValue EvaluateGroupConcat(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (function.Arguments.Count is < 1 or > 2)
            throw new EmbeddedSqlException("wrong number of arguments to function group_concat()");

        var result = new System.Text.StringBuilder();
        var hasValue = false;
        foreach (var row in rows)
        {
            var value = Evaluate(function.Arguments[0], parameters, row, context);
            if (value.Kind == SqlValueKind.Null)
                continue;

            if (hasValue)
            {
                var separator = function.Arguments.Count == 1
                    ? SqlValue.Text(",")
                    : Evaluate(function.Arguments[1], parameters, row, context);
                if (separator.Kind != SqlValueKind.Null)
                    result.Append(ToSqlText(separator));
            }

            result.Append(ToSqlText(value));
            hasValue = true;
        }

        return hasValue ? SqlValue.Text(result.ToString()) : SqlValue.Null;
    }

    private static void RequireAggregateArgumentCount(
        string functionName,
        IReadOnlyList<Expression> arguments,
        int expected)
    {
        if (arguments.Count != expected)
            throw new EmbeddedSqlException($"wrong number of arguments to function {functionName}()");
    }

    private SqlValue EvaluateManagedAggregate(
        ManagedAggregateFunction aggregate,
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        var accumulator = aggregate.Seed;
        foreach (var row in rows)
        {
            var arguments = function.Arguments
                .Select(argument => Evaluate(argument, parameters, row, context))
                .ToArray();
            accumulator = aggregate.Step(accumulator, arguments);
        }

        return aggregate.Finalize(accumulator);
    }

    private SqlValue EvaluatePercentileAggregate(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        var name = function.Name.ToUpperInvariant();
        var isMedian = name == "MEDIAN";
        var isPercentile = name == "PERCENTILE";
        var isContinuous = !isMedian && name != "PERCENTILE_DISC";
        RequireAggregateArgumentCount(name.ToLowerInvariant(), function.Arguments, isMedian ? 1 : 2);

        var values = new List<double>();
        double? percentile = null;
        string? error = null;
        var maximumPercentile = isPercentile ? 100d : 1d;

        foreach (var row in rows)
        {
            if (!TryGetPercentileNumericValue(
                    Evaluate(function.Arguments[0], parameters, row, context),
                    out var value))
            {
                continue;
            }

            if (isMedian)
            {
                values.Add(value);
                continue;
            }

            if (!TryGetPercentileNumericValue(
                    Evaluate(function.Arguments[1], parameters, row, context),
                    out var candidate))
            {
                continue;
            }

            if (!(candidate >= 0d && candidate <= maximumPercentile))
            {
                error ??= isPercentile
                    ? "Invalid percentile value"
                    : "Percentile value must be between 0.0 and 1.0 inclusive";
                continue;
            }

            if (percentile is { } existing)
            {
                if (Math.Abs(existing - candidate) >= 0.001d)
                    error ??= "Inconsistent percentile values across rows";
            }
            else
            {
                percentile = candidate;
            }

            values.Add(value);
        }

        if (error is not null)
            throw new EmbeddedSqlException(error);
        if (values.Count == 0)
            return SqlValue.Null;

        values.Sort(ComparePercentileValues);
        if (isMedian)
        {
            var middle = values.Count / 2;
            return SqlValue.Real(values.Count % 2 == 0
                ? (values[middle - 1] + values[middle]) / 2d
                : values[middle]);
        }

        var fraction = percentile!.Value / maximumPercentile;
        var rank = fraction * (values.Count - 1);
        if (isContinuous)
        {
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);
            if (lower == upper)
                return SqlValue.Real(values[lower]);

            var weight = rank - lower;
            return SqlValue.Real(values[lower] * (1d - weight) + values[upper] * weight);
        }

        return SqlValue.Real(values[(int)Math.Floor(rank)]);
    }

    private static bool TryGetPercentileNumericValue(SqlValue value, out double numeric)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
                numeric = value.AsInteger();
                return true;
            case SqlValueKind.Real:
                numeric = value.AsReal();
                return true;
            case SqlValueKind.Text:
                return double.TryParse(
                    value.AsText(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out numeric);
            default:
                numeric = default;
                return false;
        }
    }

    private static int ComparePercentileValues(double left, double right)
    {
        var leftBits = BitConverter.DoubleToInt64Bits(left);
        var rightBits = BitConverter.DoubleToInt64Bits(right);
        leftBits ^= (leftBits >> 63) & long.MaxValue;
        rightBits ^= (rightBits >> 63) & long.MaxValue;
        return leftBits.CompareTo(rightBits);
    }

    private SqlValue EvaluateAggregateCase(
        CaseExpression expression,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? representative)
    {
        var operand = expression.Operand is null
            ? (SqlValue?)null
            : EvaluateAggregate(expression.Operand, rows, parameters, context, representative);
        foreach (var clause in expression.Clauses)
        {
            var when = EvaluateAggregate(clause.When, rows, parameters, context, representative);
            var matches = operand is null
                ? IsTrue(when)
                : operand.Value.Kind != SqlValueKind.Null
                    && when.Kind != SqlValueKind.Null
                    && Compare(operand.Value, when) == 0;
            if (matches)
                return EvaluateAggregate(clause.Then, rows, parameters, context, representative);
        }

        return expression.Else is null
            ? SqlValue.Null
            : EvaluateAggregate(expression.Else, rows, parameters, context, representative);
    }

    private SqlValue EvaluateScalarFunction(
        FunctionExpression function,
        SqlValue[] parameters,
        SourceRow? row,
        QueryContext context)
    {
        if (string.Equals(function.Name, "COUNT", StringComparison.OrdinalIgnoreCase))
            throw new EmbeddedSqlException("misuse of aggregate function COUNT()");

        if (function.Filter is not null)
            throw new EmbeddedSqlException($"FILTER may not be used with non-aggregate {function.Name.ToLowerInvariant()}()");

        var arguments = function.Arguments.Select(argument => Evaluate(argument, parameters, row, context)).ToArray();
        var normalizedName = function.Name.ToUpperInvariant();
        if (_scalarFunctions.TryGetValue((normalizedName, arguments.Length), out var managedFunction)
            || _scalarFunctions.TryGetValue((normalizedName, -1), out managedFunction))
            return managedFunction(arguments);

        return function.Name.ToUpperInvariant() switch
        {
            "ABS" => EvaluateAbsoluteValue(arguments),
            "COALESCE" => EvaluateCoalesce(arguments),
            "DATE" => SqliteDateTime.Execute(arguments, SqliteDateTime.Func.Date),
            "DATETIME" => SqliteDateTime.Execute(arguments, SqliteDateTime.Func.DateTime),
            "GLOB" => EvaluateGlobFunction(arguments),
            "HEX" => EvaluateHex(arguments),
            "IFNULL" => EvaluateIfNull(arguments),
            "INSTR" => EvaluateInstr(arguments),
            "JSON" => SqliteJson.Json(arguments),
            "JSON_ARRAY" => SqliteJson.JsonArray(arguments),
            "JSON_ARRAY_LENGTH" => SqliteJson.JsonArrayLength(arguments),
            "JSON_ERROR_POSITION" => SqliteJson.JsonErrorPosition(arguments),
            "JSON_EXTRACT" => SqliteJson.JsonExtract(arguments),
            "JSON_INSERT" => SqliteJson.JsonInsert(arguments),
            "JSON_OBJECT" => SqliteJson.JsonObject(arguments),
            "JSON_PATCH" => SqliteJson.JsonPatch(arguments),
            "JSON_QUOTE" => SqliteJson.JsonQuote(arguments),
            "JSON_REMOVE" => SqliteJson.JsonRemove(arguments),
            "JSON_REPLACE" => SqliteJson.JsonReplace(arguments),
            "JSON_SET" => SqliteJson.JsonSet(arguments),
            "JSON_TYPE" => SqliteJson.JsonType(arguments),
            "JSON_VALID" => SqliteJson.JsonValid(arguments),
            "JULIANDAY" => SqliteDateTime.Execute(arguments, SqliteDateTime.Func.JulianDay),
            "LAST_INSERT_ROWID" => EvaluateLastInsertRowId(arguments, context),
            "LENGTH" => EvaluateLength(arguments),
            "LIKE" => EvaluateLikeFunction(arguments),
            "LOWER" => EvaluateCase(arguments, static value => value.ToLowerInvariant()),
            "MIN" => EvaluateScalarMinMax(arguments, maximum: false),
            "MAX" => EvaluateScalarMinMax(arguments, maximum: true),
            "NULLIF" => EvaluateNullIf(function, arguments),
            "FORMAT" or "PRINTF" => EvaluatePrintf(arguments),
            "STRFTIME" => SqliteDateTime.Strftime(arguments),
            "TIME" => SqliteDateTime.Execute(arguments, SqliteDateTime.Func.Time),
            "TYPEOF" => EvaluateTypeOf(arguments),
            "UNIXEPOCH" => SqliteDateTime.Execute(arguments, SqliteDateTime.Func.UnixEpoch),
            "UPPER" => EvaluateCase(arguments, static value => value.ToUpperInvariant()),
            "UUID4_STR" or "GEN_RANDOM_UUID" => SqlValue.Text(FormatUuid(CreateUuid4())),
            "UUID4" => SqlValue.Blob(CreateUuid4()),
            "UUID7_STR" => EvaluateUuid7String(arguments),
            "UUID7" => EvaluateUuid7Blob(arguments),
            "UUID7_TIMESTAMP_MS" => EvaluateUuid7TimestampMilliseconds(arguments),
            "UUID_STR" => EvaluateUuidString(arguments),
            "UUID_BLOB" => EvaluateUuidBlob(arguments),
            _ => throw new EmbeddedSqlException($"no such function: {function.Name}"),
        };
    }

    private static SqlValue EvaluateGlobFunction(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("glob", arguments, 2);
        return EvaluateGlobValues(arguments[1], arguments[0], negated: false);
    }

    // Keep this bounded independently of SQLite's process-wide SQLITE_LIMIT_LENGTH. The managed
    // evaluator must not let a SQL format string allocate an unbounded managed string.
    private const int MaximumPrintfWidth = 1_000_000;
    private const int MaximumPrintfPrecision = 1_000;
    private const int MaximumPrintfOutputLength = 1_000_000;

    // SQLite format() is an alias for printf(). Keep the parser independent of the platform
    // formatter so width, precision, rounding, quoting, and numeric coercion remain deterministic.
    private static SqlValue EvaluatePrintf(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0 || arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var format = ToPrintfText(arguments[0]);
        if (format.Length == 0)
            return SqlValue.Null;

        var result = new StringBuilder(Math.Min(format.Length, MaximumPrintfOutputLength));
        var argumentIndex = 1;
        for (var formatIndex = 0; formatIndex < format.Length; formatIndex++)
        {
            var character = format[formatIndex];
            if (character != '%')
            {
                AppendPrintfOutput(result, character.ToString());
                continue;
            }

            if (++formatIndex == format.Length)
            {
                AppendPrintfOutput(result, "%");
                break;
            }

            if (format[formatIndex] == '%')
            {
                AppendPrintfOutput(result, "%");
                continue;
            }

            var specifier = ParsePrintfSpecifier(
                format,
                ref formatIndex,
                arguments,
                ref argumentIndex);
            if (specifier.Verb == 'n')
                continue;

            var value = argumentIndex < arguments.Count ? arguments[argumentIndex] : SqlValue.Null;
            argumentIndex++;
            AppendPrintfOutput(result, FormatPrintfValue(specifier, value).Value);
        }

        return SqlValue.Text(result.ToString());
    }

    private static PrintfSpecifier ParsePrintfSpecifier(
        string format,
        ref int formatIndex,
        IReadOnlyList<SqlValue> arguments,
        ref int argumentIndex)
    {
        var leftJustify = false;
        var forceSign = false;
        var spaceSign = false;
        var zeroPad = false;
        var alternate = false;
        var alternate2 = false;
        var comma = false;

        while (formatIndex < format.Length)
        {
            switch (format[formatIndex])
            {
                case '-':
                    leftJustify = true;
                    formatIndex++;
                    continue;
                case '+':
                    forceSign = true;
                    formatIndex++;
                    continue;
                case ' ':
                    spaceSign = true;
                    formatIndex++;
                    continue;
                case '0':
                    zeroPad = true;
                    formatIndex++;
                    continue;
                case '#':
                    alternate = true;
                    formatIndex++;
                    continue;
                case '!':
                    alternate2 = true;
                    formatIndex++;
                    continue;
                case ',':
                    comma = true;
                    formatIndex++;
                    continue;
                default:
                    break;
            }

            break;
        }

        int? width;
        if (formatIndex < format.Length && format[formatIndex] == '*')
        {
            formatIndex++;
            var dynamicWidth = ReadPrintfDynamicSize(arguments, ref argumentIndex, MaximumPrintfWidth, "width");
            if (dynamicWidth < 0)
            {
                leftJustify = true;
                dynamicWidth = -dynamicWidth;
            }

            width = dynamicWidth;
        }
        else
        {
            width = ReadPrintfSize(format, ref formatIndex, MaximumPrintfWidth, "width");
        }

        int? precision = null;
        if (formatIndex < format.Length && format[formatIndex] == '.')
        {
            formatIndex++;
            if (formatIndex < format.Length && format[formatIndex] == '*')
            {
                formatIndex++;
                precision = Math.Abs(ReadPrintfDynamicSize(
                    arguments,
                    ref argumentIndex,
                    MaximumPrintfPrecision,
                    "precision"));
            }
            else
            {
                precision = ReadPrintfSize(format, ref formatIndex, MaximumPrintfPrecision, "precision") ?? 0;
            }
        }

        if (formatIndex == format.Length)
            throw new EmbeddedSqlException("unterminated printf format specifier.");

        if (format[formatIndex] == 'l')
        {
            formatIndex++;
            if (formatIndex < format.Length && format[formatIndex] == 'l')
                formatIndex++;
            if (formatIndex == format.Length)
                throw new EmbeddedSqlException("unterminated printf format specifier.");
        }

        var verb = format[formatIndex];
        if (!IsSupportedPrintfVerb(verb))
            throw new EmbeddedSqlException($"unsupported printf format conversion: %{verb}");

        var numeric = verb is 'd' or 'i' or 'u' or 'x' or 'X' or 'o' or 'r' or 'p'
            or 'f' or 'e' or 'E' or 'g' or 'G';
        var signedNumeric = verb is 'd' or 'i' or 'r' or 'f' or 'e' or 'E' or 'g' or 'G';

        return new PrintfSpecifier(
            verb,
            leftJustify && !(zeroPad && numeric),
            forceSign && signedNumeric,
            spaceSign && signedNumeric,
            zeroPad && numeric,
            alternate,
            alternate2,
            comma,
            width,
            precision);
    }

    private static int ReadPrintfDynamicSize(
        IReadOnlyList<SqlValue> arguments,
        ref int argumentIndex,
        int maximum,
        string kind)
    {
        var value = argumentIndex < arguments.Count
            ? ToPrintfInteger(arguments[argumentIndex])
            : 0;
        argumentIndex++;
        if (value > maximum || value < -maximum)
            throw new EmbeddedSqlException($"printf {kind} exceeds {maximum}.");

        return (int)value;
    }

    private static int? ReadPrintfSize(string format, ref int formatIndex, int maximum, string kind)
    {
        if (formatIndex == format.Length || !char.IsAsciiDigit(format[formatIndex]))
            return null;

        var value = 0;
        while (formatIndex < format.Length && char.IsAsciiDigit(format[formatIndex]))
        {
            var digit = format[formatIndex] - '0';
            if (value > (maximum - digit) / 10)
                throw new EmbeddedSqlException($"printf {kind} exceeds {maximum}.");

            value = value * 10 + digit;
            formatIndex++;
        }

        return value;
    }

    private static bool IsSupportedPrintfVerb(char verb)
        => verb is 's' or 'd' or 'i' or 'u' or 'x' or 'X' or 'o'
            or 'f' or 'e' or 'E' or 'g' or 'G' or 'c' or 'q' or 'Q' or 'w'
            or 'p' or 'r' or 'z' or 'n';

    private static void AppendPrintfOutput(StringBuilder output, string value)
    {
        if (value.Length > MaximumPrintfOutputLength - output.Length)
            throw new EmbeddedSqlException($"printf output exceeds {MaximumPrintfOutputLength} characters.");

        output.Append(value);
    }

    private static PrintfText FormatPrintfValue(PrintfSpecifier specifier, SqlValue value)
    {
        return specifier.Verb switch
        {
            's' => ApplyPrintfTextWidth(
                specifier,
                value.Kind == SqlValueKind.Null
                    ? PrintfText.Empty
                    : LimitPrintfText(value, specifier.Precision, specifier.Alternate2)),
            'z' => ApplyPrintfTextWidth(
                specifier,
                value.Kind == SqlValueKind.Null
                    ? PrintfText.Empty
                    : LimitPrintfText(value, specifier.Precision, specifier.Alternate2)),
            'd' or 'i' => FormatPrintfSignedInteger(specifier, ToPrintfInteger(value)),
            'u' => FormatPrintfUnsignedInteger(
                specifier,
                unchecked((ulong)ToPrintfInteger(value)).ToString(CultureInfo.InvariantCulture),
                group: true),
            'x' => FormatPrintfUnsignedInteger(
                specifier,
                unchecked((ulong)ToPrintfInteger(value)).ToString("x", CultureInfo.InvariantCulture),
                specifier.Alternate && ToPrintfInteger(value) != 0 ? "0x" : string.Empty),
            'X' => FormatPrintfUnsignedInteger(
                specifier,
                unchecked((ulong)ToPrintfInteger(value)).ToString("X", CultureInfo.InvariantCulture),
                specifier.Alternate && ToPrintfInteger(value) != 0 ? "0X" : string.Empty),
            'o' => FormatPrintfUnsignedInteger(
                specifier,
                FormatPrintfOctal(unchecked((ulong)ToPrintfInteger(value))),
                specifier.Alternate && ToPrintfInteger(value) != 0 ? "0" : string.Empty),
            'p' => FormatPrintfUnsignedInteger(
                specifier,
                unchecked((ulong)ToPrintfInteger(value)).ToString("X", CultureInfo.InvariantCulture),
                specifier.Alternate && ToPrintfInteger(value) != 0 ? "0x" : string.Empty),
            'r' => FormatPrintfOrdinal(specifier, ToPrintfInteger(value)),
            'f' or 'e' or 'E' or 'g' or 'G' => FormatPrintfFloatingPoint(specifier, ToPrintfReal(value)),
            'c' => FormatPrintfCharacter(specifier, value),
            'q' => FormatPrintfQuotedText(specifier, value, '\0'),
            'Q' => FormatPrintfQuotedText(specifier, value, '\''),
            'w' => FormatPrintfQuotedText(specifier, value, '"'),
            _ => throw new InvalidOperationException($"Unexpected printf verb {specifier.Verb}."),
        };
    }

    private static PrintfText FormatPrintfSignedInteger(PrintfSpecifier specifier, long value)
    {
        var negative = value < 0;
        var magnitude = negative
            ? unchecked((ulong)(-(value + 1))) + 1
            : (ulong)value;
        var digits = ApplyPrintfIntegerPrecision(
            magnitude.ToString(CultureInfo.InvariantCulture),
            specifier.Precision);
        var sign = negative ? "-" : specifier.ForceSign ? "+" : specifier.SpaceSign ? " " : string.Empty;
        if (specifier.Comma)
        {
            if (specifier.ZeroPad && specifier.Width is { } width)
                digits = digits.PadLeft(Math.Max(digits.Length, width - sign.Length), '0');
            digits = AddPrintfThousandsSeparators(digits);
        }
        return ApplyPrintfNumericWidth(
            specifier.Comma && specifier.ZeroPad ? specifier with { Width = null } : specifier,
            sign,
            digits);
    }

    private static PrintfText FormatPrintfUnsignedInteger(
        PrintfSpecifier specifier,
        string digits,
        string prefix = "",
        bool group = false)
    {
        digits = ApplyPrintfIntegerPrecision(digits, specifier.Precision);
        if (specifier.Comma && group)
        {
            if (specifier.ZeroPad && specifier.Width is { } width)
                digits = digits.PadLeft(width, '0');
            digits = AddPrintfThousandsSeparators(digits);
        }

        return ApplyPrintfNumericWidth(
            specifier.Comma && group && specifier.ZeroPad
                ? specifier with { Width = null }
                : specifier,
            prefix,
            digits);
    }

    private static PrintfText FormatPrintfOrdinal(PrintfSpecifier specifier, long value)
    {
        var magnitude = value < 0
            ? unchecked((ulong)(-(value + 1))) + 1
            : (ulong)value;
        var suffix = magnitude % 100 is 11 or 12 or 13
            ? "th"
            : (magnitude % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };
        var digits = string.Concat(
            ApplyPrintfIntegerPrecision(
                magnitude.ToString(CultureInfo.InvariantCulture),
                specifier.Precision),
            suffix);
        var sign = value < 0 ? "-" : specifier.ForceSign ? "+" : specifier.SpaceSign ? " " : string.Empty;
        return ApplyPrintfNumericWidth(specifier, sign, digits);
    }

    private static string ApplyPrintfIntegerPrecision(string digits, int? precision)
    {
        if (precision is not { } minimumDigits || digits.Length >= minimumDigits)
            return digits;

        return string.Concat(new string('0', minimumDigits - digits.Length), digits);
    }

    private static string AddPrintfThousandsSeparators(string digits)
    {
        if (digits.Length <= 3)
            return digits;

        var firstGroupLength = digits.Length % 3;
        if (firstGroupLength == 0)
            firstGroupLength = 3;
        var builder = new StringBuilder(digits.Length + (digits.Length - 1) / 3);
        builder.Append(digits.AsSpan(0, firstGroupLength));
        for (var index = firstGroupLength; index < digits.Length; index += 3)
        {
            builder.Append(',');
            builder.Append(digits.AsSpan(index, 3));
        }

        return builder.ToString();
    }

    private static PrintfText FormatPrintfFloatingPoint(PrintfSpecifier specifier, double value)
    {
        var negative = value < 0;
        var sign = negative ? "-" : specifier.ForceSign ? "+" : specifier.SpaceSign ? " " : string.Empty;
        var digits = FormatPrintfReal(
            specifier.Verb,
            Math.Abs(value),
            specifier.Precision,
            specifier.Alternate,
            specifier.Alternate2);
        if (specifier.Comma && specifier.Verb == 'f')
        {
            var decimalIndex = digits.IndexOf('.');
            var integerLength = decimalIndex < 0 ? digits.Length : decimalIndex;
            digits = string.Concat(
                AddPrintfThousandsSeparators(digits[..integerLength]),
                digits.AsSpan(integerLength));
        }

        return ApplyPrintfNumericWidth(specifier, sign, digits);
    }

    private static PrintfText FormatPrintfQuotedText(PrintfSpecifier specifier, SqlValue value, char quote)
    {
        if (value.Kind == SqlValueKind.Null)
        {
            var nullText = quote == '\'' ? "NULL" : "(NULL)";
            return ApplyPrintfTextWidth(
                specifier,
                LimitPrintfText(nullText, specifier.Precision, specifier.Alternate2));
        }

        var text = LimitPrintfText(value, specifier.Precision, specifier.Alternate2);
        if (specifier.Alternate && quote is '\0' or '\'')
            return FormatPrintfEscapedControlText(specifier, text, quote == '\'');

        var quoteCount = text.Value.Count(character => character == quote || (quote == '\0' && character == '\''));
        var enclosingQuoteCount = quote == '\'' ? 2 : 0;
        if (text.Value.Length > MaximumPrintfOutputLength - quoteCount - enclosingQuoteCount)
            throw new EmbeddedSqlException($"printf output exceeds {MaximumPrintfOutputLength} characters.");

        var escaped = quote switch
        {
            '\0' => text.Value.Replace("'", "''", StringComparison.Ordinal),
            '\'' => $"'{text.Value.Replace("'", "''", StringComparison.Ordinal)}'",
            '"' => text.Value.Replace("\"", "\"\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException($"Unexpected printf quote {quote}."),
        };
        var byteLength = text.ByteLength + quoteCount + enclosingQuoteCount;
        return ApplyPrintfTextWidth(specifier, new PrintfText(escaped, byteLength));
    }

    private static PrintfText FormatPrintfEscapedControlText(
        PrintfSpecifier specifier,
        PrintfText text,
        bool enclose)
    {
        var builder = new StringBuilder(text.Value.Length);
        var changed = false;
        foreach (var rune in text.Value.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                builder.Append(@"\u");
                builder.Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                changed = true;
            }
            else if (rune.Value == '\'')
            {
                builder.Append("''");
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        var escaped = builder.ToString();
        if (enclose)
            escaped = changed ? $"unistr('{escaped}')" : $"'{escaped}'";
        return ApplyPrintfTextWidth(
            specifier,
            new PrintfText(escaped, Encoding.UTF8.GetByteCount(escaped)));
    }

    private static PrintfText FormatPrintfCharacter(PrintfSpecifier specifier, SqlValue value)
    {
        if (value.Kind == SqlValueKind.Null)
            return ApplyPrintfTextWidth(specifier, PrintfText.Empty);

        var text = ToPrintfText(value);
        var runes = text.EnumerateRunes();
        if (!runes.MoveNext())
            return ApplyPrintfTextWidth(specifier, PrintfText.Empty);

        var character = runes.Current.ToString();
        var count = specifier.Precision is > 0 ? specifier.Precision.Value : 1;
        var builder = new StringBuilder(character.Length * count);
        for (var index = 0; index < count; index++)
            builder.Append(character);

        var output = builder.ToString();
        return ApplyPrintfTextWidth(
            specifier,
            new PrintfText(output, Encoding.UTF8.GetByteCount(character) * count));
    }

    private static PrintfText LimitPrintfText(
        SqlValue value,
        int? precision,
        bool characterPrecision = false)
        => value.Kind == SqlValueKind.Text
            ? LimitPrintfText(value.AsText(), precision, characterPrecision)
            : LimitPrintfText(ToPrintfText(value), precision, characterPrecision);

    private static PrintfText LimitPrintfText(
        string value,
        int? precision,
        bool characterPrecision = false)
    {
        if (characterPrecision && precision is { } characterLimit)
        {
            var builder = new StringBuilder(Math.Min(value.Length, characterLimit));
            var count = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (rune.Value == 0 || count++ == characterLimit)
                    break;
                builder.Append(rune.ToString());
            }

            var text = builder.ToString();
            return new PrintfText(text, Encoding.UTF8.GetByteCount(text));
        }

        if (precision is not { } byteLimit)
        {
            var terminatorOffset = value.IndexOf('\0');
            var textLength = terminatorOffset >= 0 ? terminatorOffset : value.Length;
            if (textLength > MaximumPrintfOutputLength)
                throw new EmbeddedSqlException($"printf output exceeds {MaximumPrintfOutputLength} characters.");

            var text = terminatorOffset >= 0 ? value[..terminatorOffset] : value;
            return new PrintfText(text, Encoding.UTF8.GetByteCount(text));
        }

        if (byteLimit == 0)
            return PrintfText.Empty;

        // Each UTF-16 code unit produces at least one UTF-8 byte. Inspecting at most byteLimit
        // code units therefore establishes the requested prefix without traversing the remainder.
        var sourceLength = Math.Min(value.Length, byteLimit);
        var source = value.AsSpan(0, sourceLength);
        var nulOffset = source.IndexOf('\0');
        var isTerminated = nulOffset >= 0;
        if (isTerminated)
            source = source[..nulOffset];

        Span<byte> bytes = stackalloc byte[byteLimit + 3];
        Encoding.UTF8.GetEncoder().Convert(
            source,
            bytes,
            flush: true,
            out _,
            out var bytesUsed,
            out var completed);

        if ((sourceLength == value.Length || isTerminated) && completed && bytesUsed <= byteLimit)
        {
            var text = isTerminated ? source.ToString() : value;
            return new PrintfText(text, bytesUsed);
        }

        return new PrintfText(Encoding.UTF8.GetString(bytes[..byteLimit]), byteLimit);
    }

    private static PrintfText ApplyPrintfTextWidth(PrintfSpecifier specifier, PrintfText text)
    {
        var length = specifier.Alternate2
            ? text.Value.EnumerateRunes().Count()
            : text.ByteLength;
        var padding = Math.Max(0, (specifier.Width ?? 0) - length);
        if (padding == 0)
            return text;

        var spaces = new string(' ', padding);
        return specifier.LeftJustify
            ? new PrintfText(string.Concat(text.Value, spaces), text.ByteLength + padding)
            : new PrintfText(string.Concat(spaces, text.Value), text.ByteLength + padding);
    }

    private static PrintfText ApplyPrintfNumericWidth(PrintfSpecifier specifier, string sign, string digits)
    {
        var padding = Math.Max(0, (specifier.Width ?? 0) - sign.Length - digits.Length);
        if (padding == 0)
            return new PrintfText(string.Concat(sign, digits), sign.Length + digits.Length);

        if (specifier.LeftJustify)
        {
            var spaces = new string(' ', padding);
            return new PrintfText(string.Concat(sign, digits, spaces), sign.Length + digits.Length + padding);
        }

        if (specifier.ZeroPad)
        {
            var prefixIsAlternateInteger = sign is "0" or "0x" or "0X";
            var zeroes = new string(
                '0',
                prefixIsAlternateInteger ? padding + sign.Length : padding);
            var formatted = string.Concat(sign, zeroes, digits);
            return new PrintfText(formatted, formatted.Length);
        }

        var leadingSpaces = new string(' ', padding);
        return new PrintfText(string.Concat(leadingSpaces, sign, digits), sign.Length + digits.Length + padding);
    }

    private static string ToPrintfText(SqlValue value)
    {
        if (value.Kind == SqlValueKind.Integer)
            return value.AsInteger().ToString(CultureInfo.InvariantCulture);
        if (value.Kind == SqlValueKind.Text)
            return TruncatePrintfAtNul(value.AsText());
        if (value.Kind == SqlValueKind.Real)
            return FormatPrintfTextReal(value.AsReal());
        if (value.Kind != SqlValueKind.Blob)
            throw new EmbeddedSqlException($"Cannot convert {value.Kind} to printf text.");

        var blob = value.AsBlob().Span;
        var nulOffset = blob.IndexOf((byte)0);
        if (nulOffset >= 0)
            blob = blob[..nulOffset];

        try
        {
            return TruncatePrintfAtNul(
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(blob));
        }
        catch (DecoderFallbackException exception)
        {
            throw new EmbeddedSqlException("printf() only supports UTF-8 blob arguments.", exception);
        }
    }

    private static string TruncatePrintfAtNul(string value)
    {
        var nulOffset = value.IndexOf('\0');
        return nulOffset >= 0 ? value[..nulOffset] : value;
    }

    private static string FormatPrintfTextReal(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "Inf";
        if (double.IsNegativeInfinity(value))
            return "-Inf";
        if (value == 0)
            return "0.0";

        var formatted = FormatPrintfGeneral(Math.Abs(value), 15, upperCase: false);
        if (value < 0)
            formatted = string.Concat("-", formatted);

        var exponentIndex = formatted.IndexOfAny(['e', 'E']);
        if (exponentIndex < 0)
            return formatted.Contains('.') ? formatted : $"{formatted}.0";

        var mantissa = formatted[..exponentIndex];
        if (!mantissa.Contains('.'))
            mantissa += ".0";

        var exponent = NormalizePrintfExponent(formatted[exponentIndex..], upperCase: false);
        return string.Concat(mantissa, exponent);
    }

    private static string NormalizePrintfExponent(string exponent, bool upperCase)
    {
        var sign = exponent[1];
        var digits = exponent[2..].TrimStart('0');
        if (digits.Length == 0)
            digits = "0";

        return string.Concat(upperCase ? "E" : "e", sign, digits.PadLeft(2, '0'));
    }

    private static long ToPrintfInteger(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => 0,
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => ToSqliteInteger(value.AsReal()),
            _ => ToPrintfInteger(ToPrintfText(value)),
        };
    }

    private static long ToPrintfInteger(string text)
    {
        var numericPrefix = GetSqliteNumericPrefix(text);
        if (numericPrefix is null)
            return 0;

        if (!ContainsPrintfRealMarker(numericPrefix))
            return ParseSqliteInteger(numericPrefix);

        return double.TryParse(
            numericPrefix,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var real)
            ? ToSqliteInteger(real)
            : 0;
    }

    private static double ToPrintfReal(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => 0,
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            _ => ToPrintfReal(ToPrintfText(value)),
        };
    }

    private static double ToPrintfReal(string text)
    {
        var numericPrefix = GetSqliteNumericPrefix(text);
        return numericPrefix is not null
            && double.TryParse(numericPrefix, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            ? real
            : 0;
    }

    private static string? GetSqliteNumericPrefix(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
            index++;

        var start = index;
        if (index < value.Length && value[index] is '+' or '-')
            index++;

        var digitStart = index;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
            index++;
        var hasDigits = index != digitStart;

        if (index < value.Length && value[index] == '.')
        {
            index++;
            var fractionalDigitStart = index;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
                index++;
            hasDigits |= index != fractionalDigitStart;
        }

        if (!hasDigits)
            return null;

        if (index < value.Length && value[index] is 'e' or 'E')
        {
            var exponentStart = index++;
            if (index < value.Length && value[index] is '+' or '-')
                index++;
            var exponentDigitStart = index;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
                index++;
            if (index == exponentDigitStart)
                index = exponentStart;
        }

        return value[start..index];
    }

    private static long ParseSqliteInteger(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
            index++;

        var negative = index < value.Length && value[index] == '-';
        if (index < value.Length && value[index] is '+' or '-')
            index++;

        if (!ulong.TryParse(value[index..], NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
            return negative ? long.MinValue : long.MaxValue;

        if (negative)
        {
            const ulong MinimumMagnitude = 9_223_372_036_854_775_808;
            return magnitude switch
            {
                > MinimumMagnitude => long.MinValue,
                MinimumMagnitude => long.MinValue,
                _ => -(long)magnitude,
            };
        }

        return magnitude > long.MaxValue ? long.MaxValue : (long)magnitude;
    }

    private static long ToSqliteInteger(double value)
    {
        if (double.IsNaN(value))
            return 0;
        if (value >= long.MaxValue)
            return long.MaxValue;
        if (value <= long.MinValue)
            return long.MinValue;

        return (long)Math.Truncate(value);
    }

    private static string FormatPrintfOctal(ulong value)
    {
        Span<char> buffer = stackalloc char[22];
        var index = buffer.Length;
        do
        {
            buffer[--index] = (char)('0' + (value & 7));
            value >>= 3;
        } while (value != 0);

        return new string(buffer[index..]);
    }

    private static string FormatPrintfReal(
        char verb,
        double value,
        int? requestedPrecision,
        bool alternate,
        bool alternate2)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "Inf";

        if (alternate2 && requestedPrecision is > 26)
            requestedPrecision = 26;
        var forceDecimalPoint = alternate || alternate2;
        return verb switch
        {
            'f' => EnsurePrintfDecimalPoint(
                FormatPrintfFixed(value, requestedPrecision ?? 6),
                forceDecimalPoint,
                alternate2),
            'e' => EnsurePrintfDecimalPoint(
                FormatPrintfExponential(value, requestedPrecision ?? 6, upperCase: false),
                forceDecimalPoint,
                alternate2),
            'E' => EnsurePrintfDecimalPoint(
                FormatPrintfExponential(value, requestedPrecision ?? 6, upperCase: true),
                forceDecimalPoint,
                alternate2),
            'g' => EnsurePrintfDecimalPoint(
                FormatPrintfGeneral(
                    value,
                    requestedPrecision ?? 6,
                    upperCase: false,
                    preserveTrailingZeros: alternate),
                alternate2,
                alternate2),
            'G' => EnsurePrintfDecimalPoint(
                FormatPrintfGeneral(
                    value,
                    requestedPrecision ?? 6,
                    upperCase: true,
                    preserveTrailingZeros: alternate),
                alternate2,
                alternate2),
            _ => throw new InvalidOperationException($"Unexpected printf real verb {verb}."),
        };
    }

    private static string EnsurePrintfDecimalPoint(
        string value,
        bool required,
        bool trailingZero = false)
    {
        if (!required)
            return value;
        var exponentIndex = value.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? value : value[..exponentIndex];
        if (mantissa.Contains('.'))
            return value;
        var decimalSuffix = trailingZero ? ".0" : ".";
        return exponentIndex < 0
            ? string.Concat(value, decimalSuffix)
            : string.Concat(mantissa, decimalSuffix, value.AsSpan(exponentIndex));
    }

    private static string FormatPrintfFixed(double value, int precision)
    {
        var digits = RoundPrintfReal(value, precision).ToString(CultureInfo.InvariantCulture);
        if (precision == 0)
            return digits;

        if (digits.Length <= precision)
            digits = digits.PadLeft(precision + 1, '0');

        return string.Concat(digits.AsSpan(0, digits.Length - precision), ".", digits.AsSpan(digits.Length - precision));
    }

    private static string FormatPrintfExponential(double value, int precision, bool upperCase)
    {
        if (value == 0)
            return BuildPrintfExponential(BigInteger.Zero, precision, 0, upperCase);

        var exponent = GetPrintfDecimalExponent(value);
        var digits = RoundPrintfReal(value, precision - exponent);
        var overflow = BigInteger.Pow(10, precision + 1);
        if (digits >= overflow)
        {
            digits /= 10;
            exponent++;
        }

        return BuildPrintfExponential(digits, precision, exponent, upperCase);
    }

    private static string FormatPrintfGeneral(
        double value,
        int requestedPrecision,
        bool upperCase,
        bool preserveTrailingZeros = false)
    {
        var precision = requestedPrecision == 0 ? 1 : requestedPrecision;
        if (value == 0)
            return preserveTrailingZeros
                ? precision == 1 ? "0." : string.Concat("0.", new string('0', precision - 1))
                : "0";

        var exponent = GetPrintfDecimalExponent(value);
        var digits = RoundPrintfReal(value, precision - 1 - exponent);
        var overflow = BigInteger.Pow(10, precision);
        if (digits >= overflow)
        {
            digits /= 10;
            exponent++;
        }

        if (exponent < -4 || exponent >= precision)
        {
            var exponential = BuildPrintfExponential(digits, precision - 1, exponent, upperCase);
            return preserveTrailingZeros ? exponential : TrimPrintfFractionalZeros(exponential);
        }

        var decimalDigits = digits.ToString(CultureInfo.InvariantCulture).PadLeft(precision, '0');
        string fixedPoint;
        if (exponent >= precision - 1)
        {
            fixedPoint = string.Concat(decimalDigits, new string('0', exponent - precision + 1));
        }
        else if (exponent >= 0)
        {
            fixedPoint = string.Concat(
                decimalDigits.AsSpan(0, exponent + 1),
                ".",
                decimalDigits.AsSpan(exponent + 1));
        }
        else
        {
            fixedPoint = string.Concat(
                "0.",
                new string('0', -exponent - 1),
                decimalDigits);
        }

        return preserveTrailingZeros ? EnsurePrintfDecimalPoint(fixedPoint, required: true) : TrimPrintfFractionalZeros(fixedPoint);
    }

    private static string BuildPrintfExponential(BigInteger digits, int precision, int exponent, bool upperCase)
    {
        var mantissaDigits = digits.ToString(CultureInfo.InvariantCulture).PadLeft(precision + 1, '0');
        var mantissa = precision == 0
            ? mantissaDigits
            : string.Concat(mantissaDigits[0], ".", mantissaDigits[1..]);
        var exponentSign = exponent < 0 ? '-' : '+';
        var exponentDigits = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
        return string.Concat(mantissa, upperCase ? "E" : "e", exponentSign, exponentDigits);
    }

    private static string TrimPrintfFractionalZeros(string value)
    {
        var exponentIndex = value.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? value : value[..exponentIndex];
        var exponent = exponentIndex < 0 ? string.Empty : value[exponentIndex..];
        if (!mantissa.Contains('.'))
            return value;

        mantissa = mantissa.TrimEnd('0').TrimEnd('.');
        return string.Concat(mantissa, exponent);
    }

    private static BigInteger RoundPrintfReal(double value, int decimalScale)
    {
        var parts = GetPrintfDoubleParts(value);
        var numerator = parts.Significand;
        var denominator = BigInteger.One;
        var binaryScale = parts.BinaryExponent;

        if (decimalScale >= 0)
        {
            numerator *= BigInteger.Pow(5, decimalScale);
            binaryScale += decimalScale;
        }
        else
        {
            denominator = BigInteger.Pow(5, -decimalScale);
            binaryScale += decimalScale;
        }

        if (binaryScale >= 0)
            numerator <<= binaryScale;
        else
            denominator <<= -binaryScale;

        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return remainder * 2 >= denominator ? quotient + 1 : quotient;
    }

    private static int GetPrintfDecimalExponent(double value)
    {
        var exponent = (int)Math.Floor(Math.Log10(value));
        while (ComparePrintfRealWithPowerOfTen(value, exponent) < 0)
            exponent--;
        while (ComparePrintfRealWithPowerOfTen(value, exponent + 1) >= 0)
            exponent++;

        return exponent;
    }

    private static int ComparePrintfRealWithPowerOfTen(double value, int decimalExponent)
    {
        var parts = GetPrintfDoubleParts(value);
        if (decimalExponent >= 0)
        {
            var commonBinaryExponent = Math.Min(parts.BinaryExponent, decimalExponent);
            var left = parts.Significand << (parts.BinaryExponent - commonBinaryExponent);
            var right = BigInteger.Pow(5, decimalExponent) << (decimalExponent - commonBinaryExponent);
            return left.CompareTo(right);
        }

        var decimalPower = -decimalExponent;
        var numerator = parts.Significand * BigInteger.Pow(5, decimalPower);
        var binaryExponent = parts.BinaryExponent + decimalPower;
        return binaryExponent >= 0
            ? (numerator << binaryExponent).CompareTo(BigInteger.One)
            : numerator.CompareTo(BigInteger.One << -binaryExponent);
    }

    private static PrintfDoubleParts GetPrintfDoubleParts(double value)
    {
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var fraction = bits & ((1UL << 52) - 1);
        return exponent == 0
            ? new PrintfDoubleParts(new BigInteger(fraction), -1074)
            : new PrintfDoubleParts(new BigInteger((1UL << 52) | fraction), exponent - 1023 - 52);
    }

    private readonly record struct PrintfSpecifier(
        char Verb,
        bool LeftJustify,
        bool ForceSign,
        bool SpaceSign,
        bool ZeroPad,
        bool Alternate,
        bool Alternate2,
        bool Comma,
        int? Width,
        int? Precision);

    private readonly record struct PrintfText(string Value, int ByteLength)
    {
        public static PrintfText Empty { get; } = new(string.Empty, 0);
    }

    private readonly record struct PrintfDoubleParts(BigInteger Significand, int BinaryExponent);

    // last_insert_rowid() reports the rowid of the most recent successful INSERT on this
    // connection, or 0 before any INSERT, matching SQLite's per-connection semantics.
    private static SqlValue EvaluateLastInsertRowId(IReadOnlyList<SqlValue> arguments, QueryContext context)
    {
        RequireArgumentCount("last_insert_rowid", arguments, 0);
        return SqlValue.Integer(context.LastInsertRowId);
    }

    private SqlValue EvaluateNullIf(FunctionExpression function, IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("nullif", arguments, 2);
        var value = arguments[0];
        if (value.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        return Compare(value, arguments[1], GetCollation(function.Arguments[0])) == 0 ? SqlValue.Null : value;
    }

    private static SqlValue EvaluateAbsoluteValue(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("abs", arguments, 1);
        var value = arguments[0];
        if (value.Kind == SqlValueKind.Null)
            return SqlValue.Null;
        if (value.Kind == SqlValueKind.Integer)
        {
            if (value.AsInteger() == long.MinValue)
                throw new EmbeddedSqlException("integer overflow");

            return SqlValue.Integer(Math.Abs(value.AsInteger()));
        }

        return SqlValue.Real(Math.Abs(AsReal(value)));
    }

    private static SqlValue EvaluateCoalesce(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function coalesce()");

        return arguments.FirstOrDefault(static value => value.Kind != SqlValueKind.Null);
    }

    private static SqlValue EvaluateHex(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("hex", arguments, 1);
        var value = arguments[0];
        if (value.Kind == SqlValueKind.Null)
            return SqlValue.Text(string.Empty);
        if (value.Kind == SqlValueKind.Blob)
            return SqlValue.Text(Convert.ToHexString(value.AsBlob().Span));

        return SqlValue.Text(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(ToSqlText(value))));
    }

    private static SqlValue EvaluateUuid7String(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            return SqlValue.Text(FormatUuid(CreateUuid7((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));

        var seconds = arguments[0] switch
        {
            { Kind: SqlValueKind.Integer } value => value.AsInteger(),
            { Kind: SqlValueKind.Text } value => ParseUuid7StringTimestamp(value.AsText()),
            _ => throw new EmbeddedSqlException("invalid arguments to function uuid7_str()"),
        };
        return SqlValue.Text(FormatUuid(CreateUuid7FromUnixSeconds(seconds)));
    }

    private static SqlValue EvaluateUuid7Blob(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            return SqlValue.Blob(CreateUuid7((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        return arguments[0].Kind == SqlValueKind.Integer
            ? SqlValue.Blob(CreateUuid7FromUnixSeconds(arguments[0].AsInteger()))
            : SqlValue.Null;
    }

    private static SqlValue EvaluateUuid7TimestampMilliseconds(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function uuid7_timestamp_ms()");

        if (!TryGetUuidBytes(arguments[0], out var uuid))
            return SqlValue.Null;

        return SqlValue.Integer(
            ((long)uuid[0] << 40)
            | ((long)uuid[1] << 32)
            | ((long)uuid[2] << 24)
            | ((long)uuid[3] << 16)
            | ((long)uuid[4] << 8)
            | uuid[5]);
    }

    private static SqlValue EvaluateUuidString(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function uuid_str()");
        if (arguments[0].Kind != SqlValueKind.Blob || arguments[0].AsBlob().Length != 16)
            return SqlValue.Null;

        return SqlValue.Text(FormatUuid(arguments[0].AsBlob().Span));
    }

    private static SqlValue EvaluateUuidBlob(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function uuid_blob()");

        return arguments[0].Kind == SqlValueKind.Text && TryParseUuid(arguments[0].AsText(), out var uuid)
            ? SqlValue.Blob(uuid)
            : SqlValue.Null;
    }

    private static long ParseUuid7StringTimestamp(string value)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
            throw new EmbeddedSqlException("invalid arguments to function uuid7_str()");
        if (seconds <= 0)
            throw new EmbeddedSqlException("Invalid timestamp");

        return seconds;
    }

    private static byte[] CreateUuid4()
    {
        var uuid = new byte[16];
        RandomNumberGenerator.Fill(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0f) | 0x40);
        uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
        return uuid;
    }

    private static byte[] CreateUuid7FromUnixSeconds(long seconds)
        => CreateUuid7(unchecked((ulong)seconds * 1000UL));

    private static byte[] CreateUuid7(ulong milliseconds)
    {
        var uuid = new byte[16];
        RandomNumberGenerator.Fill(uuid);
        uuid[0] = (byte)(milliseconds >> 40);
        uuid[1] = (byte)(milliseconds >> 32);
        uuid[2] = (byte)(milliseconds >> 24);
        uuid[3] = (byte)(milliseconds >> 16);
        uuid[4] = (byte)(milliseconds >> 8);
        uuid[5] = (byte)milliseconds;
        uuid[6] = (byte)((uuid[6] & 0x0f) | 0x70);
        uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
        return uuid;
    }

    private static bool TryGetUuidBytes(SqlValue value, out byte[] uuid)
    {
        if (value.Kind == SqlValueKind.Blob && value.AsBlob().Length == 16)
        {
            uuid = value.AsBlob().ToArray();
            return true;
        }

        if (value.Kind == SqlValueKind.Text && TryParseUuid(value.AsText(), out uuid))
            return true;

        uuid = [];
        return false;
    }

    private static bool TryParseUuid(string value, out byte[] uuid)
    {
        if (value.Length == value.Trim().Length && Guid.TryParse(value, out var parsed))
        {
            uuid = Convert.FromHexString(parsed.ToString("N"));
            return true;
        }

        uuid = [];
        return false;
    }

    private static string FormatUuid(ReadOnlySpan<byte> uuid)
    {
        var hex = Convert.ToHexString(uuid).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static SqlValue EvaluateIfNull(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("ifnull", arguments, 2);
        return arguments[0].Kind == SqlValueKind.Null ? arguments[1] : arguments[0];
    }

    private static SqlValue EvaluateInstr(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("instr", arguments, 2);
        var haystack = arguments[0];
        var needle = arguments[1];
        if (haystack.Kind == SqlValueKind.Null || needle.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        if (haystack.Kind == SqlValueKind.Blob && needle.Kind == SqlValueKind.Blob)
        {
            var offset = haystack.AsBlob().Span.IndexOf(needle.AsBlob().Span);
            return SqlValue.Integer(offset + 1L);
        }

        var text = ToSqlText(haystack);
        var index = text.IndexOf(ToSqlText(needle), StringComparison.Ordinal);
        if (index < 0)
            return SqlValue.Integer(0);

        var codePointCount = 0;
        foreach (var _ in text.AsSpan(0, index).EnumerateRunes())
            codePointCount++;
        return SqlValue.Integer(codePointCount + 1L);
    }

    private static SqlValue EvaluateLength(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("length", arguments, 1);
        var value = arguments[0];
        return value.Kind switch
        {
            SqlValueKind.Null => SqlValue.Null,
            SqlValueKind.Text => SqlValue.Integer(value.AsText().EnumerateRunes().Count()),
            SqlValueKind.Blob => SqlValue.Integer(value.AsBlob().Length),
            _ => SqlValue.Integer(ToSqlText(value).Length),
        };
    }

    private static SqlValue EvaluateLikeFunction(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count is < 2 or > 3)
            throw new EmbeddedSqlException("wrong number of arguments to function like()");

        return EvaluateLikeValues(
            arguments[1],
            arguments[0],
            arguments.Count == 3 ? arguments[2] : null,
            negated: false);
    }

    private SqlValue EvaluateScalarMinMax(IReadOnlyList<SqlValue> arguments, bool maximum)
    {
        if (arguments.Count < 2)
            throw new EmbeddedSqlException($"wrong number of arguments to function {(maximum ? "max" : "min")}()");

        var result = arguments[0];
        foreach (var value in arguments.Skip(1))
        {
            if (result.Kind == SqlValueKind.Null || value.Kind == SqlValueKind.Null)
                return SqlValue.Null;
            if (maximum ? Compare(value, result) > 0 : Compare(value, result) < 0)
                result = value;
        }

        return result;
    }

    private static SqlValue EvaluateCase(IReadOnlyList<SqlValue> arguments, Func<string, string> transform)
    {
        RequireArgumentCount("case", arguments, 1);
        var value = arguments[0];
        return value.Kind == SqlValueKind.Null
            ? SqlValue.Null
            : SqlValue.Text(transform(ToSqlText(value)));
    }

    private static SqlValue EvaluateTypeOf(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("typeof", arguments, 1);
        return SqlValue.Text(arguments[0].Kind switch
        {
            SqlValueKind.Null => "null",
            SqlValueKind.Integer => "integer",
            SqlValueKind.Real => "real",
            SqlValueKind.Text => "text",
            SqlValueKind.Blob => "blob",
            _ => throw new InvalidOperationException($"Unknown SQL value kind {arguments[0].Kind}."),
        });
    }

    private static void RequireArgumentCount(string functionName, IReadOnlyList<SqlValue> arguments, int expected)
    {
        if (arguments.Count != expected)
            throw new EmbeddedSqlException($"wrong number of arguments to function {functionName}()");
    }

    private static SqlValue ApplyNumeric(SqlValue left, SqlValue right, Func<long, long, long> integerOperation, Func<double, double, double> realOperation)
    {
        left = ApplyNumericAffinity(left);
        right = ApplyNumericAffinity(right);
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
        {
            try
            {
                return SqlValue.Integer(integerOperation(left.AsInteger(), right.AsInteger()));
            }
            catch (OverflowException)
            {
                return SqlValue.Real(realOperation(left.AsInteger(), right.AsInteger()));
            }
        }

        return SqlValue.Real(realOperation(AsReal(left), AsReal(right)));
    }

    private static SqlValue ApplyDivision(SqlValue left, SqlValue right)
    {
        left = ApplyNumericAffinity(left);
        right = ApplyNumericAffinity(right);
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
        {
            var divisor = right.AsInteger();
            if (divisor == 0)
                return SqlValue.Null;
            if (left.AsInteger() == long.MinValue && divisor == -1)
                return SqlValue.Real(-(double)long.MinValue);

            return SqlValue.Integer(left.AsInteger() / divisor);
        }

        var realDivisor = AsReal(right);
        if (realDivisor == 0)
            return SqlValue.Null;

        return SqlValue.Real(AsReal(left) / realDivisor);
    }

    private static SqlValue ApplyModulo(SqlValue left, SqlValue right)
    {
        left = ApplyModuloNumericAffinity(left);
        right = ApplyModuloNumericAffinity(right);
        var returnReal = left.Kind == SqlValueKind.Real || right.Kind == SqlValueKind.Real;
        var dividend = left.Kind == SqlValueKind.Integer ? left.AsInteger() : ToSqliteInteger(left.AsReal());
        var divisor = right.Kind == SqlValueKind.Integer ? right.AsInteger() : ToSqliteInteger(right.AsReal());
        if (divisor == 0)
            return SqlValue.Null;

        var remainder = dividend == long.MinValue && divisor == -1 ? 0 : dividend % divisor;
        return returnReal ? SqlValue.Real(remainder) : SqlValue.Integer(remainder);
    }

    private static SqlValue ApplyModuloNumericAffinity(SqlValue value)
    {
        if (value.Kind is SqlValueKind.Integer or SqlValueKind.Real)
            return value;

        var text = value.Kind == SqlValueKind.Text ? value.AsText() : ToPrintfText(value);
        var numericPrefix = GetSqliteNumericPrefix(text);
        if (numericPrefix is null)
            return SqlValue.Integer(0);

        if (!ContainsPrintfRealMarker(numericPrefix))
            return SqlValue.Integer(ParseSqliteInteger(numericPrefix));

        return double.TryParse(
            numericPrefix,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var real)
            ? SqlValue.Real(real)
            : SqlValue.Integer(0);
    }

    private static bool ContainsPrintfRealMarker(string value)
        => value.IndexOf('.') >= 0 || value.IndexOf('e') >= 0 || value.IndexOf('E') >= 0;

    private static SqlValue ApplyConcatenation(SqlValue left, SqlValue right)
    {
        return SqlValue.Text(ToSqlText(left) + ToSqlText(right));
    }

    internal static string ToSqlText(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => System.Text.Encoding.UTF8.GetString(value.AsBlob().Span),
            _ => throw new EmbeddedSqlException($"Cannot convert {value.Kind} to text."),
        };
    }

    private static string? GetCollation(Expression expression)
    {
        return expression switch
        {
            CollationExpression collation => collation.Name,
            _ => null,
        };
    }

    private void ValidateOrderByCollations(IReadOnlyList<OrderByTerm> orderBy)
    {
        foreach (var term in orderBy)
            ValidateCollation(GetCollation(term.Expression));
    }

    private void ValidateCollation(string? collation)
    {
        if (collation is null
            || collation.Equals("BINARY", StringComparison.OrdinalIgnoreCase)
            || collation.Equals("NOCASE", StringComparison.OrdinalIgnoreCase)
            || collation.Equals("RTRIM", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_collations.ContainsKey(collation))
            throw new EmbeddedSqlException($"no such collation sequence: {collation}");
    }

    private bool TryGetAggregateFunction(string name, int arity, out ManagedAggregateFunction function)
    {
        var normalizedName = name.ToUpperInvariant();
        return _aggregateFunctions.TryGetValue((normalizedName, arity), out function!)
            || _aggregateFunctions.TryGetValue((normalizedName, -1), out function!);
    }

    private int Compare(SqlValue left, SqlValue right, string? collation = null)
    {
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
            return left.AsInteger().CompareTo(right.AsInteger());
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Real)
            return CompareIntegerAndReal(left.AsInteger(), right.AsReal());
        if (left.Kind == SqlValueKind.Real && right.Kind == SqlValueKind.Integer)
            return -CompareIntegerAndReal(right.AsInteger(), left.AsReal());
        if (left.Kind == SqlValueKind.Real && right.Kind == SqlValueKind.Real)
            return left.AsReal().CompareTo(right.AsReal());
        if (left.Kind == SqlValueKind.Text && right.Kind == SqlValueKind.Text)
        {
            if (collation is null || string.Equals(collation, "BINARY", StringComparison.OrdinalIgnoreCase))
                return string.CompareOrdinal(left.AsText(), right.AsText());
            if (string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase))
                return CompareSqliteNoCase(left.AsText(), right.AsText());
            if (string.Equals(collation, "RTRIM", StringComparison.OrdinalIgnoreCase))
                return string.CompareOrdinal(left.AsText().TrimEnd(' '), right.AsText().TrimEnd(' '));
            if (_collations.TryGetValue(collation, out var compare))
                return compare(left.AsText(), right.AsText());

            throw new EmbeddedSqlException($"no such collation sequence: {collation}");
        }
        if (left.Kind == SqlValueKind.Blob && right.Kind == SqlValueKind.Blob)
            return left.AsBlob().Span.SequenceCompareTo(right.AsBlob().Span);

        return left.Kind.CompareTo(right.Kind);
    }

    private static int CompareSqliteNoCase(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        var count = Math.Min(leftBytes.Length, rightBytes.Length);
        for (var index = 0; index < count; index++)
        {
            var leftByte = FoldAscii(leftBytes[index]);
            var rightByte = FoldAscii(rightBytes[index]);
            if (leftByte != rightByte)
                return leftByte.CompareTo(rightByte);
        }

        return leftBytes.Length.CompareTo(rightBytes.Length);
    }

    private static byte FoldAscii(byte value)
        => value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + ((byte)'a' - (byte)'A'))
            : value;

    private static int CompareIntegerAndReal(long integer, double real)
    {
        if (real < long.MinValue)
            return 1;
        if (real >= -(double)long.MinValue)
            return -1;

        var truncated = (long)real;
        var comparison = integer.CompareTo(truncated);
        if (comparison != 0)
            return comparison;

        return real == truncated
            ? 0
            : real > truncated
                ? -1
                : 1;
    }

    // ----- Aggregate window functions (func(...) OVER (...)) -----

    private List<FunctionExpression> CollectSelectWindowFunctions(SelectStatement statement)
    {
        var result = new List<FunctionExpression>();
        foreach (var projection in statement.Projections)
            CollectWindowFunctions(projection.Expression, result);

        var orderBy = ResolveOrderBy(statement.OrderBy, statement.Projections);
        foreach (var term in orderBy)
            CollectWindowFunctions(term.Expression, result);

        return result;
    }

    private void CollectWindowFunctions(Expression expression, List<FunctionExpression> result)
    {
        switch (expression)
        {
            case FunctionExpression function when function.Window is not null:
                if (!result.Contains(function))
                    result.Add(function);
                return;
            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                    CollectWindowFunctions(argument, result);
                if (function.Filter is not null)
                    CollectWindowFunctions(function.Filter, result);
                return;
            case BinaryExpression binary:
                CollectWindowFunctions(binary.Left, result);
                CollectWindowFunctions(binary.Right, result);
                return;
            case UnaryExpression unary:
                CollectWindowFunctions(unary.Operand, result);
                return;
            case CollationExpression collation:
                CollectWindowFunctions(collation.Expression, result);
                return;
            case CastExpression cast:
                CollectWindowFunctions(cast.Expression, result);
                return;
            case CaseExpression @case:
                if (@case.Operand is not null)
                    CollectWindowFunctions(@case.Operand, result);
                foreach (var clause in @case.Clauses)
                {
                    CollectWindowFunctions(clause.When, result);
                    CollectWindowFunctions(clause.Then, result);
                }

                if (@case.Else is not null)
                    CollectWindowFunctions(@case.Else, result);
                return;
            case LikeExpression like:
                CollectWindowFunctions(like.Value, result);
                CollectWindowFunctions(like.Pattern, result);
                if (like.Escape is not null)
                    CollectWindowFunctions(like.Escape, result);
                return;
            case GlobExpression glob:
                CollectWindowFunctions(glob.Value, result);
                CollectWindowFunctions(glob.Pattern, result);
                return;
            case InExpression @in:
                CollectWindowFunctions(@in.Value, result);
                foreach (var value in @in.Values)
                    CollectWindowFunctions(value, result);
                return;
            case InSubqueryExpression inSubquery:
                CollectWindowFunctions(inSubquery.Value, result);
                return;
            case BetweenExpression between:
                CollectWindowFunctions(between.Value, result);
                CollectWindowFunctions(between.Lower, result);
                CollectWindowFunctions(between.Upper, result);
                return;
            default:
                return;
        }
    }

    private bool ContainsWindowFunction(Expression expression)
    {
        return expression switch
        {
            FunctionExpression function when function.Window is not null => true,
            FunctionExpression function => function.Arguments.Any(ContainsWindowFunction)
                || (function.Filter is not null && ContainsWindowFunction(function.Filter)),
            BinaryExpression binary => ContainsWindowFunction(binary.Left) || ContainsWindowFunction(binary.Right),
            UnaryExpression unary => ContainsWindowFunction(unary.Operand),
            CollationExpression collation => ContainsWindowFunction(collation.Expression),
            CastExpression cast => ContainsWindowFunction(cast.Expression),
            CaseExpression @case => (@case.Operand is not null && ContainsWindowFunction(@case.Operand))
                || @case.Clauses.Any(clause => ContainsWindowFunction(clause.When) || ContainsWindowFunction(clause.Then))
                || (@case.Else is not null && ContainsWindowFunction(@case.Else)),
            LikeExpression like => ContainsWindowFunction(like.Value)
                || ContainsWindowFunction(like.Pattern)
                || (like.Escape is not null && ContainsWindowFunction(like.Escape)),
            GlobExpression glob => ContainsWindowFunction(glob.Value) || ContainsWindowFunction(glob.Pattern),
            InExpression @in => ContainsWindowFunction(@in.Value) || @in.Values.Any(ContainsWindowFunction),
            InSubqueryExpression inSubquery => ContainsWindowFunction(inSubquery.Value),
            BetweenExpression between => ContainsWindowFunction(between.Value)
                || ContainsWindowFunction(between.Lower)
                || ContainsWindowFunction(between.Upper),
            _ => false,
        };
    }

    // Rewrites each windowed function call to a literal holding its already-computed value
    // for a specific row. Subquery bodies are left untouched: only their outer value
    // expression is rewritten, mirroring CollectWindowFunctions exactly.
    private Expression ReplaceWindowFunctions(
        Expression expression,
        IReadOnlyDictionary<FunctionExpression, SqlValue> substitution)
    {
        switch (expression)
        {
            case FunctionExpression function when function.Window is not null:
                return substitution.TryGetValue(function, out var value)
                    ? new LiteralExpression(value)
                    : expression;
            case FunctionExpression function:
                return function with
                {
                    Arguments = function.Arguments
                        .Select(argument => ReplaceWindowFunctions(argument, substitution))
                        .ToArray(),
                    Filter = function.Filter is null
                        ? null
                        : ReplaceWindowFunctions(function.Filter, substitution),
                };
            case BinaryExpression binary:
                return binary with
                {
                    Left = ReplaceWindowFunctions(binary.Left, substitution),
                    Right = ReplaceWindowFunctions(binary.Right, substitution),
                };
            case UnaryExpression unary:
                return unary with { Operand = ReplaceWindowFunctions(unary.Operand, substitution) };
            case CollationExpression collation:
                return collation with { Expression = ReplaceWindowFunctions(collation.Expression, substitution) };
            case CastExpression cast:
                return cast with { Expression = ReplaceWindowFunctions(cast.Expression, substitution) };
            case CaseExpression @case:
                return @case with
                {
                    Operand = @case.Operand is null ? null : ReplaceWindowFunctions(@case.Operand, substitution),
                    Clauses = @case.Clauses
                        .Select(clause => new CaseClause(
                            ReplaceWindowFunctions(clause.When, substitution),
                            ReplaceWindowFunctions(clause.Then, substitution)))
                        .ToArray(),
                    Else = @case.Else is null ? null : ReplaceWindowFunctions(@case.Else, substitution),
                };
            case LikeExpression like:
                return like with
                {
                    Value = ReplaceWindowFunctions(like.Value, substitution),
                    Pattern = ReplaceWindowFunctions(like.Pattern, substitution),
                    Escape = like.Escape is null ? null : ReplaceWindowFunctions(like.Escape, substitution),
                };
            case GlobExpression glob:
                return glob with
                {
                    Value = ReplaceWindowFunctions(glob.Value, substitution),
                    Pattern = ReplaceWindowFunctions(glob.Pattern, substitution),
                };
            case InExpression @in:
                return @in with
                {
                    Value = ReplaceWindowFunctions(@in.Value, substitution),
                    Values = @in.Values.Select(value => ReplaceWindowFunctions(value, substitution)).ToArray(),
                };
            case InSubqueryExpression inSubquery:
                return inSubquery with { Value = ReplaceWindowFunctions(inSubquery.Value, substitution) };
            case BetweenExpression between:
                return between with
                {
                    Value = ReplaceWindowFunctions(between.Value, substitution),
                    Lower = ReplaceWindowFunctions(between.Lower, substitution),
                    Upper = ReplaceWindowFunctions(between.Upper, substitution),
                };
            default:
                return expression;
        }
    }

    // Rejects everything outside the supported subset: only aggregate functions may be
    // windowed, DISTINCT is disallowed, and window arguments cannot nest aggregates or
    // other window functions.
    private void ValidateWindowFunction(FunctionExpression function)
    {
        if (function.Distinct)
            throw new EmbeddedSqlException("DISTINCT is not supported for window functions");

        var isAggregate = string.Equals(function.Name, "COUNT", StringComparison.Ordinal)
            || IsBuiltInAggregate(function)
            || TryGetAggregateFunction(function.Name, function.Arguments.Count, out _);
        if (!isAggregate)
        {
            throw new EmbeddedSqlException(
                $"{function.Name} is not a supported window function; only aggregate window functions are available");
        }

        foreach (var argument in function.Arguments)
        {
            if (ContainsWindowFunction(argument) || ContainsAggregate(argument))
                throw new EmbeddedSqlException("window function arguments cannot contain aggregate or window functions");
        }
    }

    private ExecutionResult ExecuteWindowSelect(
        SelectStatement statement,
        IReadOnlyList<SourceRow> selectedRows,
        IReadOnlyList<FunctionExpression> windowFunctions,
        string[] columnNames,
        IReadOnlyList<OutputColumn> outputColumns,
        long offset,
        long? limit,
        SqlValue[] parameters,
        QueryContext context)
    {
        foreach (var function in windowFunctions)
            ValidateWindowFunction(function);

        var rowCount = selectedRows.Count;
        var windowValues = new Dictionary<FunctionExpression, SqlValue>[rowCount];
        for (var index = 0; index < rowCount; index++)
            windowValues[index] = new Dictionary<FunctionExpression, SqlValue>();

        foreach (var function in windowFunctions)
        {
            var values = ComputeWindowFunction(function, selectedRows, parameters, context);
            for (var index = 0; index < rowCount; index++)
                windowValues[index][function] = values[index];
        }

        var orderBy = ResolveOrderBy(statement.OrderBy, statement.Projections);
        var indices = Enumerable.Range(0, rowCount).ToList();
        if (orderBy.Count > 0)
        {
            indices = StableSortIndices(indices, (left, right) =>
            {
                foreach (var term in orderBy)
                {
                    var leftValue = Evaluate(
                        ReplaceWindowFunctions(term.Expression, windowValues[left]),
                        parameters,
                        selectedRows[left],
                        context);
                    var rightValue = Evaluate(
                        ReplaceWindowFunctions(term.Expression, windowValues[right]),
                        parameters,
                        selectedRows[right],
                        context);
                    var comparison = CompareForOrdering(
                        leftValue,
                        rightValue,
                        term,
                        GetCollation(term.Expression));
                    if (comparison == 0)
                        continue;

                    return comparison;
                }

                return 0;
            });
        }

        var resultRows = new List<SqlValue[]>(rowCount);
        foreach (var index in indices)
        {
            var row = selectedRows[index];
            var substitution = windowValues[index];
            var values = new List<SqlValue>();
            foreach (var projection in statement.Projections)
            {
                switch (projection.Expression)
                {
                    case StarExpression:
                        foreach (var column in outputColumns)
                            values.Add(GetOutputValue(row, column));
                        break;
                    case QualifiedStarExpression qualifiedStar:
                        var rawMatches = GetRawOutputColumns(statement.Source, context)
                            .Where(column => string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        if (rawMatches.Length == 0)
                            throw new EmbeddedSqlException($"no such table: {qualifiedStar.Qualifier}");

                        var matches = rawMatches
                            .Select(raw => outputColumns.FirstOrDefault(column =>
                                string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase)
                                && column.Index == raw.Index) ?? raw)
                            .ToArray();
                        foreach (var column in matches)
                            values.Add(GetOutputValue(row, column));
                        break;
                    default:
                        values.Add(Evaluate(
                            ReplaceWindowFunctions(projection.Expression, substitution),
                            parameters,
                            row,
                            context));
                        break;
                }
            }

            resultRows.Add(values.ToArray());
        }

        var collations = BuildProjectionCollations(statement, outputColumns, context);
        return new ExecutionResult(
            columnNames,
            ApplyDistinctLimit(resultRows, statement.Distinct, offset, limit, collations),
            0);
    }

    // Produces a collation entry per output column (not per projection) so that DISTINCT
    // comparison never indexes past the end when a projection expands to several columns
    // via * or table.*.
    private IReadOnlyList<string?> BuildProjectionCollations(
        SelectStatement statement,
        IReadOnlyList<OutputColumn> outputColumns,
        QueryContext context)
    {
        var collations = new List<string?>();
        foreach (var projection in statement.Projections)
        {
            switch (projection.Expression)
            {
                case StarExpression:
                    for (var index = 0; index < outputColumns.Count; index++)
                        collations.Add(null);
                    break;
                case QualifiedStarExpression qualifiedStar:
                    var count = GetRawOutputColumns(statement.Source, context)
                        .Count(column => string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase));
                    for (var index = 0; index < count; index++)
                        collations.Add(null);
                    break;
                default:
                    collations.Add(GetCollation(projection.Expression));
                    break;
            }
        }

        return collations;
    }

    // Sorts row indices with a caller-supplied comparison while guaranteeing stability by
    // breaking ties on the element's original ordinal. Window ordering must be stable so
    // ROWS frames and ORDER BY peer groups observe rows in their source order.
    private static List<int> StableSortIndices(List<int> indices, Comparison<int> comparison)
    {
        var decorated = indices
            .Select((value, ordinal) => (value, ordinal))
            .ToList();
        decorated.Sort((left, right) =>
        {
            var result = comparison(left.value, right.value);
            return result != 0 ? result : left.ordinal.CompareTo(right.ordinal);
        });

        return decorated.Select(entry => entry.value).ToList();
    }

    // Computes the window aggregate for every row, returned in the same order as the input
    // rows. Rows are grouped by PARTITION BY, ordered within the partition, and each row's
    // frame is aggregated independently.
    private SqlValue[] ComputeWindowFunction(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        var spec = function.Window!;
        var results = new SqlValue[rows.Count];
        var baseFunction = function with { Window = null };

        var partitions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var partitionOrder = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var key = spec.PartitionBy.Count == 0
                ? string.Empty
                : GetGroupKey(spec.PartitionBy, parameters, rows[index], context);
            if (!partitions.TryGetValue(key, out var members))
            {
                members = [];
                partitions.Add(key, members);
                partitionOrder.Add(key);
            }

            members.Add(index);
        }

        foreach (var key in partitionOrder)
        {
            var members = partitions[key];
            if (spec.OrderBy.Count > 0)
            {
                members = StableSortIndices(
                    members,
                    (left, right) => CompareRows(rows[left], rows[right], spec.OrderBy, parameters, context));
            }

            var orderedRows = members.Select(index => rows[index]).ToList();
            for (var position = 0; position < orderedRows.Count; position++)
            {
                var (start, end) = ResolveFrame(spec, orderedRows, position, parameters, context);
                IReadOnlyList<SourceRow> frameRows = start > end
                    ? []
                    : orderedRows.GetRange(start, end - start + 1);
                results[members[position]] = EvaluateAggregate(baseFunction, frameRows, parameters, context);
            }
        }

        return results;
    }

    private (int Start, int End) ResolveFrame(
        WindowSpecification spec,
        IReadOnlyList<SourceRow> orderedRows,
        int position,
        SqlValue[] parameters,
        QueryContext context)
    {
        var count = orderedRows.Count;
        if (spec.Frame is null)
        {
            if (spec.OrderBy.Count == 0)
                return (0, count - 1);

            // Default frame is RANGE UNBOUNDED PRECEDING AND CURRENT ROW: the current row
            // plus every earlier row, and all following rows that are ORDER BY peers.
            var end = position;
            while (end + 1 < count
                && CompareRows(orderedRows[end + 1], orderedRows[position], spec.OrderBy, parameters, context) == 0)
            {
                end++;
            }

            return (0, end);
        }

        var startRaw = ResolveBoundIndex(spec.Frame.Start, position, count, parameters, context);
        var endRaw = ResolveBoundIndex(spec.Frame.End, position, count, parameters, context);
        var effectiveStart = Math.Max(0L, startRaw);
        var effectiveEnd = Math.Min(count - 1L, endRaw);
        if (effectiveStart > effectiveEnd)
            return (0, -1);

        return ((int)effectiveStart, (int)effectiveEnd);
    }

    private long ResolveBoundIndex(
        FrameBound bound,
        int position,
        int count,
        SqlValue[] parameters,
        QueryContext context)
    {
        switch (bound.Kind)
        {
            case FrameBoundKind.UnboundedPreceding:
                return 0;
            case FrameBoundKind.UnboundedFollowing:
                return count - 1L;
            case FrameBoundKind.CurrentRow:
                return position;
            case FrameBoundKind.Preceding:
                return position - GetClampedFrameOffset(bound.Offset!, count, parameters, context);
            case FrameBoundKind.Following:
                return position + GetClampedFrameOffset(bound.Offset!, count, parameters, context);
            default:
                throw new EmbeddedSqlException("Unsupported window frame bound.");
        }
    }

    private long GetClampedFrameOffset(
        Expression expression,
        int count,
        SqlValue[] parameters,
        QueryContext context)
    {
        var value = Evaluate(expression, parameters, null, context);
        if (value.Kind != SqlValueKind.Integer)
            throw new EmbeddedSqlException("frame boundary offset must be a non-negative integer");

        var offset = value.AsInteger();
        if (offset < 0)
            throw new EmbeddedSqlException("frame boundary offset must be a non-negative integer");

        // Clamp before it is combined with the row position so the bound arithmetic can
        // never overflow the 64-bit index space.
        return Math.Min(offset, count);
    }

    private int CompareRows(
        SourceRow left,
        SourceRow right,
        IReadOnlyList<OrderByTerm> orderBy,
        SqlValue[] parameters,
        QueryContext context)
    {
        foreach (var term in orderBy)
        {
            var comparison = CompareForOrdering(
                Evaluate(term.Expression, parameters, left, context),
                Evaluate(term.Expression, parameters, right, context),
                term,
                GetCollation(term.Expression));
            if (comparison == 0)
                continue;

            return comparison;
        }

        return 0;
    }

    private int CompareGroupedRows(
        GroupedResult left,
        GroupedResult right,
        IReadOnlyList<OrderByTerm> orderBy,
        SqlValue[] parameters,
        QueryContext context)
    {
        foreach (var term in orderBy)
        {
            var comparison = CompareForOrdering(
                ContainsAggregate(term.Expression)
                    ? EvaluateAggregate(
                        term.Expression,
                        left.Rows,
                        parameters,
                        context,
                        left.Representative)
                    : Evaluate(term.Expression, parameters, left.Representative, context),
                ContainsAggregate(term.Expression)
                    ? EvaluateAggregate(
                        term.Expression,
                        right.Rows,
                        parameters,
                        context,
                        right.Representative)
                    : Evaluate(term.Expression, parameters, right.Representative, context),
                term,
                GetCollation(term.Expression));
            if (comparison == 0)
                continue;

            return comparison;
        }

        return 0;
    }

    private int CompareForOrdering(
        SqlValue left,
        SqlValue right,
        OrderByTerm term,
        string? collation)
    {
        if (left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null)
        {
            if (left.Kind == right.Kind)
                return 0;

            var nullPlacement = term.NullPlacement switch
            {
                NullPlacement.Default => term.Descending ? NullPlacement.Last : NullPlacement.First,
                NullPlacement.First => NullPlacement.First,
                NullPlacement.Last => NullPlacement.Last,
                _ => throw new InvalidOperationException($"Unsupported NULL placement {term.NullPlacement}."),
            };
            return left.Kind == SqlValueKind.Null
                ? nullPlacement == NullPlacement.First ? -1 : 1
                : nullPlacement == NullPlacement.First ? 1 : -1;
        }

        var comparison = Compare(left, right, collation);
        return term.Descending && comparison != 0
            ? comparison > 0 ? -1 : 1
            : comparison;
    }

    private static IReadOnlyList<OrderByTerm> ResolveOrderBy(
        IReadOnlyList<OrderByTerm> orderBy,
        IReadOnlyList<Projection> projections)
    {
        return orderBy
            .Select(term => term with
            {
                Expression = ResolveOrderByExpression(term.Expression, projections, term.Ordinal),
                Ordinal = null,
            })
            .ToArray();
    }

    private static Expression ResolveOrderByExpression(
        Expression expression,
        IReadOnlyList<Projection> projections,
        long? ordinal = null)
    {
        if (expression is CollationExpression collation)
        {
            return collation with
            {
                Expression = ResolveOrderByExpression(collation.Expression, projections, ordinal),
            };
        }

        if (ordinal is { } value)
        {
            if (value is >= 1 and <= int.MaxValue && value <= projections.Count)
                return projections[(int)value - 1].Expression;

            if (!projections.Any(projection =>
                    projection.Expression is StarExpression or QualifiedStarExpression))
            {
                throw new EmbeddedSqlException(
                    $"ORDER BY position {value} is out of range for {projections.Count} result columns");
            }
        }

        if (expression is not ColumnExpression column)
            return expression;

        var projection = projections.FirstOrDefault(projection =>
            projection.Alias is not null
            && string.Equals(projection.Alias, column.Name, StringComparison.OrdinalIgnoreCase));
        return projection?.Expression ?? expression;
    }

    private static double AsReal(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            _ => throw new EmbeddedSqlException($"Expected a numeric value, got {value.Kind}."),
        };
    }

    private static SqlValue ApplyNumericAffinity(SqlValue value)
    {
        if (value.Kind is SqlValueKind.Integer or SqlValueKind.Real)
            return value;
        if (value.Kind == SqlValueKind.Text)
        {
            var text = value.AsText().Trim();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                return SqlValue.Integer(integer);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                return SqlValue.Real(real);
        }

        return SqlValue.Integer(0);
    }

    private static long RequireInteger(SqlValue value)
    {
        if (value.Kind != SqlValueKind.Integer)
            throw new EmbeddedSqlException($"Expected an integer value, got {value.Kind}.");

        return value.AsInteger();
    }

    private static long RequireLimitInteger(SqlValue value)
    {
        if (value.Kind == SqlValueKind.Integer)
            return value.AsInteger();
        if (value.Kind == SqlValueKind.Real)
        {
            var real = value.AsReal();
            if (double.IsFinite(real)
                && real == Math.Truncate(real)
                && real >= long.MinValue
                && real < -(double)long.MinValue)
            {
                return (long)real;
            }
        }

        throw new EmbeddedSqlException("datatype mismatch");
    }

    private static bool IsTrue(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => false,
            SqlValueKind.Integer => value.AsInteger() != 0,
            SqlValueKind.Real => value.AsReal() != 0,
            _ => IsTrue(ApplyNumericAffinity(value)),
        };
    }

    private static SqlValue ReadParameter(SqlValue[] parameters, int index)
    {
        if (index < 1 || index >= parameters.Length)
            throw new EmbeddedSqlException($"Missing value for parameter at position {index}.");

        return parameters[index];
    }

    internal static Dictionary<string, EmbeddedTable> CloneTables(Dictionary<string, EmbeddedTable> source)
    {
        var clone = new Dictionary<string, EmbeddedTable>(source.Comparer);
        foreach (var pair in source)
            clone.Add(pair.Key, pair.Value.Clone());

        return clone;
    }

    // SQLite-compatible date/time scalar functions.
    //
    // This is a faithful port of SQLite's date.c algorithm (Julian Day arithmetic on integer
    // milliseconds), matched against the reference implementation in core/functions/datetime.rs.
    // It reproduces SQLite's exact parsing, modifier, and formatting semantics, including NULL
    // results for malformed inputs, out-of-range dates, and unsupported modifiers.
    private static class SqliteDateTime
    {
        internal enum Func
        {
            Date,
            Time,
            DateTime,
            JulianDay,
            UnixEpoch,
        }

        private const long JdToMs = 86_400_000L;
        private const long MaxJd = 464269060799999L; // 9999-12-31 23:59:59.999
        private const long UnixEpochIJd = 210866760000000L;
        private const double DoubleEpsilon = 2.220446049250313e-16; // f64::EPSILON
        private static readonly char[] AsciiWsChars = { ' ', '\t', '\n', '\r', '\u000C' };

        private sealed class Dt
        {
            public long IJd;
            public int Y = 2000;
            public int M = 1;
            public int D = 1;
            public int H;
            public int Min;
            public double S;
            public int Tz;
            public int NFloor;
            public bool ValidJd;
            public bool ValidYmd;
            public bool ValidHms;
            public bool RawS;
            public bool IsError;
            public bool UseSubsec;
            public bool IsUtc;
            public bool IsLocal;

            public Dt Clone() => (Dt)MemberwiseClone();

            public void SetError()
            {
                IJd = 0;
                Y = 2000;
                M = 1;
                D = 1;
                H = 0;
                Min = 0;
                S = 0.0;
                Tz = 0;
                NFloor = 0;
                ValidJd = ValidYmd = ValidHms = RawS = UseSubsec = IsUtc = IsLocal = false;
                IsError = true;
            }

            public void ComputeJd()
            {
                if (ValidJd)
                    return;

                int y;
                int m;
                int d;
                if (ValidYmd)
                {
                    y = Y;
                    m = M;
                    d = D;
                }
                else
                {
                    y = 2000;
                    m = 1;
                    d = 1;
                }

                if (y < -4713 || y > 9999 || RawS)
                {
                    SetError();
                    return;
                }

                if (m <= 2)
                {
                    y -= 1;
                    m += 12;
                }

                int a = (y + 4800) / 100;
                int b = 38 - a + (a / 4);
                int x1 = 36525 * (y + 4716) / 100;
                int x2 = 306001 * (m + 1) / 10000;
                IJd = ((long)x1 + x2 + d + b) * 86400000L - 131716800000L;
                ValidJd = true;
                if (ValidHms)
                {
                    IJd += (long)H * 3_600_000 + (long)Min * 60_000 + (long)(S * 1000.0 + 0.5);
                    if (Tz != 0)
                    {
                        IJd -= (long)Tz * 60_000;
                        ValidYmd = false;
                        ValidHms = false;
                        Tz = 0;
                        IsUtc = true;
                        IsLocal = false;
                    }
                }
            }

            public void ComputeYmd()
            {
                if (ValidYmd)
                    return;

                if (!ValidJd)
                {
                    Y = 2000;
                    M = 1;
                    D = 1;
                }
                else if (IJd < 0 || IJd > MaxJd)
                {
                    SetError();
                    return;
                }
                else
                {
                    int z = (int)((IJd + 43200000) / JdToMs);
                    int alpha = (int)((z + 32044.75) / 36524.25) - 52;
                    int a = z + 1 + alpha - ((alpha + 100) / 4) + 25;
                    int b = a + 1524;
                    int c = (int)((b - 122.1) / 365.25);
                    int dCalc = 36525 * (c & 32767) / 100;
                    int e = (int)((b - dCalc) / 30.6001);
                    int x1 = (int)(30.6001 * e);
                    D = b - dCalc - x1;
                    M = e < 14 ? e - 1 : e - 13;
                    Y = M > 2 ? c - 4716 : c - 4715;
                }

                ValidYmd = true;
            }

            public void ComputeHms()
            {
                if (ValidHms)
                    return;

                ComputeJd();
                int dayMs = (int)((IJd + 43200000) % 86400000);
                S = (dayMs % 60000) / 1000.0;
                int dayMin = dayMs / 60000;
                Min = dayMin % 60;
                H = dayMin / 60;
                RawS = false;
                ValidHms = true;
            }

            public void ComputeYmdHms()
            {
                ComputeYmd();
                ComputeHms();
            }

            public void ClearYmdHmsTz()
            {
                ValidYmd = false;
                ValidHms = false;
                Tz = 0;
            }

            public void ComputeFloor()
            {
                if (D <= 28 || ((1 << M) & 0x15aa) != 0)
                    NFloor = 0;
                else if (M != 2)
                    NFloor = D == 31 ? 1 : 0;
                else if (Y % 4 != 0 || (Y % 100 == 0 && Y % 400 != 0))
                    NFloor = D - 28;
                else
                    NFloor = D - 29;
            }
        }

        internal static SqlValue Execute(IReadOnlyList<SqlValue> args, Func func)
        {
            var p = new Dt();
            bool hasModifier = false;
            if (args.Count == 0)
            {
                SetToCurrent(p);
            }
            else
            {
                if (!InitTimeValue(p, args[0]))
                    return SqlValue.Null;

                for (int i = 1; i < args.Count; i++)
                {
                    hasModifier = true;
                    var v = args[i];
                    if (v.Kind != SqlValueKind.Text)
                        return SqlValue.Null;
                    if (!ParseModifier(p, v.AsText(), i - 1))
                        return SqlValue.Null;
                }
            }

            p.ComputeJd();
            if (p.IsError || p.IJd < 0 || p.IJd > MaxJd)
                return SqlValue.Null;

            if (!hasModifier && p.ValidYmd && p.D > 28)
                p.ValidYmd = false;

            switch (func)
            {
                case Func.JulianDay:
                    return SqlValue.Real((double)p.IJd / 86400000.0);
                case Func.UnixEpoch:
                    if (p.UseSubsec)
                        return SqlValue.Real((double)(p.IJd - UnixEpochIJd) / 1000.0);
                    return SqlValue.Integer((p.IJd - UnixEpochIJd) / 1000);
                default:
                    p.ComputeYmdHms();
                    if (p.IsError)
                        return SqlValue.Null;
                    return SqlValue.Text(Format(p, func));
            }
        }

        internal static SqlValue Strftime(IReadOnlyList<SqlValue> args)
        {
            if (args.Count < 1)
                return SqlValue.Null;

            var fmtVal = args[0];
            if (fmtVal.Kind == SqlValueKind.Null)
                return SqlValue.Null;
            string fmt = fmtVal.Kind == SqlValueKind.Text ? fmtVal.AsText() : ToSqlText(fmtVal);

            var p = new Dt();
            if (args.Count == 1)
            {
                SetToCurrent(p);
            }
            else
            {
                if (!InitTimeValue(p, args[1]))
                    return SqlValue.Null;

                for (int i = 2; i < args.Count; i++)
                {
                    var v = args[i];
                    if (v.Kind != SqlValueKind.Text)
                        return SqlValue.Null;
                    if (!ParseModifier(p, v.AsText(), i - 2))
                        return SqlValue.Null;
                }
            }

            p.ComputeJd();
            if (p.IsError)
                return SqlValue.Null;
            p.ComputeYmdHms();

            return FormatStrftime(fmt, p);
        }

        private static bool InitTimeValue(Dt p, SqlValue value)
        {
            switch (value.Kind)
            {
                case SqlValueKind.Text:
                    return ParseDateOrTime(value.AsText(), p);
                case SqlValueKind.Integer:
                    SetRawNumber(p, value.AsInteger());
                    return true;
                case SqlValueKind.Real:
                    SetRawNumber(p, value.AsReal());
                    return true;
                default:
                    return false;
            }
        }

        private static void SetRawNumber(Dt p, double value)
        {
            p.S = value;
            p.RawS = true;
            if (value >= 0.0 && value < 5373484.5)
            {
                p.IJd = (long)(value * JdToMs + 0.5);
                p.ValidJd = true;
            }
        }

        private static string Format(Dt p, Func func)
        {
            var sb = new StringBuilder();
            if (func == Func.Time)
            {
                sb.Append(p.H.ToString("D2", CultureInfo.InvariantCulture));
                sb.Append(':').Append(p.Min.ToString("D2", CultureInfo.InvariantCulture));
                AppendSeconds(sb, p);
            }
            else
            {
                AppendDate(sb, p);
                if (func == Func.DateTime)
                {
                    sb.Append(' ');
                    sb.Append(p.H.ToString("D2", CultureInfo.InvariantCulture));
                    sb.Append(':').Append(p.Min.ToString("D2", CultureInfo.InvariantCulture));
                    AppendSeconds(sb, p);
                }
            }

            return sb.ToString();
        }

        private static void AppendDate(StringBuilder sb, Dt p)
        {
            if (p.Y < 0)
                sb.Append('-').Append(Math.Abs(p.Y).ToString("D4", CultureInfo.InvariantCulture));
            else
                sb.Append(p.Y.ToString("D4", CultureInfo.InvariantCulture));
            sb.Append('-').Append(p.M.ToString("D2", CultureInfo.InvariantCulture));
            sb.Append('-').Append(p.D.ToString("D2", CultureInfo.InvariantCulture));
        }

        private static void AppendSeconds(StringBuilder sb, Dt p)
        {
            sb.Append(':');
            if (p.UseSubsec)
                sb.Append(p.S.ToString("00.000", CultureInfo.InvariantCulture));
            else
                sb.Append(((int)p.S).ToString("D2", CultureInfo.InvariantCulture));
        }

        private static SqlValue FormatStrftime(string fmt, Dt p)
        {
            var res = new StringBuilder();
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (c != '%')
                {
                    res.Append(c);
                    continue;
                }

                i++;
                if (i >= fmt.Length)
                    return SqlValue.Null;

                switch (fmt[i])
                {
                    case 'd':
                        res.Append(p.D.ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'e':
                        res.Append(p.D.ToString(CultureInfo.InvariantCulture).PadLeft(2));
                        break;
                    case 'F':
                        res.Append(p.Y.ToString("D4", CultureInfo.InvariantCulture));
                        res.Append('-').Append(p.M.ToString("D2", CultureInfo.InvariantCulture));
                        res.Append('-').Append(p.D.ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'f':
                        {
                            double sf = p.S;
                            if (sf > 59.999)
                                sf = 59.999;
                            res.Append(sf.ToString("00.000", CultureInfo.InvariantCulture));
                            break;
                        }
                    case 'g':
                        {
                            var iso = p.Clone();
                            iso.IJd += (3 - DaysAfterMon(p)) * 86400000;
                            iso.ValidYmd = false;
                            iso.ComputeYmd();
                            res.Append((iso.Y % 100).ToString("D2", CultureInfo.InvariantCulture));
                            break;
                        }
                    case 'G':
                        {
                            var iso = p.Clone();
                            iso.IJd += (3 - DaysAfterMon(p)) * 86400000;
                            iso.ValidYmd = false;
                            iso.ComputeYmd();
                            res.Append(iso.Y.ToString("D4", CultureInfo.InvariantCulture));
                            break;
                        }
                    case 'H':
                        res.Append(p.H.ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'I':
                        {
                            int h = p.H % 12 == 0 ? 12 : p.H % 12;
                            res.Append(h.ToString("D2", CultureInfo.InvariantCulture));
                            break;
                        }
                    case 'j':
                        res.Append((DaysAfterJan1(p) + 1).ToString("D3", CultureInfo.InvariantCulture));
                        break;
                    case 'J':
                        res.Append(FormatJulianDaySpecifier(p));
                        break;
                    case 'k':
                        res.Append(p.H.ToString(CultureInfo.InvariantCulture).PadLeft(2));
                        break;
                    case 'l':
                        {
                            int h = p.H % 12 == 0 ? 12 : p.H % 12;
                            res.Append(h.ToString(CultureInfo.InvariantCulture).PadLeft(2));
                            break;
                        }
                    case 'm':
                        res.Append(p.M.ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'M':
                        res.Append(p.Min.ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'p':
                        res.Append(p.H >= 12 ? "PM" : "AM");
                        break;
                    case 'P':
                        res.Append(p.H >= 12 ? "pm" : "am");
                        break;
                    case 'R':
                        res.Append(p.H.ToString("D2", CultureInfo.InvariantCulture));
                        res.Append(':').Append(p.Min.ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 's':
                        if (p.UseSubsec)
                            res.Append(((double)(p.IJd - UnixEpochIJd) / 1000.0).ToString("F3", CultureInfo.InvariantCulture));
                        else
                            res.Append(((p.IJd - UnixEpochIJd) / 1000).ToString(CultureInfo.InvariantCulture));
                        break;
                    case 'S':
                        res.Append(((int)p.S).ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'T':
                        res.Append(p.H.ToString("D2", CultureInfo.InvariantCulture));
                        res.Append(':').Append(p.Min.ToString("D2", CultureInfo.InvariantCulture));
                        res.Append(':').Append(((int)p.S).ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'u':
                        {
                            long w = DaysAfterSun(p);
                            if (w == 0)
                                w = 7;
                            res.Append(w.ToString(CultureInfo.InvariantCulture));
                            break;
                        }
                    case 'U':
                        res.Append(((DaysAfterJan1(p) - DaysAfterSun(p) + 7) / 7).ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'V':
                        {
                            var iso = p.Clone();
                            iso.IJd += (3 - DaysAfterMon(p)) * 86400000;
                            iso.ValidYmd = false;
                            iso.ComputeYmd();
                            res.Append((DaysAfterJan1(iso) / 7 + 1).ToString("D2", CultureInfo.InvariantCulture));
                            break;
                        }
                    case 'w':
                        res.Append(DaysAfterSun(p).ToString(CultureInfo.InvariantCulture));
                        break;
                    case 'W':
                        res.Append(((DaysAfterJan1(p) - DaysAfterMon(p) + 7) / 7).ToString("D2", CultureInfo.InvariantCulture));
                        break;
                    case 'Y':
                        res.Append(p.Y.ToString("D4", CultureInfo.InvariantCulture));
                        break;
                    case '%':
                        res.Append('%');
                        break;
                    default:
                        return SqlValue.Null;
                }
            }

            return SqlValue.Text(res.ToString());
        }

        private static string FormatJulianDaySpecifier(Dt p)
        {
            double val = (double)p.IJd / 86400000.0;
            double abs = Math.Abs(val);
            if (abs >= 1_000_000.0 && abs < 10_000_000.0)
            {
                string s = val.ToString("F9", CultureInfo.InvariantCulture);
                s = s.TrimEnd('0').TrimEnd('.');
                return s;
            }

            return val.ToString(CultureInfo.InvariantCulture);
        }

        private static long DaysAfterJan1(Dt cur)
        {
            var jan1 = new Dt { Y = cur.Y, M = 1, D = 1, ValidYmd = true };
            jan1.ComputeJd();
            var c1 = new Dt { Y = cur.Y, M = cur.M, D = cur.D, ValidYmd = true };
            c1.ComputeJd();
            return (c1.IJd - jan1.IJd) / JdToMs;
        }

        private static long DaysAfterMon(Dt cur) => ((cur.IJd + 43200000) / JdToMs) % 7;

        private static long DaysAfterSun(Dt cur) => ((cur.IJd + 129600000) / JdToMs) % 7;

        private static void SetToCurrent(Dt p)
        {
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.IJd = UnixEpochIJd + ms;
            p.ValidJd = true;
            p.IsUtc = true;
            p.IsLocal = false;
            p.ClearYmdHmsTz();
        }

        private static bool ParseDateOrTime(string value, Dt p)
        {
            if (ParseYyyyMmDd(value, p))
                return true;
            if (ParseHhMmSs(value, p))
                return true;
            if (value.Equals("now", StringComparison.OrdinalIgnoreCase))
            {
                SetToCurrent(p);
                return true;
            }

            string numeric = value.Trim(AsciiWsChars);
            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                p.S = val;
                p.RawS = true;
                if (val >= 0.0 && val < 5373484.5)
                {
                    p.IJd = (long)(val * JdToMs + 0.5);
                    p.ValidJd = true;
                }

                return true;
            }

            if (value.Equals("subsec", StringComparison.OrdinalIgnoreCase)
                || value.Equals("subsecond", StringComparison.OrdinalIgnoreCase))
            {
                p.UseSubsec = true;
                SetToCurrent(p);
                return true;
            }

            return false;
        }

        private static bool ParseYyyyMmDd(string z, Dt p)
        {
            bool neg = z.StartsWith('-');
            if (neg)
                z = z.Substring(1);

            if (!GetDigits(z, 4, 0, 9999, out int y, out z))
                return false;
            if (!z.StartsWith('-'))
                return false;
            z = z.Substring(1);
            if (!GetDigits(z, 2, 1, 12, out int m, out z))
                return false;
            if (!z.StartsWith('-'))
                return false;
            z = z.Substring(1);
            if (!GetDigits(z, 2, 1, 31, out int d, out z))
                return false;

            while (z.Length > 0 && (IsAsciiWhitespace(z[0]) || z[0] == 'T'))
                z = z.Substring(1);

            if (ParseHhMmSs(z, p))
            {
                // Time component parsed.
            }
            else if (z.Length == 0)
            {
                p.ValidHms = false;
            }
            else
            {
                return false;
            }

            p.ValidJd = false;
            p.ValidYmd = true;
            p.Y = neg ? -y : y;
            p.M = m;
            p.D = d;
            p.ComputeFloor();
            if (p.Tz != 0)
                p.ComputeJd();
            return true;
        }

        private static bool ParseHhMmSs(string z, Dt p)
        {
            if (!GetDigits(z, 2, 0, 24, out int h, out z))
                return false;
            if (!z.StartsWith(':'))
                return false;
            z = z.Substring(1);
            if (!GetDigits(z, 2, 0, 59, out int m, out z))
                return false;

            int s;
            double ms = 0.0;
            if (z.StartsWith(':'))
            {
                z = z.Substring(1);
                if (!GetDigits(z, 2, 0, 59, out s, out z))
                    return false;

                if (z.Length > 1 && z[0] == '.' && char.IsAsciiDigit(z[1]))
                {
                    double rScale = 1.0;
                    z = z.Substring(1);
                    while (z.Length > 0 && char.IsAsciiDigit(z[0]))
                    {
                        ms = ms * 10.0 + (z[0] - '0');
                        rScale *= 10.0;
                        z = z.Substring(1);
                    }

                    ms /= rScale;
                    if (ms > 0.999)
                        ms = 0.999;
                }
            }
            else
            {
                s = 0;
            }

            p.ValidJd = false;
            p.RawS = false;
            p.ValidHms = true;
            p.H = h;
            p.Min = m;
            p.S = s + ms;

            return !ParseTimezone(z, p);
        }

        private static bool ParseTimezone(string z, Dt p)
        {
            while (z.Length > 0 && IsAsciiWhitespace(z[0]))
                z = z.Substring(1);

            p.Tz = 0;
            if (z.Length == 0)
                return false;

            char c = z[0];
            int sgn;
            if (c == '-')
            {
                sgn = -1;
            }
            else if (c == '+')
            {
                sgn = 1;
            }
            else if (c == 'Z' || c == 'z')
            {
                z = z.Substring(1);
                p.IsLocal = false;
                p.IsUtc = true;
                return CheckTrailingGarbage(z);
            }
            else
            {
                return true;
            }

            z = z.Substring(1);
            if (!GetDigits(z, 2, 0, 14, out int nHr, out z))
                return true;
            if (!z.StartsWith(':'))
                return true;
            z = z.Substring(1);
            if (!GetDigits(z, 2, 0, 59, out int nMn, out z))
                return true;

            p.Tz = sgn * (nMn + nHr * 60);
            if (p.Tz == 0)
            {
                p.IsLocal = false;
                p.IsUtc = true;
            }

            return CheckTrailingGarbage(z);
        }

        private static bool CheckTrailingGarbage(string z)
        {
            while (z.Length > 0 && IsAsciiWhitespace(z[0]))
                z = z.Substring(1);
            return z.Length > 0;
        }

        private static void AutoAdjustDate(Dt p)
        {
            if (!p.RawS || p.ValidJd)
            {
                p.RawS = false;
            }
            else if (p.S >= -210866760000.0 && p.S <= 253402300799.0)
            {
                double r = p.S * 1000.0 + 210866760000000.0;
                p.IJd = (long)(r + 0.5);
                p.ValidJd = true;
                p.RawS = false;
                p.ClearYmdHmsTz();
            }
        }

        private static bool ParseModifier(Dt p, string z, int idx)
        {
            if (z.Length == 0)
                return false;

            switch (char.ToLowerInvariant(z[0]))
            {
                case 'a':
                    if (z.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        if (idx > 0)
                            return false;
                        AutoAdjustDate(p);
                        return true;
                    }

                    return false;
                case 'c':
                    if (z.Equals("ceiling", StringComparison.OrdinalIgnoreCase))
                    {
                        p.ComputeJd();
                        p.ClearYmdHmsTz();
                        p.NFloor = 0;
                        return true;
                    }

                    return false;
                case 'f':
                    if (z.Equals("floor", StringComparison.OrdinalIgnoreCase))
                    {
                        p.ComputeJd();
                        if (p.NFloor != 0)
                        {
                            p.IJd -= (long)p.NFloor * JdToMs;
                            p.NFloor = 0;
                        }

                        p.ClearYmdHmsTz();
                        return true;
                    }

                    return false;
                case 'j':
                    if (z.Equals("julianday", StringComparison.OrdinalIgnoreCase))
                    {
                        if (idx > 0)
                            return false;
                        if (p.ValidJd && p.RawS)
                        {
                            p.RawS = false;
                            return true;
                        }

                        return false;
                    }

                    return false;
                case 'l':
                    if (z.Equals("localtime", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!p.IsLocal)
                        {
                            p.ComputeJd();
                            p.IJd += (long)LocalOffsetSeconds(p.IJd) * 1000;
                            p.ClearYmdHmsTz();
                            p.IsLocal = true;
                            p.IsUtc = false;
                        }

                        return true;
                    }

                    return false;
                case 'u':
                    if (z.Equals("unixepoch", StringComparison.OrdinalIgnoreCase))
                    {
                        if (idx > 0)
                            return false;
                        if (p.RawS)
                        {
                            double r = p.S * 1000.0 + 210866760000000.0;
                            p.IJd = (long)(r + 0.5);
                            p.ValidJd = true;
                            p.RawS = false;
                            p.ClearYmdHmsTz();
                            return true;
                        }

                        return false;
                    }

                    if (z.Equals("utc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!p.IsUtc)
                        {
                            p.ComputeJd();
                            p.IJd -= (long)LocalOffsetSeconds(p.IJd) * 1000;
                            p.ClearYmdHmsTz();
                            p.IsUtc = true;
                            p.IsLocal = false;
                        }

                        return true;
                    }

                    return false;
                case 'w':
                    if (z.Length >= 8 && z.Substring(0, 8).Equals("weekday ", StringComparison.OrdinalIgnoreCase))
                    {
                        string rest = z.Substring(8).Trim();
                        if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double val)
                            && val >= 0.0 && val < 7.0 && (double)(long)val == val)
                        {
                            long n = (long)val;
                            p.ComputeYmdHms();
                            p.ValidJd = false;
                            p.ComputeJd();
                            long zz = ((p.IJd + 129600000) / 86400000) % 7;
                            if (zz > n)
                                zz -= 7;
                            p.IJd += (n - zz) * 86400000;
                            p.ClearYmdHmsTz();
                            return true;
                        }

                        return false;
                    }

                    return false;
                case 's':
                    if (z.Equals("subsec", StringComparison.OrdinalIgnoreCase)
                        || z.Equals("subsecond", StringComparison.OrdinalIgnoreCase))
                    {
                        p.UseSubsec = true;
                        return true;
                    }

                    if (z.Length >= 9 && z.Substring(0, 9).Equals("start of ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!p.ValidJd && !p.ValidYmd && !p.ValidHms)
                            return false;

                        p.ComputeYmd();
                        p.ValidHms = true;
                        p.H = 0;
                        p.Min = 0;
                        p.S = 0.0;
                        p.RawS = false;
                        p.ValidJd = false;
                        p.Tz = 0;
                        p.NFloor = 0;

                        string suffix = z.Substring(9);
                        if (suffix.Equals("month", StringComparison.OrdinalIgnoreCase))
                        {
                            p.D = 1;
                            return true;
                        }

                        if (suffix.Equals("year", StringComparison.OrdinalIgnoreCase))
                        {
                            p.M = 1;
                            p.D = 1;
                            return true;
                        }

                        if (suffix.Equals("day", StringComparison.OrdinalIgnoreCase))
                            return true;

                        return false;
                    }

                    return false;
                case '+':
                case '-':
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    return ParseArithmeticModifier(p, z);
                default:
                    return false;
            }
        }

        private static bool ParseArithmeticModifier(Dt p, string z)
        {
            z = z.Trim(AsciiWsChars);
            bool isNeg = z.StartsWith('-');
            int sign = isNeg ? -1 : 1;
            bool hasSign = z.StartsWith('+') || z.StartsWith('-');
            string cleanZ = hasSign ? z.Substring(1) : z;

            // Case 1: YYYY-MM-DD [HH:MM:SS] arithmetic modifier.
            // SQLite's date.c requires an explicit leading '+'/'-' sign for this form; the
            // unsigned "NNNN-NN-NN[ HH:MM:SS]" spelling is rejected (yields NULL). Match that
            // exactly, even though the Rust reference currently accepts the unsigned spelling.
            if (hasSign && cleanZ.Length >= 10 && cleanZ[4] == '-' && cleanZ[7] == '-')
            {
                bool okY = GetDigits(cleanZ.Substring(0, 4), 4, 0, 9999, out int yy, out _);
                bool okM = GetDigits(cleanZ.Substring(5, 2), 2, 0, 11, out int mm, out _);
                bool okD = GetDigits(cleanZ.Substring(8, 2), 2, 0, 30, out int dd, out _);
                if (okY && okM && okD)
                {
                    string rem = cleanZ.Substring(10);
                    bool validFormat = true;
                    string? timeStr = null;
                    if (rem.Length != 0)
                    {
                        if (rem.StartsWith(' '))
                            timeStr = rem.TrimStart(AsciiWsChars);
                        else
                            validFormat = false;
                    }

                    if (validFormat)
                    {
                        p.ComputeYmdHms();
                        p.ValidJd = false;
                        unchecked
                        {
                            if (isNeg)
                            {
                                p.Y -= yy;
                                p.M -= mm;
                            }
                            else
                            {
                                p.Y += yy;
                                p.M += mm;
                            }

                            long mCurrent = p.M;
                            long x = mCurrent > 0 ? (mCurrent - 1) / 12 : (mCurrent - 12) / 12;
                            p.Y += (int)x;
                            p.M = (int)(mCurrent - x * 12);
                        }

                        p.ComputeFloor();
                        p.ComputeJd();

                        long dayDiff = isNeg ? -(long)dd : dd;
                        p.IJd = unchecked(p.IJd + dayDiff * JdToMs);

                        if (timeStr != null)
                        {
                            var tx = new Dt();
                            if (ParseHhMmSs(timeStr, tx))
                            {
                                tx.ComputeJd();
                                long ms = (long)tx.H * 3600000 + (long)tx.Min * 60000 + (long)(tx.S * 1000.0);
                                p.IJd = unchecked(p.IJd + sign * ms);
                            }
                            else
                            {
                                return false;
                            }
                        }

                        p.ClearYmdHmsTz();
                        return true;
                    }
                }
            }

            // Case 2: HH:MM:SS arithmetic modifier.
            if (z.Contains(':'))
            {
                var tx = new Dt();
                string timeStr = z.StartsWith('+') || z.StartsWith('-') ? z.Substring(1) : z;
                if (ParseHhMmSs(timeStr, tx))
                {
                    tx.ComputeJd();
                    long ms = (long)tx.H * 3600000 + (long)tx.Min * 60000 + (long)(tx.S * 1000.0);
                    p.ComputeJd();
                    p.IJd = unchecked(p.IJd + sign * ms);
                    p.ClearYmdHmsTz();
                    return true;
                }
            }

            // Case 3: NNN <unit> arithmetic modifier.
            string[] parts = z.Split(AsciiWsChars, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
            {
                string unit = parts[1];
                double abs = Math.Abs(amount);

                if (UnitIs(unit, "day"))
                    return ApplyMillisecondUnit(p, amount, abs, 5373485.0, 86400000.0);
                if (UnitIs(unit, "hour"))
                    return ApplyMillisecondUnit(p, amount, abs, 1.2897e+11, 3600000.0);
                if (UnitIs(unit, "minute"))
                    return ApplyMillisecondUnit(p, amount, abs, 7.7379e+12, 60000.0);
                if (UnitIs(unit, "second"))
                    return ApplyMillisecondUnit(p, amount, abs, 4.6427e+14, 1000.0);
                if (UnitIs(unit, "month"))
                    return ApplyMonths(p, amount, abs);
                if (UnitIs(unit, "year"))
                    return ApplyYears(p, amount, abs);
            }

            return false;
        }

        private static bool UnitIs(string unit, string singular)
            => unit.Equals(singular, StringComparison.OrdinalIgnoreCase)
                || unit.Equals(singular + "s", StringComparison.OrdinalIgnoreCase);

        private static bool ApplyMillisecondUnit(Dt p, double amount, double abs, double limit, double scale)
        {
            if (!(abs < limit))
                return false;

            p.ComputeJd();
            double ms = amount * scale;
            double rounder = ms < 0.0 ? -0.5 : 0.5;
            p.IJd = unchecked(p.IJd + (long)(ms + rounder));
            p.NFloor = 0;
            p.ClearYmdHmsTz();
            return true;
        }

        private static bool ApplyMonths(Dt p, double amount, double abs)
        {
            if (!(abs < 176546.0))
                return false;

            p.ComputeYmdHms();
            long intMonths = (long)amount;
            double fracMonths = amount - intMonths;

            long totalMonths = p.M + intMonths;
            long x = totalMonths > 0 ? (totalMonths - 1) / 12 : (totalMonths - 12) / 12;
            p.Y = unchecked(p.Y + (int)x);
            p.M = (int)(totalMonths - x * 12);

            p.ComputeFloor();
            p.ValidJd = false;
            p.ComputeJd();

            if (Math.Abs(fracMonths) > DoubleEpsilon)
            {
                double ms = fracMonths * 30.0 * JdToMs;
                double rounder = ms < 0.0 ? -0.5 : 0.5;
                p.IJd = unchecked(p.IJd + (long)(ms + rounder));
            }

            p.ClearYmdHmsTz();
            return true;
        }

        private static bool ApplyYears(Dt p, double amount, double abs)
        {
            if (!(abs < 14713.0))
                return false;

            p.ComputeYmdHms();
            long intYears = (long)amount;
            double fracYears = amount - intYears;

            p.Y = unchecked(p.Y + (int)intYears);

            p.ComputeFloor();
            p.ValidJd = false;
            p.ComputeJd();

            if (Math.Abs(fracYears) > DoubleEpsilon)
            {
                double ms = fracYears * 365.0 * JdToMs;
                double rounder = ms < 0.0 ? -0.5 : 0.5;
                p.IJd = unchecked(p.IJd + (long)(ms + rounder));
            }

            p.ClearYmdHmsTz();
            return true;
        }

        private static int LocalOffsetSeconds(long iJd)
        {
            long timestamp = (iJd - UnixEpochIJd) / 1000;
            try
            {
                var instant = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                return (int)TimeZoneInfo.Local.GetUtcOffset(instant).TotalSeconds;
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
        }

        private static bool GetDigits(string z, int digits, int minVal, int maxVal, out int val, out string rem)
        {
            val = 0;
            rem = z;
            if (z.Length < digits)
                return false;
            for (int i = 0; i < digits; i++)
            {
                if (!char.IsAsciiDigit(z[i]))
                    return false;
            }

            if (!int.TryParse(z.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out val))
                return false;
            if (val < minVal || val > maxVal)
                return false;

            rem = z.Substring(digits);
            return true;
        }

        private static bool IsAsciiWhitespace(char c)
            => c is ' ' or '\t' or '\n' or '\r' or '\u000C';
    }

    // SQLite-compatible JSON scalar functions. JSON text keeps an ephemeral subtype while an
    // expression is evaluated, so constructors and mutators embed direct JSON function results.
    // Column affinity strips that subtype before values are stored or later read as column data.
    //
    // The parser is strict RFC-8259. This yields exact parity with SQLite's json_valid()
    // (whose default behavior is strict) and with json()/json_type()/json_extract() for every
    // RFC-8259-conformant input. SQLite additionally accepts non-standard JSON5 leniencies
    // (leading '+', a trailing '.', a leading '.', trailing commas, single quotes, unquoted
    // object keys, hexadecimal, comments, and raw control characters inside strings). Those are
    // deliberately rejected here as "malformed JSON" instead of returning a canonicalized value,
    // matching the task's requirement to reject unsupported input rather than guess.
    private static class SqliteJson
    {
        private enum JKind
        {
            Null,
            True,
            False,
            Integer,
            Real,
            Text,
            Array,
            Object,
        }

        private sealed class JMember
        {
            public string RawKey = string.Empty;
            public string Key = string.Empty;
            public JNode Value = null!;
        }

        private sealed class JNode
        {
            public JKind Kind;
            public string Raw = string.Empty; // Verbatim token for numbers and quoted strings.
            public string Str = string.Empty; // Decoded text for Text nodes.
            public List<JNode>? Items;
            public List<JMember>? Members;
        }

        private enum MutationMode
        {
            Set,
            Insert,
            Replace,
        }

        private enum PathStepKind
        {
            Key,
            Index,
            FromEndIndex,
            Append,
        }

        private readonly record struct PathStep(PathStepKind Kind, string? Key, long Index);

        internal static SqlValue Json(IReadOnlyList<SqlValue> args)
        {
            RequireArgumentCount("json", args, 1);
            var value = args[0];
            switch (value.Kind)
            {
                case SqlValueKind.Null:
                    return SqlValue.Null;
                case SqlValueKind.Integer:
                    return SqlValue.JsonText(value.AsInteger().ToString(CultureInfo.InvariantCulture));
                case SqlValueKind.Real:
                    return SqlValue.JsonText(FormatJsonReal(value.AsReal()));
                default:
                    var node = TryParse(InputText(value));
                    if (node is null)
                        throw new EmbeddedSqlException("malformed JSON");
                    return SqlValue.JsonText(Serialize(node));
            }
        }

        internal static SqlValue JsonValid(IReadOnlyList<SqlValue> args)
        {
            RequireArgumentCount("json_valid", args, 1);
            var value = args[0];
            switch (value.Kind)
            {
                case SqlValueKind.Null:
                    return SqlValue.Null;
                case SqlValueKind.Integer:
                case SqlValueKind.Real:
                    return SqlValue.Integer(1);
                default:
                    return SqlValue.Integer(TryParse(InputText(value)) is null ? 0 : 1);
            }
        }

        internal static SqlValue JsonType(IReadOnlyList<SqlValue> args)
        {
            if (args.Count is < 1 or > 2)
                throw new EmbeddedSqlException("wrong number of arguments to function json_type()");

            var value = args[0];
            if (value.Kind == SqlValueKind.Null)
                return SqlValue.Null;

            var root = ParseOrThrow(value);

            if (args.Count == 2)
            {
                var pathArg = args[1];
                if (pathArg.Kind == SqlValueKind.Null)
                    return SqlValue.Null;

                var (found, node) = Navigate(root, RequirePathText(pathArg));
                if (!found)
                    return SqlValue.Null;
                return SqlValue.Text(TypeName(node));
            }

            return SqlValue.Text(TypeName(root));
        }

        internal static SqlValue JsonExtract(IReadOnlyList<SqlValue> args)
        {
            if (args.Count < 1)
                throw new EmbeddedSqlException("wrong number of arguments to function json_extract()");

            var value = args[0];
            if (value.Kind == SqlValueKind.Null)
                return SqlValue.Null;

            var root = ParseOrThrow(value);

            if (args.Count == 1)
                return SqlValue.Null;

            // A NULL anywhere in the path list makes the whole result NULL.
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i].Kind == SqlValueKind.Null)
                    return SqlValue.Null;
            }

            if (args.Count == 2)
            {
                var (found, node) = Navigate(root, RequirePathText(args[1]));
                return found ? NodeToSql(node) : SqlValue.Null;
            }

            var result = new StringBuilder();
            result.Append('[');
            for (int i = 1; i < args.Count; i++)
            {
                if (i > 1)
                    result.Append(',');
                var (found, node) = Navigate(root, RequirePathText(args[i]));
                result.Append(found ? Serialize(node) : "null");
            }

            result.Append(']');
            return SqlValue.JsonText(result.ToString());
        }

        internal static SqlValue JsonArrow(SqlValue value, SqlValue operand, bool textResult)
        {
            if (!TryGetArrowPath(operand, out var path))
                return SqlValue.Null;

            var root = ParseOrThrow(value);
            var (found, node) = Navigate(root, path);
            if (!found)
                return SqlValue.Null;

            if (!textResult)
                return SqlValue.JsonText(Serialize(node));

            return node.Kind is JKind.Array or JKind.Object
                ? SqlValue.Text(Serialize(node))
                : NodeToSql(node);
        }

        internal static SqlValue JsonArray(IReadOnlyList<SqlValue> args)
        {
            var items = new List<JNode>(args.Count);
            for (int i = 0; i < args.Count; i++)
                items.Add(ValueToNode(args[i]));
            return SqlValue.JsonText(Serialize(new JNode { Kind = JKind.Array, Items = items }));
        }

        internal static SqlValue JsonArrayLength(IReadOnlyList<SqlValue> args)
        {
            if (args.Count is < 1 or > 2)
                throw new EmbeddedSqlException("wrong number of arguments to function json_array_length()");
            if (args[0].Kind == SqlValueKind.Null || (args.Count == 2 && args[1].Kind == SqlValueKind.Null))
                return SqlValue.Null;

            var root = ParseOrThrow(args[0]);
            if (args.Count == 2)
            {
                var (found, node) = Navigate(root, RequirePathText(args[1]));
                if (!found)
                    return SqlValue.Null;
                root = node;
            }

            return SqlValue.Integer(root.Kind == JKind.Array ? root.Items!.Count : 0);
        }

        internal static SqlValue JsonObject(IReadOnlyList<SqlValue> args)
        {
            if ((args.Count & 1) != 0)
                throw new EmbeddedSqlException("json_object() requires an even number of arguments");

            var members = new List<JMember>(args.Count / 2);
            for (int i = 0; i < args.Count; i += 2)
            {
                if (args[i].Kind != SqlValueKind.Text)
                    throw new EmbeddedSqlException("json_object() labels must be TEXT");

                string key = args[i].AsText();
                members.Add(new JMember
                {
                    RawKey = QuoteString(key),
                    Key = key,
                    Value = ValueToNode(args[i + 1]),
                });
            }

            return SqlValue.JsonText(Serialize(new JNode { Kind = JKind.Object, Members = members }));
        }

        internal static SqlValue JsonQuote(IReadOnlyList<SqlValue> args)
        {
            RequireArgumentCount("json_quote", args, 1);
            return SqlValue.JsonText(Serialize(ValueToNode(args[0])));
        }

        internal static SqlValue JsonErrorPosition(IReadOnlyList<SqlValue> args)
        {
            RequireArgumentCount("json_error_position", args, 1);
            if (args[0].Kind == SqlValueKind.Null)
                return SqlValue.Null;

            string input = args[0].Kind switch
            {
                SqlValueKind.Integer => args[0].AsInteger().ToString(CultureInfo.InvariantCulture),
                SqlValueKind.Real => FormatJsonReal(args[0].AsReal()),
                _ => InputText(args[0]),
            };
            return SqlValue.Integer(ParseErrorPosition(input));
        }

        internal static SqlValue JsonRemove(IReadOnlyList<SqlValue> args)
        {
            if (args.Count < 1)
                throw new EmbeddedSqlException("wrong number of arguments to function json_remove()");
            if (args[0].Kind == SqlValueKind.Null)
                return SqlValue.Null;

            var root = ParseOrThrow(args[0]);
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i].Kind == SqlValueKind.Null)
                    return SqlValue.Null;

                string path = RequirePathText(args[i]);
                var (found, _) = Navigate(root, path);
                if (!found)
                    continue;

                var steps = ParsePath(path);
                if (steps.Count == 0)
                    return SqlValue.Null;
                Remove(root, steps);
            }

            return SqlValue.JsonText(Serialize(root));
        }

        internal static SqlValue JsonSet(IReadOnlyList<SqlValue> args)
            => JsonModify(args, MutationMode.Set, "json_set");

        internal static SqlValue JsonInsert(IReadOnlyList<SqlValue> args)
            => JsonModify(args, MutationMode.Insert, "json_insert");

        internal static SqlValue JsonReplace(IReadOnlyList<SqlValue> args)
            => JsonModify(args, MutationMode.Replace, "json_replace");

        internal static SqlValue JsonPatch(IReadOnlyList<SqlValue> args)
        {
            RequireArgumentCount("json_patch", args, 2);
            if (args[0].Kind == SqlValueKind.Null || args[1].Kind == SqlValueKind.Null)
                return SqlValue.Null;

            return SqlValue.JsonText(Serialize(MergePatch(ParseOrThrow(args[0]), ParseOrThrow(args[1]))));
        }

        private static SqlValue JsonModify(
            IReadOnlyList<SqlValue> args,
            MutationMode mode,
            string functionName)
        {
            if ((args.Count & 1) == 0)
                throw new EmbeddedSqlException($"wrong number of arguments to function {functionName}()");
            if (args[0].Kind == SqlValueKind.Null)
                return SqlValue.Null;

            var root = ParseOrThrow(args[0]);
            for (int i = 1; i < args.Count; i += 2)
            {
                if (args[i].Kind == SqlValueKind.Null)
                    continue;

                var value = ValueToNode(args[i + 1]);
                string path = RequirePathText(args[i]);

                if (mode == MutationMode.Replace)
                {
                    var (found, _) = Navigate(root, path);
                    if (!found)
                        continue;
                }

                List<PathStep> steps;
                try
                {
                    steps = ParsePath(path);
                }
                catch (EmbeddedSqlException)
                {
                    if (mode != MutationMode.Replace && MutationPathCannotReachValue(root, path, mode))
                        continue;
                    throw;
                }

                var candidate = Clone(root);
                if (TryModify(ref candidate, steps, value, mode))
                    root = candidate;
            }

            return SqlValue.JsonText(Serialize(root));
        }

        private static JNode ValueToNode(SqlValue value)
        {
            if (value.IsJson)
                return ParseOrThrow(value);

            return value.Kind switch
            {
                SqlValueKind.Null => new JNode { Kind = JKind.Null },
                SqlValueKind.Integer => new JNode
                {
                    Kind = JKind.Integer,
                    Raw = value.AsInteger().ToString(CultureInfo.InvariantCulture),
                },
                SqlValueKind.Real => new JNode
                {
                    Kind = JKind.Real,
                    Raw = FormatJsonValueReal(value.AsReal()),
                },
                SqlValueKind.Text => new JNode
                {
                    Kind = JKind.Text,
                    Raw = QuoteString(value.AsText()),
                    Str = value.AsText(),
                },
                SqlValueKind.Blob => throw new EmbeddedSqlException("JSON cannot hold BLOB values"),
                _ => throw new InvalidOperationException(),
            };
        }

        private static List<PathStep> ParsePath(string path)
        {
            if (path.Length == 0 || path[0] != '$')
                throw BadPath(path);

            var steps = new List<PathStep>();
            int i = 1;
            while (i < path.Length)
            {
                if (path[i] == '.')
                {
                    i++;
                    string key;
                    if (i < path.Length && path[i] == '"')
                    {
                        var parser = new Parser(path, i);
                        var node = parser.ParseString();
                        if (node is null)
                            throw BadPath(path);
                        i = parser.Pos;
                        key = node.Str;
                    }
                    else
                    {
                        int start = i;
                        while (i < path.Length && path[i] != '.' && path[i] != '[')
                            i++;
                        if (i == start)
                            throw BadPath(path);
                        key = path.Substring(start, i - start);
                    }

                    steps.Add(new PathStep(PathStepKind.Key, key, 0));
                    continue;
                }

                if (path[i] != '[')
                    throw BadPath(path);

                i++;
                if (i < path.Length && path[i] == '#')
                {
                    i++;
                    if (i < path.Length && path[i] == ']')
                    {
                        i++;
                        steps.Add(new PathStep(PathStepKind.Append, null, 0));
                        continue;
                    }

                    if (i >= path.Length || path[i] != '-')
                        throw BadPath(path);
                    i++;
                    if (!ReadDigits(path, ref i, out long index) || i >= path.Length || path[i] != ']')
                        throw BadPath(path);
                    i++;
                    steps.Add(new PathStep(PathStepKind.FromEndIndex, null, index));
                    continue;
                }

                if (!ReadDigits(path, ref i, out long absolute) || i >= path.Length || path[i] != ']')
                    throw BadPath(path);
                i++;
                steps.Add(new PathStep(PathStepKind.Index, null, absolute));
            }

            return steps;
        }

        private static bool MutationPathCannotReachValue(JNode root, string path, MutationMode mode)
        {
            if (path.Length == 0 || path[0] != '$')
                throw BadPath(path);

            JNode current = root;
            int i = 1;
            while (i < path.Length)
            {
                if (path[i] == '.')
                {
                    i++;
                    string key;
                    if (i < path.Length && path[i] == '"')
                    {
                        var parser = new Parser(path, i);
                        var node = parser.ParseString();
                        if (node is null)
                            throw BadPath(path);
                        i = parser.Pos;
                        key = node.Str;
                    }
                    else
                    {
                        int start = i;
                        while (i < path.Length && path[i] != '.' && path[i] != '[')
                            i++;
                        if (i == start)
                            throw BadPath(path);
                        key = path.Substring(start, i - start);
                    }

                    if (current.Kind != JKind.Object)
                        return true;
                    int memberIndex = FindMemberIndex(current, key);
                    if (memberIndex < 0)
                        return false;
                    current = current.Members![memberIndex].Value;
                    continue;
                }

                if (path[i] != '[')
                    throw BadPath(path);
                if (current.Kind != JKind.Array)
                    return true;

                i++;
                PathStep step;
                if (i < path.Length && path[i] == '#')
                {
                    i++;
                    if (i < path.Length && path[i] == ']')
                    {
                        i++;
                        step = new PathStep(PathStepKind.Append, null, 0);
                    }
                    else
                    {
                        if (i >= path.Length || path[i] != '-')
                            throw BadPath(path);
                        i++;
                        if (!ReadDigits(path, ref i, out long index) || i >= path.Length || path[i] != ']')
                            throw BadPath(path);
                        i++;
                        step = new PathStep(PathStepKind.FromEndIndex, null, index);
                    }
                }
                else
                {
                    if (!ReadDigits(path, ref i, out long absolute) || i >= path.Length || path[i] != ']')
                        throw BadPath(path);
                    i++;
                    step = new PathStep(PathStepKind.Index, null, absolute);
                }

                long arrayIndex = ResolveArrayIndex(current.Items!.Count, step);
                if (arrayIndex >= 0 && arrayIndex < current.Items.Count)
                {
                    current = current.Items[(int)arrayIndex];
                    continue;
                }

                return mode != MutationMode.Replace && arrayIndex != current.Items.Count;
            }

            return false;
        }

        private static bool TryModify(ref JNode root, IReadOnlyList<PathStep> steps, JNode value, MutationMode mode)
        {
            if (steps.Count == 0)
            {
                if (mode == MutationMode.Insert)
                    return false;
                root = Clone(value);
                return true;
            }

            JNode current = root;
            for (int i = 0; i < steps.Count - 1; i++)
            {
                var step = steps[i];
                var next = steps[i + 1];
                if (step.Kind == PathStepKind.Key)
                {
                    if (current.Kind != JKind.Object)
                        return false;

                    int memberIndex = FindMemberIndex(current, step.Key!);
                    if (memberIndex >= 0)
                    {
                        current = current.Members![memberIndex].Value;
                        continue;
                    }

                    if (mode == MutationMode.Replace)
                        return false;

                    var created = ContainerFor(next);
                    current.Members!.Add(new JMember
                    {
                        RawKey = QuoteString(step.Key!),
                        Key = step.Key!,
                        Value = created,
                    });
                    current = created;
                    continue;
                }

                if (current.Kind != JKind.Array)
                    return false;

                long index = ResolveArrayIndex(current.Items!.Count, step);
                if (index >= 0 && index < current.Items.Count)
                {
                    current = current.Items[(int)index];
                    continue;
                }

                if (mode == MutationMode.Replace || index != current.Items.Count)
                    return false;

                var appended = ContainerFor(next);
                current.Items.Add(appended);
                current = appended;
            }

            var final = steps[^1];
            if (final.Kind == PathStepKind.Key)
            {
                if (current.Kind != JKind.Object)
                    return false;

                int memberIndex = FindMemberIndex(current, final.Key!);
                if (memberIndex >= 0)
                {
                    if (mode == MutationMode.Insert)
                        return false;
                    current.Members![memberIndex].Value = Clone(value);
                    return true;
                }

                if (mode == MutationMode.Replace)
                    return false;
                current.Members!.Add(new JMember
                {
                    RawKey = QuoteString(final.Key!),
                    Key = final.Key!,
                    Value = Clone(value),
                });
                return true;
            }

            if (current.Kind != JKind.Array)
                return false;

            long arrayIndex = ResolveArrayIndex(current.Items!.Count, final);
            if (arrayIndex >= 0 && arrayIndex < current.Items.Count)
            {
                if (mode == MutationMode.Insert)
                    return false;
                current.Items[(int)arrayIndex] = Clone(value);
                return true;
            }

            if (mode == MutationMode.Replace || arrayIndex != current.Items.Count)
                return false;
            current.Items.Add(Clone(value));
            return true;
        }

        private static void Remove(JNode root, IReadOnlyList<PathStep> steps)
        {
            JNode current = root;
            for (int i = 0; i < steps.Count - 1; i++)
            {
                var step = steps[i];
                if (step.Kind == PathStepKind.Key)
                {
                    if (current.Kind != JKind.Object)
                        return;
                    int memberIndex = FindMemberIndex(current, step.Key!);
                    if (memberIndex < 0)
                        return;
                    current = current.Members![memberIndex].Value;
                    continue;
                }

                if (current.Kind != JKind.Array)
                    return;
                long index = ResolveArrayIndex(current.Items!.Count, step);
                if (index < 0 || index >= current.Items.Count)
                    return;
                current = current.Items[(int)index];
            }

            var final = steps[^1];
            if (final.Kind == PathStepKind.Key)
            {
                if (current.Kind != JKind.Object)
                    return;
                int memberIndex = FindMemberIndex(current, final.Key!);
                if (memberIndex >= 0)
                    current.Members!.RemoveAt(memberIndex);
                return;
            }

            if (current.Kind != JKind.Array)
                return;
            long arrayIndex = ResolveArrayIndex(current.Items!.Count, final);
            if (arrayIndex >= 0 && arrayIndex < current.Items.Count)
                current.Items.RemoveAt((int)arrayIndex);
        }

        private static JNode MergePatch(JNode target, JNode patch)
        {
            if (patch.Kind != JKind.Object)
                return Clone(patch);

            var merged = target.Kind == JKind.Object
                ? Clone(target)
                : new JNode { Kind = JKind.Object, Members = [] };
            foreach (var patchMember in patch.Members!)
            {
                int targetIndex = FindMemberIndex(merged, patchMember.Key);
                if (patchMember.Value.Kind == JKind.Null)
                {
                    if (targetIndex >= 0)
                        merged.Members!.RemoveAt(targetIndex);
                    continue;
                }

                var value = targetIndex >= 0
                    ? MergePatch(merged.Members![targetIndex].Value, patchMember.Value)
                    : MergePatch(new JNode { Kind = JKind.Object, Members = [] }, patchMember.Value);
                if (targetIndex >= 0)
                    merged.Members![targetIndex].Value = value;
                else
                    merged.Members!.Add(new JMember
                    {
                        RawKey = patchMember.RawKey,
                        Key = patchMember.Key,
                        Value = value,
                    });
            }

            return merged;
        }

        private static long ResolveArrayIndex(int length, PathStep step)
        {
            return step.Kind switch
            {
                PathStepKind.Index => step.Index,
                PathStepKind.FromEndIndex => (long)length - step.Index,
                PathStepKind.Append => length,
                _ => throw new InvalidOperationException(),
            };
        }

        private static int FindMemberIndex(JNode node, string key)
        {
            for (int i = 0; i < node.Members!.Count; i++)
            {
                if (string.Equals(node.Members[i].Key, key, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private static JNode ContainerFor(PathStep next)
            => next.Kind == PathStepKind.Key
                ? new JNode { Kind = JKind.Object, Members = [] }
                : new JNode { Kind = JKind.Array, Items = [] };

        private static JNode Clone(JNode node)
        {
            var clone = new JNode
            {
                Kind = node.Kind,
                Raw = node.Raw,
                Str = node.Str,
            };
            if (node.Items is not null)
                clone.Items = node.Items.Select(Clone).ToList();
            if (node.Members is not null)
            {
                clone.Members = node.Members.Select(member => new JMember
                {
                    RawKey = member.RawKey,
                    Key = member.Key,
                    Value = Clone(member.Value),
                }).ToList();
            }

            return clone;
        }

        private static string QuoteString(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        private static string FormatJsonValueReal(double d)
        {
            if (double.IsPositiveInfinity(d))
                return "9.0e+999";
            if (double.IsNegativeInfinity(d))
                return "-9.0e+999";
            return FormatJsonReal(d);
        }

        private static JNode ParseOrThrow(SqlValue value)
        {
            var node = value.Kind switch
            {
                SqlValueKind.Integer => TryParse(value.AsInteger().ToString(CultureInfo.InvariantCulture)),
                SqlValueKind.Real => TryParse(FormatJsonReal(value.AsReal())),
                _ => TryParse(InputText(value)),
            };

            if (node is null)
                throw new EmbeddedSqlException("malformed JSON");
            return node;
        }

        private static string InputText(SqlValue value)
            => value.Kind == SqlValueKind.Blob
                ? Encoding.UTF8.GetString(value.AsBlob().Span)
                : value.AsText();

        private static string RequirePathText(SqlValue value)
            => value.Kind switch
            {
                SqlValueKind.Text => value.AsText(),
                SqlValueKind.Blob => Encoding.UTF8.GetString(value.AsBlob().Span),
                SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
                SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
                _ => string.Empty,
            };

        private static bool TryGetArrowPath(SqlValue value, out string path)
        {
            switch (value.Kind)
            {
                case SqlValueKind.Integer:
                    {
                        var index = value.AsInteger();
                        path = index >= 0
                            ? $"$[{index}]"
                            : $"$[#{index}]";
                        return true;
                    }
                case SqlValueKind.Text:
                    {
                        var nameOrPath = value.AsText();
                        path = nameOrPath.Length == 0 || nameOrPath.StartsWith('$')
                            ? nameOrPath
                            : "$." + QuoteString(nameOrPath);
                        return true;
                    }
                default:
                    path = string.Empty;
                    return false;
            }
        }

        private static string TypeName(JNode node) => node.Kind switch
        {
            JKind.Null => "null",
            JKind.True => "true",
            JKind.False => "false",
            JKind.Integer => "integer",
            JKind.Real => "real",
            JKind.Text => "text",
            JKind.Array => "array",
            JKind.Object => "object",
            _ => throw new InvalidOperationException(),
        };

        private static SqlValue NodeToSql(JNode node)
        {
            switch (node.Kind)
            {
                case JKind.Null:
                    return SqlValue.Null;
                case JKind.True:
                    return SqlValue.Integer(1);
                case JKind.False:
                    return SqlValue.Integer(0);
                case JKind.Integer:
                    if (long.TryParse(node.Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                        return SqlValue.Integer(l);
                    return SqlValue.Real(ParseDouble(node.Raw));
                case JKind.Real:
                    return SqlValue.Real(ParseDouble(node.Raw));
                case JKind.Text:
                    return SqlValue.Text(node.Str);
                default:
                    return SqlValue.JsonText(Serialize(node));
            }
        }

        private static double ParseDouble(string raw)
            => double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static string Serialize(JNode node)
        {
            var sb = new StringBuilder();
            Serialize(node, sb);
            return sb.ToString();
        }

        private static void Serialize(JNode node, StringBuilder sb)
        {
            switch (node.Kind)
            {
                case JKind.Null:
                    sb.Append("null");
                    break;
                case JKind.True:
                    sb.Append("true");
                    break;
                case JKind.False:
                    sb.Append("false");
                    break;
                case JKind.Integer:
                case JKind.Real:
                case JKind.Text:
                    sb.Append(node.Raw);
                    break;
                case JKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < node.Items!.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        Serialize(node.Items[i], sb);
                    }

                    sb.Append(']');
                    break;
                case JKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < node.Members!.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        sb.Append(node.Members[i].RawKey);
                        sb.Append(':');
                        Serialize(node.Members[i].Value, sb);
                    }

                    sb.Append('}');
                    break;
            }
        }

        private static (bool Found, JNode Node) Navigate(JNode root, string path)
        {
            if (path.Length == 0 || path[0] != '$')
                throw BadPath(path);

            JNode current = root;
            int i = 1;
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '.')
                {
                    i++;
                    string keyName;
                    if (i < path.Length && path[i] == '"')
                    {
                        var parser = new Parser(path, i);
                        var strNode = parser.ParseString();
                        if (strNode is null)
                            throw BadPath(path);
                        i = parser.Pos;
                        keyName = strNode.Str;
                    }
                    else
                    {
                        int start = i;
                        while (i < path.Length && path[i] != '.' && path[i] != '[')
                            i++;
                        if (i == start)
                            throw BadPath(path);
                        keyName = path.Substring(start, i - start);
                    }

                    // The label is validated syntactically above: SQLite rejects an empty label
                    // (e.g. "$.") as a bad path even when the current node is not an object. Only a
                    // syntactically valid label against a non-object degrades to "not found".
                    if (current.Kind != JKind.Object)
                        return (false, root);

                    JNode? match = null;
                    foreach (var member in current.Members!)
                    {
                        if (string.Equals(member.Key, keyName, StringComparison.Ordinal))
                        {
                            match = member.Value;
                            break;
                        }
                    }

                    if (match is null)
                        return (false, root);
                    current = match;
                }
                else if (c == '[')
                {
                    // SQLite checks the container type before parsing the subscript: a '[' step
                    // against a non-array yields "not found" (NULL) without validating the bracket
                    // contents, whereas an invalid subscript on an array is a hard "bad path" error.
                    if (current.Kind != JKind.Array)
                        return (false, root);

                    i++;
                    long length = current.Items!.Count;
                    bool fromEnd = false;
                    long value;
                    if (i < path.Length && path[i] == '#')
                    {
                        fromEnd = true;
                        i++;
                        if (i < path.Length && path[i] == '-')
                        {
                            i++;
                            if (!ReadDigits(path, ref i, out value))
                                throw BadPath(path);
                        }
                        else
                        {
                            value = 0;
                        }
                    }
                    else if (i < path.Length && char.IsAsciiDigit(path[i]))
                    {
                        if (!ReadDigits(path, ref i, out value))
                            throw BadPath(path);
                    }
                    else
                    {
                        throw BadPath(path);
                    }

                    if (i >= path.Length || path[i] != ']')
                        throw BadPath(path);
                    i++;

                    long actual = fromEnd ? length - value : value;
                    if (actual < 0 || actual >= length)
                        return (false, root);
                    current = current.Items[(int)actual];
                }
                else
                {
                    throw BadPath(path);
                }
            }

            return (true, current);
        }

        private static bool ReadDigits(string s, ref int i, out long value)
        {
            int start = i;
            long acc = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                if (acc < 100000000000000000L)
                    acc = acc * 10 + (s[i] - '0');
                i++;
            }

            value = acc;
            return i > start;
        }

        private static EmbeddedSqlException BadPath(string path)
            => new($"bad JSON path: '{path}'");

        private static string FormatJsonReal(double d)
        {
            if (double.IsNaN(d))
                return "null";
            if (double.IsPositiveInfinity(d))
                return "9e999";
            if (double.IsNegativeInfinity(d))
                return "-9e999";
            if (d == 0.0)
                return "0.0";

            string s = d.ToString("G15", CultureInfo.InvariantCulture);
            int e = s.IndexOfAny(new[] { 'e', 'E' });
            if (e >= 0)
            {
                string mantissa = s.Substring(0, e);
                string exponent = s.Substring(e + 1);
                char sign = '+';
                if (exponent.StartsWith('+') || exponent.StartsWith('-'))
                {
                    sign = exponent[0];
                    exponent = exponent.Substring(1);
                }

                exponent = exponent.TrimStart('0');
                if (exponent.Length < 2)
                    exponent = exponent.PadLeft(2, '0');
                if (!mantissa.Contains('.'))
                    mantissa += ".0";
                return mantissa + "e" + sign + exponent;
            }

            if (!s.Contains('.'))
                s += ".0";
            return s;
        }

        private static int ParseErrorPosition(string input)
        {
            var parser = new Parser(input, 0);
            parser.SkipWs();
            int rootStart = parser.Pos;
            var node = parser.ParseValue();
            if (node is not null)
            {
                parser.SkipWs();
                if (parser.AtEnd)
                    return 0;
            }

            return parser.ErrorPosition >= 0 ? parser.ErrorPosition + 1 : rootStart + 1;
        }

        private static JNode? TryParse(string s)
        {
            var parser = new Parser(s, 0);
            parser.SkipWs();
            var node = parser.ParseValue();
            if (node is null)
                return null;
            parser.SkipWs();
            return parser.AtEnd ? node : null;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            private int _errorPosition = -1;

            public Parser(string s, int start)
            {
                _s = s;
                _i = start;
            }

            public int Pos => _i;

            public bool AtEnd => _i >= _s.Length;

            public int ErrorPosition => _errorPosition;

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c is ' ' or '\t' or '\n' or '\r')
                        _i++;
                    else
                        break;
                }
            }

            public JNode? ParseValue()
            {
                if (_i >= _s.Length)
                {
                    RecordFailure();
                    return null;
                }

                char c = _s[_i];
                switch (c)
                {
                    case '{':
                        return ParseObject();
                    case '[':
                        return ParseArray();
                    case '"':
                        return ParseString();
                    case 't':
                        return ParseLiteral("true", JKind.True);
                    case 'f':
                        return ParseLiteral("false", JKind.False);
                    case 'n':
                        return ParseLiteral("null", JKind.Null);
                    default:
                        if (c == '-' || char.IsAsciiDigit(c))
                            return ParseNumber();
                        RecordFailure();
                        return null;
                }
            }

            private JNode? ParseLiteral(string literal, JKind kind)
            {
                int start = _i;
                if (_i + literal.Length > _s.Length)
                {
                    RecordFailure(start);
                    return null;
                }
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    RecordFailure(start);
                    return null;
                }
                _i += literal.Length;
                return new JNode { Kind = kind };
            }

            private JNode? ParseNumber()
            {
                int start = _i;
                if (_i < _s.Length && _s[_i] == '-')
                    _i++;

                if (_i >= _s.Length)
                {
                    RecordFailure();
                    return null;
                }

                if (_s[_i] == '0')
                {
                    _i++;
                }
                else if (_s[_i] is >= '1' and <= '9')
                {
                    while (_i < _s.Length && char.IsAsciiDigit(_s[_i]))
                        _i++;
                }
                else
                {
                    RecordFailure();
                    return null;
                }

                bool isReal = false;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    isReal = true;
                    _i++;
                    if (_i >= _s.Length || !char.IsAsciiDigit(_s[_i]))
                    {
                        RecordFailure();
                        return null;
                    }
                    while (_i < _s.Length && char.IsAsciiDigit(_s[_i]))
                        _i++;
                }

                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    isReal = true;
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-'))
                        _i++;
                    if (_i >= _s.Length || !char.IsAsciiDigit(_s[_i]))
                    {
                        RecordFailure();
                        return null;
                    }
                    while (_i < _s.Length && char.IsAsciiDigit(_s[_i]))
                        _i++;
                }

                string raw = _s.Substring(start, _i - start);
                return new JNode { Kind = isReal ? JKind.Real : JKind.Integer, Raw = raw };
            }

            public JNode? ParseString()
            {
                if (_i >= _s.Length || _s[_i] != '"')
                {
                    RecordFailure();
                    return null;
                }

                int start = _i;
                _i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length)
                    {
                        RecordFailure();
                        return null;
                    }

                    char c = _s[_i];
                    if (c == '"')
                    {
                        _i++;
                        break;
                    }

                    if (c == '\\')
                    {
                        _i++;
                        if (_i >= _s.Length)
                        {
                            RecordFailure();
                            return null;
                        }
                        char escaped = _s[_i];
                        switch (escaped)
                        {
                            case '"':
                                sb.Append('"');
                                break;
                            case '\\':
                                sb.Append('\\');
                                break;
                            case '/':
                                sb.Append('/');
                                break;
                            case 'b':
                                sb.Append('\b');
                                break;
                            case 'f':
                                sb.Append('\f');
                                break;
                            case 'n':
                                sb.Append('\n');
                                break;
                            case 'r':
                                sb.Append('\r');
                                break;
                            case 't':
                                sb.Append('\t');
                                break;
                            case 'u':
                                if (_i + 4 >= _s.Length)
                                {
                                    RecordFailure();
                                    return null;
                                }
                                int cp = 0;
                                for (int k = 1; k <= 4; k++)
                                {
                                    int digit = HexValue(_s[_i + k]);
                                    if (digit < 0)
                                    {
                                        RecordFailure(_i + k);
                                        return null;
                                    }
                                    cp = (cp * 16) + digit;
                                }

                                sb.Append((char)cp);
                                _i += 4;
                                break;
                            default:
                                RecordFailure();
                                return null;
                        }

                        _i++;
                    }
                    else if (c < 0x20)
                    {
                        RecordFailure();
                        return null;
                    }
                    else
                    {
                        sb.Append(c);
                        _i++;
                    }
                }

                string raw = _s.Substring(start, _i - start);
                return new JNode { Kind = JKind.Text, Raw = raw, Str = sb.ToString() };
            }

            private JNode? ParseArray()
            {
                _i++;
                var items = new List<JNode>();
                SkipWs();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    return new JNode { Kind = JKind.Array, Items = items };
                }

                while (true)
                {
                    SkipWs();
                    var v = ParseValue();
                    if (v is null)
                        return null;
                    items.Add(v);
                    SkipWs();
                    if (_i >= _s.Length)
                    {
                        RecordFailure();
                        return null;
                    }
                    char c = _s[_i];
                    if (c == ',')
                    {
                        _i++;
                        continue;
                    }

                    if (c == ']')
                    {
                        _i++;
                        break;
                    }

                    RecordFailure();
                    return null;
                }

                return new JNode { Kind = JKind.Array, Items = items };
            }

            private JNode? ParseObject()
            {
                _i++;
                var members = new List<JMember>();
                SkipWs();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    return new JNode { Kind = JKind.Object, Members = members };
                }

                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != '"')
                    {
                        RecordFailure();
                        return null;
                    }
                    var key = ParseString();
                    if (key is null)
                        return null;
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ':')
                    {
                        RecordFailure();
                        return null;
                    }
                    _i++;
                    SkipWs();
                    var v = ParseValue();
                    if (v is null)
                        return null;
                    members.Add(new JMember { RawKey = key.Raw, Key = key.Str, Value = v });
                    SkipWs();
                    if (_i >= _s.Length)
                    {
                        RecordFailure();
                        return null;
                    }
                    char c = _s[_i];
                    if (c == ',')
                    {
                        _i++;
                        continue;
                    }

                    if (c == '}')
                    {
                        _i++;
                        break;
                    }

                    RecordFailure();
                    return null;
                }

                return new JNode { Kind = JKind.Object, Members = members };
            }

            public void RecordFailure()
            {
                RecordFailure(_i);
            }

            private void RecordFailure(int position)
            {
                if (_errorPosition < 0)
                    _errorPosition = position;
            }

            private static int HexValue(char c) => c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
        }
    }
}

public sealed class EmbeddedConnection : IDisposable
{
    private const int MaximumAttachedDatabases = 10;
    private const char UnqualifiedSchemaMarker = '\0';
    private readonly EmbeddedDatabase _database;
    private readonly Dictionary<string, AttachedDatabase> _attachedDatabases = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<EmbeddedDatabase, TransactionDatabaseState>? _transactionDatabases;
    private EmbeddedDatabase? _transactionWriteDatabase;
    private EmbeddedDatabase? _transactionMutationDatabase;
    private bool _transactionOpenedBySavepoint;
    private readonly List<SavepointEntry> _savepoints = [];
    private long _lastInsertRowId;
    private bool _queryOnly;
    private bool _foreignKeys;
    private bool _recursiveTriggers;
    private int? _pendingPageSize;
    private bool _disposed;

    private sealed class AttachedDatabase : IDisposable
    {
        public AttachedDatabase(
            string path,
            string pathIdentity,
            EmbeddedDatabase database,
            int sequence,
            IDisposable? ownedFileSystem)
        {
            Path = path;
            PathIdentity = pathIdentity;
            Database = database;
            Sequence = sequence;
            OwnedFileSystem = ownedFileSystem;
        }

        public string Path { get; }
        public string PathIdentity { get; }
        public EmbeddedDatabase Database { get; }
        public int Sequence { get; }
        private IDisposable? OwnedFileSystem { get; }

        public void Dispose()
        {
            try
            {
                Database.Dispose();
            }
            finally
            {
                OwnedFileSystem?.Dispose();
            }
        }
    }

    private sealed class TransactionDatabaseState(
        EmbeddedDatabase.SchemaCatalog catalog,
        long version,
        PragmaHeaderMetadata pragmaHeader)
    {
        public EmbeddedDatabase.SchemaCatalog Catalog { get; set; } = catalog;
        public long Version { get; } = version;
        public PragmaHeaderMetadata PragmaHeader { get; set; } = pragmaHeader;
        public bool HasChanges { get; set; }
        public bool HasSnapshotPragmaHeader { get; set; }
    }

    private readonly record struct RoutedStatement(
        EmbeddedDatabase Database,
        ParsedStatement Statement,
        bool IsAttached);

    internal EmbeddedConnection(EmbeddedDatabase database)
    {
        _database = database;
    }

    internal bool HasActiveTransaction => _transactionDatabases is not null;

    internal EmbeddedConnection OpenDatabaseConnection(string databaseName)
        => ResolveDatabase(databaseName).Connect();

    internal (EmbeddedConnection Connection, EmbeddedDatabase? Owner) OpenSnapshotConnection(
        string databaseName)
    {
        var database = ResolveDatabase(databaseName);
        if (!database.IsFileBacked)
            return (database.Connect(), null);

        var snapshot = EmbeddedDatabase.OpenFile(
            database.DatabasePath,
            database.FileSystem,
            readOnly: true);
        try
        {
            database.CopyFunctionAndCollationRegistriesTo(snapshot);
            return (snapshot.Connect(), snapshot);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    internal bool ReferencesSameDatabase(
        string databaseName,
        EmbeddedConnection other,
        string otherDatabaseName)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ResolveDatabase(databaseName).ReferencesSameDatabase(other.ResolveDatabase(otherDatabaseName));
    }

    internal bool CannotProveDistinctSnapshotFiles(
        string databaseName,
        EmbeddedConnection other,
        string otherDatabaseName)
    {
        ArgumentNullException.ThrowIfNull(other);
        var database = ResolveDatabase(databaseName);
        var otherDatabase = other.ResolveDatabase(otherDatabaseName);
        if (!database.IsFileBacked || !otherDatabase.IsFileBacked)
            return false;

        var storageKind = GetSnapshotStorageKind(database.FileSystem);
        var otherStorageKind = GetSnapshotStorageKind(otherDatabase.FileSystem);
        return storageKind == SnapshotStorageKind.Unknown
               || otherStorageKind == SnapshotStorageKind.Unknown;
    }

    private static SnapshotStorageKind GetSnapshotStorageKind(IFileSystem fileSystem)
        => TursoEncryptionFileSystem.Unwrap(fileSystem) switch
        {
            PhysicalFileSystem => SnapshotStorageKind.Physical,
            InMemoryFileSystem => SnapshotStorageKind.InMemory,
            _ => SnapshotStorageKind.Unknown,
        };

    private enum SnapshotStorageKind
    {
        Physical,
        InMemory,
        Unknown,
    }

    public EmbeddedStatement Prepare(string sql)
    {
        ThrowIfDisposed();
        var parameterMap = SqlParameterMap.Parse(sql);
        return new EmbeddedStatement(this, SqlParser.Parse(sql, parameterMap), parameterMap);
    }

    public IReadOnlyList<EmbeddedStatement> PrepareScript(string sql)
    {
        ThrowIfDisposed();
        return SqlScript.Split(sql).Select(Prepare).ToArray();
    }

    public void ResetForPooling()
    {
        ThrowIfDisposed();
        ResetTransactionState();
        _lastInsertRowId = 0;
        _queryOnly = false;
        _foreignKeys = false;
        _recursiveTriggers = false;
        _pendingPageSize = null;
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Dispose();
        _attachedDatabases.Clear();
        _database.RefreshFileCatalogForPooling();
    }

    public void RegisterScalarFunction(string name, int arity, Func<IReadOnlyList<SqlValue>, SqlValue> function)
    {
        ThrowIfDisposed();
        _database.RegisterScalarFunction(name, arity, function);
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Database.RegisterScalarFunction(name, arity, function);
    }

    public bool UnregisterScalarFunction(string name, int arity)
    {
        ThrowIfDisposed();
        var removed = _database.UnregisterScalarFunction(name, arity);
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Database.UnregisterScalarFunction(name, arity);

        return removed;
    }

    public int UnregisterScalarFunctions(string name)
    {
        ThrowIfDisposed();
        var removed = _database.UnregisterScalarFunctions(name);
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Database.UnregisterScalarFunctions(name);

        return removed;
    }

    public void RegisterAggregateFunction(
        string name,
        int arity,
        SqlValue seed,
        Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
        Func<SqlValue, SqlValue> finalize)
    {
        ThrowIfDisposed();
        _database.RegisterAggregateFunction(name, arity, seed, step, finalize);
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Database.RegisterAggregateFunction(name, arity, seed, step, finalize);
    }

    public int UnregisterAggregateFunctions(string name)
    {
        ThrowIfDisposed();
        var removed = _database.UnregisterAggregateFunctions(name);
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Database.UnregisterAggregateFunctions(name);

        return removed;
    }

    public void RegisterCollation(string name, Func<string, string, int> compare)
    {
        ThrowIfDisposed();
        _database.RegisterCollation(name, compare);
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Database.RegisterCollation(name, compare);
    }

    public bool UnregisterCollation(string name)
    {
        ThrowIfDisposed();
        var removed = _database.UnregisterCollation(name);
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Database.UnregisterCollation(name);

        return removed;
    }

    internal IDisposable OpenBlobMutationLease(string databaseName, string tableName, long rowId)
    {
        ThrowIfDisposed();
        return ResolveBlobDatabase(databaseName).OpenBlobMutationLease(tableName, rowId);
    }

    internal long GetBlobMutationGeneration(string databaseName, string tableName, long rowId)
    {
        ThrowIfDisposed();
        return ResolveBlobDatabase(databaseName).GetBlobMutationGeneration(tableName, rowId);
    }

    internal bool HasUpdateTrigger(string databaseName, string tableName)
    {
        ThrowIfDisposed();
        var database = ResolveBlobDatabase(databaseName);
        var catalog = GetTransactionState(database)?.Catalog;
        return catalog is null
            ? database.HasUpdateTrigger(tableName)
            : catalog.Triggers.Values.Any(trigger =>
                trigger.Event == TriggerEvent.Update
                && string.Equals(trigger.TableName, tableName, StringComparison.OrdinalIgnoreCase));
    }

    internal bool HasAttachedDatabases => _attachedDatabases.Count != 0;

    public void Dispose()
    {
        ResetTransactionState();
        foreach (var attachment in _attachedDatabases.Values)
            attachment.Dispose();
        _attachedDatabases.Clear();
        _disposed = true;
    }

    internal ExecutionResult Execute(
        ParsedStatement statement,
        SqlValue[] parameters,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_transactionMutationDatabase is not null
            && statement is BeginStatement
                or CommitStatement
                or RollbackStatement
                or SavepointStatement
                or ReleaseSavepointStatement
                or RollbackToSavepointStatement
                or AttachDatabaseStatement
                or DetachDatabaseStatement)
        {
            throw new EmbeddedSqlException(
                "Managed SQL callbacks cannot change transaction or attachment state during a write.");
        }

        switch (statement)
        {
            case BeginStatement:
                if (_transactionDatabases is not null)
                    throw new EmbeddedSqlException("cannot start a transaction within a transaction");
                BeginTransaction(openedBySavepoint: false);
                return ExecutionResult.Empty;
            case CommitStatement:
                if (_transactionDatabases is null)
                    throw new EmbeddedSqlException("cannot commit - no transaction is active");

                CommitTransaction();
                return ExecutionResult.Empty;
            case RollbackStatement:
                if (_transactionDatabases is null)
                    throw new EmbeddedSqlException("cannot rollback - no transaction is active");

                ResetTransactionState();
                return ExecutionResult.Empty;
            case SavepointStatement savepoint:
                CreateSavepoint(savepoint.Name);
                return ExecutionResult.Empty;
            case ReleaseSavepointStatement release:
                ReleaseSavepoint(release.Name);
                return ExecutionResult.Empty;
            case RollbackToSavepointStatement rollbackTo:
                RollbackToSavepoint(rollbackTo.Name);
                return ExecutionResult.Empty;
            case AttachDatabaseStatement attach:
                return ExecuteWithMutationReservation(
                    _database,
                    () => ExecuteAttach(attach, parameters));
            case DetachDatabaseStatement detach:
                return ExecuteDetach(detach);
            case PragmaDatabaseListStatement:
                return ExecutePragmaDatabaseList();
            case PragmaQueryOnlyStatement queryOnly:
                return ExecutePragmaQueryOnly(queryOnly);
            case PragmaForeignKeysStatement foreignKeys:
                return ExecutePragmaForeignKeys(foreignKeys);
            case PragmaRecursiveTriggersStatement recursiveTriggers:
                return ExecutePragmaRecursiveTriggers(recursiveTriggers);
            case PragmaHeaderIntegerStatement headerInteger:
                return ExecutePragmaHeaderInteger(headerInteger);
            case PragmaJournalModeStatement journalMode:
                return ExecutePragmaJournalMode(journalMode);
            case PragmaPageSizeStatement pageSize:
                return ExecutePragmaPageSize(pageSize);
            case VacuumStatement:
                return ExecuteVacuum();
            default:
                if (_queryOnly && EmbeddedDatabase.MayMutate(statement))
                    throw new EmbeddedSqlException("attempt to write a readonly database");

                var routed = RouteStatement(statement);
                TransactionDatabaseState? transactionState = null;
                EmbeddedDatabase.SchemaCatalog? statementCatalog = null;
                try
                {
                    ExecutionResult result;
                    transactionState = GetTransactionState(routed.Database);
                    var mutationReserved = ReserveTransactionMutation(routed.Database, routed.Statement);
                    try
                    {
                        if (transactionState is null)
                        {
                            result = routed.Database.Execute(
                                routed.Statement,
                                parameters,
                                _lastInsertRowId,
                                _foreignKeys,
                                _recursiveTriggers,
                                cancellationToken);
                        }
                        else
                        {
                            statementCatalog = EmbeddedDatabase.MayMutate(routed.Statement)
                                ? transactionState.Catalog.Clone()
                                : transactionState.Catalog;
                            result = routed.Database.Execute(
                                routed.Statement,
                                parameters,
                                statementCatalog,
                                _lastInsertRowId,
                                _foreignKeys,
                                _recursiveTriggers,
                                cancellationToken);
                            if (EmbeddedDatabase.MayMutate(routed.Statement))
                                cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    finally
                    {
                        if (mutationReserved)
                            ReleaseTransactionMutation(routed.Database);
                    }
                    if (transactionState is not null)
                    {
                        if (result.Changed)
                        {
                            transactionState.Catalog = statementCatalog
                                ?? throw new InvalidOperationException("A transactional mutation lost its statement catalog.");
                            transactionState.HasChanges = true;
                            _transactionWriteDatabase = routed.Database;
                            if (EmbeddedDatabase.MayChangeSchema(routed.Statement))
                            {
                                transactionState.PragmaHeader = transactionState.PragmaHeader with
                                {
                                    SchemaVersion = unchecked(transactionState.PragmaHeader.SchemaVersion + 1),
                                };
                            }
                        }
                    }

                    // last_insert_rowid() tracks the most recent successful INSERT on this
                    // connection; UPDATE/DELETE and zero-row inserts leave it unchanged.
                    if (result.LastInsertRowId is { } insertedRowId)
                        _lastInsertRowId = insertedRowId;

                    return result;
                }
                catch (EmbeddedConflictRollbackException exception)
                {
                    if (_transactionDatabases is not null)
                        ResetTransactionState();

                    throw new EmbeddedSqlException(exception.Message, exception.InnerException ?? exception);
                }
                catch (EmbeddedConflictFailException exception)
                {
                    _lastInsertRowId = exception.LastInsertRowId;
                    if (transactionState is not null)
                    {
                        transactionState.Catalog = statementCatalog
                            ?? throw new InvalidOperationException("A partial transactional mutation lost its statement catalog.");
                        transactionState.HasChanges = true;
                        _transactionWriteDatabase = routed.Database;
                    }

                    throw new EmbeddedSqlException(exception.Message, exception.InnerException ?? exception);
                }
        }
    }

    private ExecutionResult ExecuteAttach(AttachDatabaseStatement statement, SqlValue[] parameters)
    {
        EnsureAutocommitAttachmentLifecycle();
        if (!_database.IsFileBacked)
        {
            throw new EmbeddedSqlException(
                "Managed ATTACH requires a file-backed managed primary database so attachments share its file system.");
        }

        var pathValue = _database.EvaluateConstant(statement.Path, parameters, _lastInsertRowId);
        var requestedPath = EmbeddedDatabase.ToSqlText(pathValue);
        var (path, uriReadOnly) = ResolveAttachmentPath(requestedPath);
        if (string.IsNullOrWhiteSpace(path)
            || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                "Managed ATTACH supports only non-empty file paths; memory databases are not supported.");
        }

        if (statement.Alias.Equals("main", StringComparison.OrdinalIgnoreCase)
            || statement.Alias.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException($"cannot attach database as {statement.Alias}");
        }

        if (_attachedDatabases.ContainsKey(statement.Alias))
            throw new EmbeddedSqlException($"database {statement.Alias} is already in use");
        if (_attachedDatabases.Count >= MaximumAttachedDatabases)
            throw new EmbeddedSqlException($"too many attached databases - maximum {MaximumAttachedDatabases}");

        var pathIdentity = GetAttachmentPathIdentity(path);
        var pathComparer = GetAttachmentPathComparer();
        if (pathComparer.Equals(pathIdentity, GetAttachmentPathIdentity(_database.DatabasePath)))
            throw new EmbeddedSqlException("database file is already open as main");
        if (_attachedDatabases.Values.Any(attachment =>
                pathComparer.Equals(attachment.PathIdentity, pathIdentity)))
        {
            throw new EmbeddedSqlException("database file is already attached");
        }

        var readOnly = _database.IsReadOnly || uriReadOnly;
        IFileSystem attachmentFileSystem = _database.FileSystem;
        TursoEncryptionFileSystem? ownedFileSystem = null;
        if (statement.Key is not null)
        {
            if (_database.FileSystem is not TursoEncryptionFileSystem encryptedFileSystem)
            {
                throw new EmbeddedSqlException(
                    "Managed ATTACH KEY overrides require an encrypted primary database to select the attachment cipher.");
            }

            var keyValue = _database.EvaluateConstant(statement.Key, parameters, _lastInsertRowId);
            var key = EmbeddedDatabase.ToSqlText(keyValue);
            TursoEncryptionOptions encryption;
            try
            {
                encryption = TursoEncryptionOptions.FromHex(encryptedFileSystem.Encryption.Cipher, key);
            }
            catch (ArgumentException exception)
            {
                throw new EmbeddedSqlException(
                    "Managed ATTACH KEY must be a hexadecimal key for the primary database cipher.",
                    exception);
            }
            using (encryption)
            {
                ownedFileSystem = new TursoEncryptionFileSystem(
                    TursoEncryptionFileSystem.Unwrap(_database.FileSystem),
                    encryption);
            }
            attachmentFileSystem = ownedFileSystem;
        }

        EmbeddedDatabase? attached = null;
        try
        {
            attached = EmbeddedDatabase.OpenFile(path, attachmentFileSystem, readOnly);
            _database.CopyFunctionAndCollationRegistriesTo(attached);
            _attachedDatabases.Add(
                statement.Alias,
                new AttachedDatabase(path, pathIdentity, attached, GetNextAttachedDatabaseSequence(), ownedFileSystem));
        }
        catch
        {
            attached?.Dispose();
            ownedFileSystem?.Dispose();
            throw;
        }

        return ExecutionResult.Empty;
    }

    internal void ApplySnapshotPragmaHeader(int schemaVersion, int userVersion, int applicationId)
    {
        var state = GetTransactionState(_database);
        if (state is null)
            throw new InvalidOperationException("Snapshot PRAGMA metadata requires an active transaction.");

        if (_transactionWriteDatabase is not null
            && !ReferenceEquals(_transactionWriteDatabase, _database))
        {
            throw new InvalidOperationException(
                "Snapshot PRAGMA metadata cannot cross the managed transaction write boundary.");
        }

        _transactionWriteDatabase = _database;
        state.PragmaHeader = new PragmaHeaderMetadata(schemaVersion, userVersion, applicationId);
        state.HasChanges = true;
        state.HasSnapshotPragmaHeader = true;
    }

    private EmbeddedDatabase ResolveDatabase(string databaseName)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        if (databaseName.Equals("main", StringComparison.OrdinalIgnoreCase))
            return _database;
        if (_attachedDatabases.TryGetValue(databaseName, out var attachment))
            return attachment.Database;

        throw new EmbeddedSqlException($"no such database: {databaseName}");
    }

    private ExecutionResult ExecuteDetach(DetachDatabaseStatement statement)
    {
        EnsureAutocommitAttachmentLifecycle();
        if (!_attachedDatabases.TryGetValue(statement.Alias, out var attachment))
            throw new EmbeddedSqlException($"no such database: {statement.Alias}");
        if (attachment.Database.HasOpenBlobHandles)
            throw new EmbeddedSqlException("database is locked");

        _attachedDatabases.Remove(statement.Alias);
        attachment.Dispose();
        return ExecutionResult.Empty;
    }

    private int GetNextAttachedDatabaseSequence()
    {
        var used = _attachedDatabases.Values.Select(attachment => attachment.Sequence).ToHashSet();
        for (var sequence = 2; sequence < MaximumAttachedDatabases + 2; sequence++)
        {
            if (!used.Contains(sequence))
                return sequence;
        }

        throw new InvalidOperationException("No managed ATTACH sequence is available below the enforced limit.");
    }

    private ExecutionResult ExecutePragmaDatabaseList()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(0), SqlValue.Text("main"), SqlValue.Text(_database.DatabasePath) },
        };

        foreach (var pair in _attachedDatabases.OrderBy(pair => pair.Value.Sequence))
        {
            var alias = pair.Key;
            var attachment = pair.Value;
            rows.Add(
            new[]
            {
                SqlValue.Integer(attachment.Sequence),
                SqlValue.Text(alias),
                SqlValue.Text(attachment.Path),
            });
        }

        return new ExecutionResult(["seq", "name", "file"], rows.ToArray(), 0);
    }

    private RoutedStatement RouteStatement(ParsedStatement statement)
    {
        return statement switch
        {
            CreateTableStatement create => RouteNamedStatement(create.Name, name => create with { Name = name }),
            DropTableStatement drop => RouteExistingNamedStatement(
                drop.Name,
                ManagedSchemaObjectKind.Table,
                name => drop with { Name = name }),
            CreateIndexStatement createIndex => RouteCreateIndex(createIndex),
            DropIndexStatement dropIndex => RouteExistingNamedStatement(
                dropIndex.Name,
                ManagedSchemaObjectKind.Index,
                name => dropIndex with { Name = name }),
            DropViewStatement dropView => RouteExistingNamedStatement(
                dropView.Name,
                ManagedSchemaObjectKind.View,
                name => dropView with { Name = name }),
            DropTriggerStatement dropTrigger => RouteExistingNamedStatement(
                dropTrigger.Name,
                ManagedSchemaObjectKind.Trigger,
                name => dropTrigger with { Name = name }),
            AlterTableAddColumnStatement addColumn => RouteExistingNamedStatement(
                addColumn.TableName,
                ManagedSchemaObjectKind.Table,
                name => addColumn with { TableName = name }),
            AlterTableRenameStatement rename => RouteExistingNamedStatement(
                rename.TableName,
                ManagedSchemaObjectKind.Table,
                name => rename with { TableName = name }),
            AlterTableRenameColumnStatement renameColumn => RouteExistingNamedStatement(
                renameColumn.TableName,
                ManagedSchemaObjectKind.Table,
                name => renameColumn with { TableName = name }),
            WithDmlStatement with => RouteDataStatement(with),
            InsertStatement insert => RouteDataStatement(insert),
            UpdateStatement update => RouteDataStatement(update),
            DeleteStatement delete => RouteDataStatement(delete),
            QueryStatement query => RouteQuery(query),
            ExplainStatement { Inner: var inner } when ContainsSchemaQualification(inner)
                => throw new EmbeddedSqlException("EXPLAIN for schema-qualified managed ATTACH statements is not supported."),
            ExplainQueryPlanStatement { Inner: var inner } when ContainsSchemaQualification(inner)
                => throw new EmbeddedSqlException(
                    "EXPLAIN QUERY PLAN for schema-qualified managed ATTACH statements is not supported."),
            _ when ContainsSchemaQualification(statement)
                => throw new EmbeddedSqlException("This schema-qualified statement is not supported by managed ATTACH."),
            _ => new RoutedStatement(_database, statement, IsAttached: false),
        };
    }

    private RoutedStatement RouteNamedStatement(string objectName, Func<string, ParsedStatement> rewrite)
    {
        if (!ManagedSchemaName.TrySplit(objectName, out var schema, out var localName))
            return new RoutedStatement(_database, rewrite(objectName), IsAttached: false);

        return RouteSchema(schema, localName, rewrite);
    }

    private RoutedStatement RouteExistingNamedStatement(
        string objectName,
        ManagedSchemaObjectKind kind,
        Func<string, ParsedStatement> rewrite)
    {
        if (ManagedSchemaName.TrySplit(objectName, out var schema, out var localName))
            return RouteSchema(schema, localName, rewrite);

        schema = ResolveExistingObjectSchema(objectName, kind);
        return RouteSchema(schema, objectName, rewrite);
    }

    private string ResolveExistingObjectSchema(string objectName, ManagedSchemaObjectKind kind)
    {
        if (GetTransactionState(_database) is { } mainState
            ? CatalogContainsSchemaObject(mainState.Catalog, objectName, kind)
            : _database.ContainsSchemaObject(objectName, kind))
        {
            return "main";
        }

        foreach (var pair in _attachedDatabases.OrderBy(pair => pair.Value.Sequence))
        {
            var database = pair.Value.Database;
            if (GetTransactionState(database) is { } state
                ? CatalogContainsSchemaObject(state.Catalog, objectName, kind)
                : database.ContainsSchemaObject(objectName, kind))
            {
                return pair.Key;
            }
        }

        return "main";
    }

    private static bool CatalogContainsSchemaObject(
        EmbeddedDatabase.SchemaCatalog catalog,
        string objectName,
        ManagedSchemaObjectKind kind)
        => kind switch
        {
            ManagedSchemaObjectKind.Table => catalog.Tables.ContainsKey(objectName),
            ManagedSchemaObjectKind.View => catalog.Views.ContainsKey(objectName),
            ManagedSchemaObjectKind.Trigger => catalog.Triggers.ContainsKey(objectName),
            ManagedSchemaObjectKind.Index => catalog.Tables.Values.Any(table =>
                table.Indexes.Any(index =>
                    string.Equals(index.Name, objectName, StringComparison.OrdinalIgnoreCase))),
            _ => throw new InvalidOperationException($"Unknown managed schema object kind {kind}."),
        };

    private RoutedStatement RouteCreateIndex(CreateIndexStatement statement)
    {
        var hasIndexSchema = ManagedSchemaName.TrySplit(statement.Name, out var indexSchema, out var indexName);
        var hasTableSchema = ManagedSchemaName.TrySplit(statement.TableName, out var tableSchema, out var tableName);
        if (!hasIndexSchema && !hasTableSchema)
            return new RoutedStatement(_database, statement, IsAttached: false);
        if (hasIndexSchema)
        {
            if (hasTableSchema && !string.Equals(indexSchema, tableSchema, StringComparison.OrdinalIgnoreCase))
                throw new EmbeddedSqlException("CREATE INDEX cannot span managed database schemas.");

            return RouteSchema(
                indexSchema,
                hasTableSchema ? tableName : statement.TableName,
                localTableName => statement with
                {
                    Name = indexName,
                    TableName = localTableName,
                });
        }

        if (!tableSchema.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                "CREATE INDEX on an attached table must qualify the index name with the attached schema.");
        }

        return RouteSchema(
            tableSchema,
            tableName,
            localTableName => statement with
            {
                Name = statement.Name,
                TableName = localTableName,
            });
    }

    private RoutedStatement RouteQuery(QueryStatement query)
    {
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectQuerySchemas(query, schemas, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return RouteForSchemas(query, schemas);
    }

    private RoutedStatement RouteDataStatement(ParsedStatement statement)
    {
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectStatementSchemas(statement, schemas, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return RouteForSchemas(statement, schemas);
    }

    private RoutedStatement RouteForSchemas(ParsedStatement statement, HashSet<string> schemas)
    {
        var resolvedSchemas = schemas
            .Select(ResolveCollectedSchema)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (resolvedSchemas.Count == 0)
            return new RoutedStatement(_database, statement, IsAttached: false);
        if (resolvedSchemas.Count != 1)
        {
            throw new EmbeddedSqlException(
                "Cross-database statements are not supported by managed ATTACH; every persistent table reference must resolve to one database.");
        }

        var schema = resolvedSchemas.Single();
        var rewritten = RewriteStatementSchema(statement, schema);
        if (schema.Equals("main", StringComparison.OrdinalIgnoreCase))
            return new RoutedStatement(_database, rewritten, IsAttached: false);
        if (schema.Equals("temp", StringComparison.OrdinalIgnoreCase))
            throw new EmbeddedSqlException("Managed ATTACH does not implement the temporary database.");
        if (!_attachedDatabases.TryGetValue(schema, out var attachment))
            throw new EmbeddedSqlException($"no such database: {schema}");

        return new RoutedStatement(attachment.Database, rewritten, IsAttached: true);
    }

    private string ResolveCollectedSchema(string schema)
    {
        if (schema.Length == 0 || schema[0] != UnqualifiedSchemaMarker)
            return schema;

        var objectName = schema[1..];
        if (GetTransactionState(_database) is { } mainState
            ? mainState.Catalog.Tables.ContainsKey(objectName) || mainState.Catalog.Views.ContainsKey(objectName)
            : _database.ContainsTableOrView(objectName))
        {
            return "main";
        }

        foreach (var pair in _attachedDatabases.OrderBy(pair => pair.Value.Sequence))
        {
            var attachment = pair.Value;
            if (GetTransactionState(attachment.Database) is { } state
                ? state.Catalog.Tables.ContainsKey(objectName) || state.Catalog.Views.ContainsKey(objectName)
                : attachment.Database.ContainsTableOrView(objectName))
            {
                return pair.Key;
            }
        }

        return "main";
    }

    private static void CollectStatementSchemas(
        ParsedStatement statement,
        ISet<string> schemas,
        HashSet<string> commonTableExpressions)
    {
        switch (statement)
        {
            case InsertStatement insert:
                AddPersistentObjectSchema(insert.TableName, schemas);
                foreach (var expression in insert.Rows.SelectMany(row => row))
                    CollectExpressionSchemas(expression, schemas, commonTableExpressions);
                if (insert.Source is not null)
                    CollectQuerySchemas(insert.Source, schemas, commonTableExpressions);
                if (insert.Upsert is { Action: DoUpdateUpsertAction upsertUpdate })
                {
                    foreach (var assignment in upsertUpdate.Assignments)
                        CollectExpressionSchemas(assignment.Value, schemas, commonTableExpressions);
                }
                CollectProjectionSchemas(insert.Returning, schemas, commonTableExpressions);
                break;
            case UpdateStatement update:
                AddPersistentObjectSchema(update.TableName, schemas);
                foreach (var assignment in update.Assignments)
                    CollectExpressionSchemas(assignment.Value, schemas, commonTableExpressions);
                CollectExpressionSchemas(update.Where, schemas, commonTableExpressions);
                foreach (var orderBy in update.EffectiveOrderBy)
                    CollectExpressionSchemas(orderBy.Expression, schemas, commonTableExpressions);
                CollectExpressionSchemas(update.Limit, schemas, commonTableExpressions);
                CollectExpressionSchemas(update.Offset, schemas, commonTableExpressions);
                CollectProjectionSchemas(update.Returning, schemas, commonTableExpressions);
                break;
            case DeleteStatement delete:
                AddPersistentObjectSchema(delete.TableName, schemas);
                CollectExpressionSchemas(delete.Where, schemas, commonTableExpressions);
                foreach (var orderBy in delete.EffectiveOrderBy)
                    CollectExpressionSchemas(orderBy.Expression, schemas, commonTableExpressions);
                CollectExpressionSchemas(delete.Limit, schemas, commonTableExpressions);
                CollectExpressionSchemas(delete.Offset, schemas, commonTableExpressions);
                CollectProjectionSchemas(delete.Returning, schemas, commonTableExpressions);
                break;
            case WithDmlStatement with:
                var names = new HashSet<string>(commonTableExpressions, StringComparer.OrdinalIgnoreCase);
                foreach (var commonTableExpression in with.CommonTableExpressions)
                {
                    names.Add(commonTableExpression.Name);
                    CollectQuerySchemas(commonTableExpression.Query, schemas, names);
                }
                CollectStatementSchemas(with.Dml, schemas, names);
                break;
            default:
                throw new InvalidOperationException($"Cannot route data statement {statement.GetType().Name}.");
        }
    }

    private static void CollectQuerySchemas(
        QueryStatement query,
        ISet<string> schemas,
        HashSet<string> commonTableExpressions)
    {
        switch (query)
        {
            case SelectStatement select:
                CollectSourceSchemas(select.Source, schemas, commonTableExpressions);
                CollectProjectionSchemas(select.Projections, schemas, commonTableExpressions);
                CollectExpressionSchemas(select.Where, schemas, commonTableExpressions);
                foreach (var expression in select.GroupBy)
                    CollectExpressionSchemas(expression, schemas, commonTableExpressions);
                CollectExpressionSchemas(select.Having, schemas, commonTableExpressions);
                foreach (var orderBy in select.OrderBy)
                    CollectExpressionSchemas(orderBy.Expression, schemas, commonTableExpressions);
                CollectExpressionSchemas(select.Limit, schemas, commonTableExpressions);
                CollectExpressionSchemas(select.Offset, schemas, commonTableExpressions);
                break;
            case ValuesClause values:
                foreach (var expression in values.Rows.SelectMany(row => row))
                    CollectExpressionSchemas(expression, schemas, commonTableExpressions);
                break;
            case CompoundSelectStatement compound:
                foreach (var term in compound.Terms)
                    CollectQuerySchemas(term, schemas, commonTableExpressions);
                foreach (var orderBy in compound.OrderBy)
                    CollectExpressionSchemas(orderBy.Expression, schemas, commonTableExpressions);
                CollectExpressionSchemas(compound.Limit, schemas, commonTableExpressions);
                CollectExpressionSchemas(compound.Offset, schemas, commonTableExpressions);
                break;
            case WithSelectStatement with:
                var names = new HashSet<string>(commonTableExpressions, StringComparer.OrdinalIgnoreCase);
                foreach (var commonTableExpression in with.CommonTableExpressions)
                {
                    names.Add(commonTableExpression.Name);
                    CollectQuerySchemas(commonTableExpression.Query, schemas, names);
                }
                CollectQuerySchemas(with.Query, schemas, names);
                break;
            default:
                throw new InvalidOperationException($"Cannot route query {query.GetType().Name}.");
        }
    }

    private static void CollectSourceSchemas(
        TableSource? source,
        ISet<string> schemas,
        HashSet<string> commonTableExpressions)
    {
        switch (source)
        {
            case null:
                break;
            case NamedTableSource named:
                if (ManagedSchemaName.TrySplit(named.Name, out var schema, out _))
                    schemas.Add(schema);
                else if (!commonTableExpressions.Contains(named.Name))
                    schemas.Add(UnqualifiedSchemaMarker + named.Name);
                break;
            case DerivedTableSource derived:
                CollectQuerySchemas(derived.Query, schemas, commonTableExpressions);
                break;
            case JoinTableSource join:
                CollectSourceSchemas(join.Left, schemas, commonTableExpressions);
                CollectSourceSchemas(join.Right, schemas, commonTableExpressions);
                CollectExpressionSchemas(join.Condition, schemas, commonTableExpressions);
                break;
            case GenerateSeriesSource generateSeries:
                CollectExpressionSchemas(generateSeries.Start, schemas, commonTableExpressions);
                CollectExpressionSchemas(generateSeries.Stop, schemas, commonTableExpressions);
                CollectExpressionSchemas(generateSeries.Step, schemas, commonTableExpressions);
                break;
        }
    }

    private static void CollectProjectionSchemas(
        IReadOnlyList<Projection>? projections,
        ISet<string> schemas,
        HashSet<string> commonTableExpressions)
    {
        if (projections is null)
            return;
        foreach (var projection in projections)
            CollectExpressionSchemas(projection.Expression, schemas, commonTableExpressions);
    }

    private static void CollectExpressionSchemas(
        Expression? expression,
        ISet<string> schemas,
        HashSet<string> commonTableExpressions)
    {
        switch (expression)
        {
            case null:
            case LiteralExpression:
            case ParameterExpression:
            case ColumnExpression:
            case StarExpression:
            case QualifiedStarExpression:
                return;
            case ScalarSubqueryExpression scalarSubquery:
                CollectQuerySchemas(scalarSubquery.Query, schemas, commonTableExpressions);
                return;
            case ExistsExpression exists:
                CollectQuerySchemas(exists.Query, schemas, commonTableExpressions);
                return;
            case InSubqueryExpression inSubquery:
                CollectExpressionSchemas(inSubquery.Value, schemas, commonTableExpressions);
                CollectQuerySchemas(inSubquery.Query, schemas, commonTableExpressions);
                return;
            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                    CollectExpressionSchemas(argument, schemas, commonTableExpressions);
                CollectExpressionSchemas(function.Filter, schemas, commonTableExpressions);
                CollectWindowSchemas(function.Window, schemas, commonTableExpressions);
                return;
            case CollationExpression collation:
                CollectExpressionSchemas(collation.Expression, schemas, commonTableExpressions);
                return;
            case CastExpression cast:
                CollectExpressionSchemas(cast.Expression, schemas, commonTableExpressions);
                return;
            case CaseExpression @case:
                CollectExpressionSchemas(@case.Operand, schemas, commonTableExpressions);
                foreach (var clause in @case.Clauses)
                {
                    CollectExpressionSchemas(clause.When, schemas, commonTableExpressions);
                    CollectExpressionSchemas(clause.Then, schemas, commonTableExpressions);
                }
                CollectExpressionSchemas(@case.Else, schemas, commonTableExpressions);
                return;
            case LikeExpression like:
                CollectExpressionSchemas(like.Value, schemas, commonTableExpressions);
                CollectExpressionSchemas(like.Pattern, schemas, commonTableExpressions);
                CollectExpressionSchemas(like.Escape, schemas, commonTableExpressions);
                return;
            case GlobExpression glob:
                CollectExpressionSchemas(glob.Value, schemas, commonTableExpressions);
                CollectExpressionSchemas(glob.Pattern, schemas, commonTableExpressions);
                return;
            case InExpression @in:
                CollectExpressionSchemas(@in.Value, schemas, commonTableExpressions);
                foreach (var value in @in.Values)
                    CollectExpressionSchemas(value, schemas, commonTableExpressions);
                return;
            case BetweenExpression between:
                CollectExpressionSchemas(between.Value, schemas, commonTableExpressions);
                CollectExpressionSchemas(between.Lower, schemas, commonTableExpressions);
                CollectExpressionSchemas(between.Upper, schemas, commonTableExpressions);
                return;
            case UnaryExpression unary:
                CollectExpressionSchemas(unary.Operand, schemas, commonTableExpressions);
                return;
            case BinaryExpression binary:
                CollectExpressionSchemas(binary.Left, schemas, commonTableExpressions);
                CollectExpressionSchemas(binary.Right, schemas, commonTableExpressions);
                return;
            default:
                throw new InvalidOperationException($"Cannot route expression {expression.GetType().Name}.");
        }
    }

    private static void CollectWindowSchemas(
        WindowSpecification? window,
        ISet<string> schemas,
        HashSet<string> commonTableExpressions)
    {
        if (window is null)
            return;
        foreach (var expression in window.PartitionBy)
            CollectExpressionSchemas(expression, schemas, commonTableExpressions);
        foreach (var orderBy in window.OrderBy)
            CollectExpressionSchemas(orderBy.Expression, schemas, commonTableExpressions);
        CollectExpressionSchemas(window.Frame?.Start.Offset, schemas, commonTableExpressions);
        CollectExpressionSchemas(window.Frame?.End.Offset, schemas, commonTableExpressions);
    }

    private static void AddPersistentObjectSchema(string name, ISet<string> schemas)
    {
        schemas.Add(
            ManagedSchemaName.TrySplit(name, out var schema, out _)
                ? schema
                : UnqualifiedSchemaMarker + name);
    }

    private static ParsedStatement RewriteStatementSchema(ParsedStatement statement, string schema)
    {
        return RewriteStatementSchema(
            statement,
            schema,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static ParsedStatement RewriteStatementSchema(
        ParsedStatement statement,
        string schema,
        HashSet<string> commonTableExpressions)
    {
        return statement switch
        {
            InsertStatement insert => insert with
            {
                TableName = RewritePersistentObjectName(insert.TableName, schema),
                Rows = insert.Rows.Select(row => row.Select(expression =>
                    RewriteExpressionSchema(expression, schema, commonTableExpressions)).ToArray()).ToArray(),
                Source = insert.Source is null
                    ? null
                    : RewriteQuerySchema(insert.Source, schema, commonTableExpressions),
                Returning = RewriteProjections(insert.Returning, schema, commonTableExpressions),
                Upsert = insert.Upsert is { Action: DoUpdateUpsertAction update } upsert
                    ? upsert with
                    {
                        Action = update with
                        {
                            Assignments = update.Assignments.Select(assignment => assignment with
                            {
                                Value = RewriteExpressionSchema(
                                    assignment.Value,
                                    schema,
                                    commonTableExpressions),
                            }).ToArray(),
                        },
                    }
                    : insert.Upsert,
            },
            UpdateStatement update => update with
            {
                TableName = RewritePersistentObjectName(update.TableName, schema),
                Assignments = update.Assignments.Select(assignment => assignment with
                {
                    Value = RewriteExpressionSchema(assignment.Value, schema, commonTableExpressions),
                }).ToArray(),
                Where = RewriteNullableExpression(update.Where, schema, commonTableExpressions),
                OrderBy = RewriteOrderBy(update.EffectiveOrderBy, schema, commonTableExpressions),
                Limit = RewriteNullableExpression(update.Limit, schema, commonTableExpressions),
                Offset = RewriteNullableExpression(update.Offset, schema, commonTableExpressions),
                Returning = RewriteProjections(update.Returning, schema, commonTableExpressions),
            },
            DeleteStatement delete => delete with
            {
                TableName = RewritePersistentObjectName(delete.TableName, schema),
                Where = RewriteNullableExpression(delete.Where, schema, commonTableExpressions),
                OrderBy = RewriteOrderBy(delete.EffectiveOrderBy, schema, commonTableExpressions),
                Limit = RewriteNullableExpression(delete.Limit, schema, commonTableExpressions),
                Offset = RewriteNullableExpression(delete.Offset, schema, commonTableExpressions),
                Returning = RewriteProjections(delete.Returning, schema, commonTableExpressions),
            },
            WithDmlStatement with => RewriteWithDmlSchema(with, schema, commonTableExpressions),
            QueryStatement query => RewriteQuerySchema(query, schema, commonTableExpressions),
            _ => statement,
        };
    }

    private static WithDmlStatement RewriteWithDmlSchema(
        WithDmlStatement statement,
        string schema,
        HashSet<string> commonTableExpressions)
    {
        var names = new HashSet<string>(commonTableExpressions, StringComparer.OrdinalIgnoreCase);
        var rewritten = new List<CommonTableExpression>();
        foreach (var commonTableExpression in statement.CommonTableExpressions)
        {
            names.Add(commonTableExpression.Name);
            rewritten.Add(commonTableExpression with
            {
                Query = RewriteQuerySchema(commonTableExpression.Query, schema, names),
            });
        }

        return statement with
        {
            CommonTableExpressions = rewritten,
            Dml = RewriteStatementSchema(statement.Dml, schema, names),
        };
    }

    private static QueryStatement RewriteQuerySchema(
        QueryStatement query,
        string schema,
        HashSet<string> commonTableExpressions)
    {
        return query switch
        {
            SelectStatement select => select with
            {
                Projections = RewriteProjections(select.Projections, schema, commonTableExpressions)!,
                Source = RewriteSourceSchema(select.Source, schema, commonTableExpressions),
                Where = RewriteNullableExpression(select.Where, schema, commonTableExpressions),
                GroupBy = select.GroupBy.Select(expression =>
                    RewriteExpressionSchema(expression, schema, commonTableExpressions)).ToArray(),
                Having = RewriteNullableExpression(select.Having, schema, commonTableExpressions),
                OrderBy = RewriteOrderBy(select.OrderBy, schema, commonTableExpressions),
                Limit = RewriteNullableExpression(select.Limit, schema, commonTableExpressions),
                Offset = RewriteNullableExpression(select.Offset, schema, commonTableExpressions),
            },
            ValuesClause values => values with
            {
                Rows = values.Rows.Select(row => row.Select(expression =>
                    RewriteExpressionSchema(expression, schema, commonTableExpressions)).ToArray()).ToArray(),
            },
            CompoundSelectStatement compound => compound with
            {
                Terms = compound.Terms.Select(term =>
                    RewriteQuerySchema(term, schema, commonTableExpressions)).ToArray(),
                OrderBy = RewriteOrderBy(compound.OrderBy, schema, commonTableExpressions),
                Limit = RewriteNullableExpression(compound.Limit, schema, commonTableExpressions),
                Offset = RewriteNullableExpression(compound.Offset, schema, commonTableExpressions),
            },
            WithSelectStatement with => RewriteWithSelectSchema(with, schema, commonTableExpressions),
            _ => throw new InvalidOperationException($"Cannot rewrite query {query.GetType().Name}."),
        };
    }

    private static WithSelectStatement RewriteWithSelectSchema(
        WithSelectStatement statement,
        string schema,
        HashSet<string> commonTableExpressions)
    {
        var names = new HashSet<string>(commonTableExpressions, StringComparer.OrdinalIgnoreCase);
        var rewritten = new List<CommonTableExpression>();
        foreach (var commonTableExpression in statement.CommonTableExpressions)
        {
            names.Add(commonTableExpression.Name);
            rewritten.Add(commonTableExpression with
            {
                Query = RewriteQuerySchema(commonTableExpression.Query, schema, names),
            });
        }

        return statement with
        {
            CommonTableExpressions = rewritten,
            Query = RewriteQuerySchema(statement.Query, schema, names),
        };
    }

    private static TableSource? RewriteSourceSchema(
        TableSource? source,
        string schema,
        HashSet<string> commonTableExpressions)
    {
        return source switch
        {
            null => null,
            NamedTableSource named => commonTableExpressions.Contains(named.Name)
                ? named
                : named with { Name = RewritePersistentObjectName(named.Name, schema) },
            DerivedTableSource derived => derived with
            {
                Query = RewriteQuerySchema(derived.Query, schema, commonTableExpressions),
            },
            JoinTableSource join => join with
            {
                Left = RewriteSourceSchema(join.Left, schema, commonTableExpressions)!,
                Right = RewriteSourceSchema(join.Right, schema, commonTableExpressions)!,
                Condition = RewriteNullableExpression(join.Condition, schema, commonTableExpressions),
            },
            GenerateSeriesSource generateSeries => generateSeries with
            {
                Start = RewriteExpressionSchema(generateSeries.Start, schema, commonTableExpressions),
                Stop = RewriteExpressionSchema(generateSeries.Stop, schema, commonTableExpressions),
                Step = RewriteExpressionSchema(generateSeries.Step, schema, commonTableExpressions),
            },
            _ => throw new InvalidOperationException($"Cannot rewrite source {source.GetType().Name}."),
        };
    }

    private static IReadOnlyList<Projection>? RewriteProjections(
        IReadOnlyList<Projection>? projections,
        string schema,
        HashSet<string> commonTableExpressions)
        => projections?.Select(projection => projection with
        {
            Expression = RewriteExpressionSchema(projection.Expression, schema, commonTableExpressions),
        }).ToArray();

    private static IReadOnlyList<OrderByTerm> RewriteOrderBy(
        IReadOnlyList<OrderByTerm> orderBy,
        string schema,
        HashSet<string> commonTableExpressions)
        => orderBy.Select(term => term with
        {
            Expression = RewriteExpressionSchema(term.Expression, schema, commonTableExpressions),
        }).ToArray();

    private static Expression? RewriteNullableExpression(
        Expression? expression,
        string schema,
        HashSet<string> commonTableExpressions)
        => expression is null ? null : RewriteExpressionSchema(expression, schema, commonTableExpressions);

    private static Expression RewriteExpressionSchema(
        Expression expression,
        string schema,
        HashSet<string> commonTableExpressions)
    {
        return expression switch
        {
            ScalarSubqueryExpression scalarSubquery => scalarSubquery with
            {
                Query = RewriteQuerySchema(scalarSubquery.Query, schema, commonTableExpressions),
            },
            ExistsExpression exists => exists with
            {
                Query = RewriteQuerySchema(exists.Query, schema, commonTableExpressions),
            },
            InSubqueryExpression inSubquery => inSubquery with
            {
                Value = RewriteExpressionSchema(inSubquery.Value, schema, commonTableExpressions),
                Query = RewriteQuerySchema(inSubquery.Query, schema, commonTableExpressions),
            },
            FunctionExpression function => function with
            {
                Arguments = function.Arguments.Select(argument =>
                    RewriteExpressionSchema(argument, schema, commonTableExpressions)).ToArray(),
                Filter = RewriteNullableExpression(function.Filter, schema, commonTableExpressions),
                Window = RewriteWindowSchema(function.Window, schema, commonTableExpressions),
            },
            CollationExpression collation => collation with
            {
                Expression = RewriteExpressionSchema(collation.Expression, schema, commonTableExpressions),
            },
            CastExpression cast => cast with
            {
                Expression = RewriteExpressionSchema(cast.Expression, schema, commonTableExpressions),
            },
            CaseExpression @case => @case with
            {
                Operand = RewriteNullableExpression(@case.Operand, schema, commonTableExpressions),
                Clauses = @case.Clauses.Select(clause => clause with
                {
                    When = RewriteExpressionSchema(clause.When, schema, commonTableExpressions),
                    Then = RewriteExpressionSchema(clause.Then, schema, commonTableExpressions),
                }).ToArray(),
                Else = RewriteNullableExpression(@case.Else, schema, commonTableExpressions),
            },
            LikeExpression like => like with
            {
                Value = RewriteExpressionSchema(like.Value, schema, commonTableExpressions),
                Pattern = RewriteExpressionSchema(like.Pattern, schema, commonTableExpressions),
                Escape = RewriteNullableExpression(like.Escape, schema, commonTableExpressions),
            },
            GlobExpression glob => glob with
            {
                Value = RewriteExpressionSchema(glob.Value, schema, commonTableExpressions),
                Pattern = RewriteExpressionSchema(glob.Pattern, schema, commonTableExpressions),
            },
            InExpression @in => @in with
            {
                Value = RewriteExpressionSchema(@in.Value, schema, commonTableExpressions),
                Values = @in.Values.Select(value =>
                    RewriteExpressionSchema(value, schema, commonTableExpressions)).ToArray(),
            },
            BetweenExpression between => between with
            {
                Value = RewriteExpressionSchema(between.Value, schema, commonTableExpressions),
                Lower = RewriteExpressionSchema(between.Lower, schema, commonTableExpressions),
                Upper = RewriteExpressionSchema(between.Upper, schema, commonTableExpressions),
            },
            UnaryExpression unary => unary with
            {
                Operand = RewriteExpressionSchema(unary.Operand, schema, commonTableExpressions),
            },
            BinaryExpression binary => binary with
            {
                Left = RewriteExpressionSchema(binary.Left, schema, commonTableExpressions),
                Right = RewriteExpressionSchema(binary.Right, schema, commonTableExpressions),
            },
            _ => expression,
        };
    }

    private static WindowSpecification? RewriteWindowSchema(
        WindowSpecification? window,
        string schema,
        HashSet<string> commonTableExpressions)
    {
        if (window is null)
            return null;

        return window with
        {
            PartitionBy = window.PartitionBy.Select(expression =>
                RewriteExpressionSchema(expression, schema, commonTableExpressions)).ToArray(),
            OrderBy = RewriteOrderBy(window.OrderBy, schema, commonTableExpressions),
            Frame = window.Frame is null
                ? null
                : window.Frame with
                {
                    Start = window.Frame.Start with
                    {
                        Offset = RewriteNullableExpression(
                            window.Frame.Start.Offset,
                            schema,
                            commonTableExpressions),
                    },
                    End = window.Frame.End with
                    {
                        Offset = RewriteNullableExpression(
                            window.Frame.End.Offset,
                            schema,
                            commonTableExpressions),
                    },
                },
        };
    }

    private static string RewritePersistentObjectName(string name, string schema)
    {
        if (!ManagedSchemaName.TrySplit(name, out var objectSchema, out var localName))
            return name;
        if (!objectSchema.Equals(schema, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A cross-database object escaped managed ATTACH routing.");

        return localName;
    }

    private RoutedStatement RouteSchema(string schema, string localName, Func<string, ParsedStatement> rewrite)
    {
        if (schema.Equals("main", StringComparison.OrdinalIgnoreCase))
            return new RoutedStatement(_database, rewrite(localName), IsAttached: false);
        if (!_attachedDatabases.TryGetValue(schema, out var attachment))
            throw new EmbeddedSqlException($"no such database: {schema}");

        return new RoutedStatement(attachment.Database, rewrite(localName), IsAttached: true);
    }

    private EmbeddedDatabase ResolveBlobDatabase(string databaseName)
    {
        if (databaseName.Equals("main", StringComparison.OrdinalIgnoreCase))
            return _database;
        if (_attachedDatabases.TryGetValue(databaseName, out var attachment))
            return attachment.Database;

        throw new EmbeddedSqlException($"no such database: {databaseName}");
    }

    private static bool ContainsSchemaQualification(ParsedStatement statement)
    {
        return statement switch
        {
            CreateTableStatement create => ManagedSchemaName.TrySplit(create.Name, out _, out _),
            DropTableStatement drop => ManagedSchemaName.TrySplit(drop.Name, out _, out _),
            CreateIndexStatement createIndex => ManagedSchemaName.TrySplit(createIndex.Name, out _, out _)
                || ManagedSchemaName.TrySplit(createIndex.TableName, out _, out _),
            DropIndexStatement dropIndex => ManagedSchemaName.TrySplit(dropIndex.Name, out _, out _),
            CreateViewStatement createView => ManagedSchemaName.TrySplit(createView.Name, out _, out _)
                || QueryContainsSchemaQualification(createView.Query),
            DropViewStatement dropView => ManagedSchemaName.TrySplit(dropView.Name, out _, out _),
            CreateTriggerStatement createTrigger => ManagedSchemaName.TrySplit(createTrigger.Name, out _, out _)
                || ManagedSchemaName.TrySplit(createTrigger.TableName, out _, out _)
                || createTrigger.Body.Any(ContainsSchemaQualification),
            DropTriggerStatement dropTrigger => ManagedSchemaName.TrySplit(dropTrigger.Name, out _, out _),
            AlterTableAddColumnStatement addColumn => ManagedSchemaName.TrySplit(addColumn.TableName, out _, out _),
            AlterTableRenameStatement rename => ManagedSchemaName.TrySplit(rename.TableName, out _, out _),
            AlterTableRenameColumnStatement renameColumn => ManagedSchemaName.TrySplit(renameColumn.TableName, out _, out _),
            InsertStatement insert => ManagedSchemaName.TrySplit(insert.TableName, out _, out _)
                || insert.Rows.SelectMany(row => row).Any(ExpressionContainsSchemaQualification)
                || (insert.Source is not null && QueryContainsSchemaQualification(insert.Source))
                || (insert.Upsert is { Action: DoUpdateUpsertAction update }
                    && (update.Assignments.Any(assignment => ExpressionContainsSchemaQualification(assignment.Value))
                        || ExpressionContainsSchemaQualification(update.Where)))
                || (insert.Returning?.Any(projection => ExpressionContainsSchemaQualification(projection.Expression)) ?? false),
            UpdateStatement update => ManagedSchemaName.TrySplit(update.TableName, out _, out _)
                || update.Assignments.Any(assignment => ExpressionContainsSchemaQualification(assignment.Value))
                || ExpressionContainsSchemaQualification(update.Where)
                || update.EffectiveOrderBy.Any(term => ExpressionContainsSchemaQualification(term.Expression))
                || ExpressionContainsSchemaQualification(update.Limit)
                || ExpressionContainsSchemaQualification(update.Offset)
                || (update.Returning?.Any(projection => ExpressionContainsSchemaQualification(projection.Expression)) ?? false),
            DeleteStatement delete => ManagedSchemaName.TrySplit(delete.TableName, out _, out _)
                || ExpressionContainsSchemaQualification(delete.Where)
                || delete.EffectiveOrderBy.Any(term => ExpressionContainsSchemaQualification(term.Expression))
                || ExpressionContainsSchemaQualification(delete.Limit)
                || ExpressionContainsSchemaQualification(delete.Offset)
                || (delete.Returning?.Any(projection => ExpressionContainsSchemaQualification(projection.Expression)) ?? false),
            WithDmlStatement with => with.CommonTableExpressions.Any(commonTableExpression =>
                    ManagedSchemaName.TrySplit(commonTableExpression.Name, out _, out _)
                    || QueryContainsSchemaQualification(commonTableExpression.Query))
                || ContainsSchemaQualification(with.Dml),
            QueryStatement query => QueryContainsSchemaQualification(query),
            ExplainStatement explain => ContainsSchemaQualification(explain.Inner),
            ExplainQueryPlanStatement explainQueryPlan => ContainsSchemaQualification(explainQueryPlan.Inner),
            _ => false,
        };
    }

    private static bool QueryContainsSchemaQualification(QueryStatement query)
    {
        return query switch
        {
            SelectStatement select => SourceContainsSchemaQualification(select.Source)
                || select.Projections.Any(projection => ExpressionContainsSchemaQualification(projection.Expression))
                || ExpressionContainsSchemaQualification(select.Where)
                || select.GroupBy.Any(ExpressionContainsSchemaQualification)
                || ExpressionContainsSchemaQualification(select.Having)
                || select.OrderBy.Any(orderBy => ExpressionContainsSchemaQualification(orderBy.Expression))
                || ExpressionContainsSchemaQualification(select.Limit)
                || ExpressionContainsSchemaQualification(select.Offset),
            ValuesClause values => values.Rows.SelectMany(row => row).Any(ExpressionContainsSchemaQualification),
            CompoundSelectStatement compound => compound.Terms.Any(QueryContainsSchemaQualification)
                || compound.OrderBy.Any(orderBy => ExpressionContainsSchemaQualification(orderBy.Expression))
                || ExpressionContainsSchemaQualification(compound.Limit)
                || ExpressionContainsSchemaQualification(compound.Offset),
            WithSelectStatement with => with.CommonTableExpressions.Any(commonTableExpression =>
                    QueryContainsSchemaQualification(commonTableExpression.Query))
                || QueryContainsSchemaQualification(with.Query),
            _ => false,
        };
    }

    private static bool SourceContainsSchemaQualification(TableSource? source)
    {
        return source switch
        {
            null => false,
            NamedTableSource named => ManagedSchemaName.TrySplit(named.Name, out _, out _),
            DerivedTableSource derived => QueryContainsSchemaQualification(derived.Query),
            JoinTableSource join => SourceContainsSchemaQualification(join.Left)
                || SourceContainsSchemaQualification(join.Right)
                || ExpressionContainsSchemaQualification(join.Condition),
            GenerateSeriesSource generateSeries => ExpressionContainsSchemaQualification(generateSeries.Start)
                || ExpressionContainsSchemaQualification(generateSeries.Stop)
                || ExpressionContainsSchemaQualification(generateSeries.Step),
            _ => false,
        };
    }

    private static bool ExpressionContainsSchemaQualification(Expression? expression)
    {
        return expression switch
        {
            null => false,
            ScalarSubqueryExpression scalarSubquery => QueryContainsSchemaQualification(scalarSubquery.Query),
            ExistsExpression exists => QueryContainsSchemaQualification(exists.Query),
            InSubqueryExpression inSubquery => QueryContainsSchemaQualification(inSubquery.Query)
                || ExpressionContainsSchemaQualification(inSubquery.Value),
            FunctionExpression function => function.Arguments.Any(ExpressionContainsSchemaQualification)
                || ExpressionContainsSchemaQualification(function.Filter)
                || WindowContainsSchemaQualification(function.Window),
            CollationExpression collation => ExpressionContainsSchemaQualification(collation.Expression),
            CastExpression cast => ExpressionContainsSchemaQualification(cast.Expression),
            CaseExpression @case => ExpressionContainsSchemaQualification(@case.Operand)
                || @case.Clauses.Any(clause => ExpressionContainsSchemaQualification(clause.When)
                    || ExpressionContainsSchemaQualification(clause.Then))
                || ExpressionContainsSchemaQualification(@case.Else),
            LikeExpression like => ExpressionContainsSchemaQualification(like.Value)
                || ExpressionContainsSchemaQualification(like.Pattern)
                || ExpressionContainsSchemaQualification(like.Escape),
            InExpression @in => ExpressionContainsSchemaQualification(@in.Value)
                || @in.Values.Any(ExpressionContainsSchemaQualification),
            BetweenExpression between => ExpressionContainsSchemaQualification(between.Value)
                || ExpressionContainsSchemaQualification(between.Lower)
                || ExpressionContainsSchemaQualification(between.Upper),
            UnaryExpression unary => ExpressionContainsSchemaQualification(unary.Operand),
            GlobExpression glob => ExpressionContainsSchemaQualification(glob.Value)
                || ExpressionContainsSchemaQualification(glob.Pattern),
            BinaryExpression binary => ExpressionContainsSchemaQualification(binary.Left)
                || ExpressionContainsSchemaQualification(binary.Right),
            _ => false,
        };
    }

    private static bool WindowContainsSchemaQualification(WindowSpecification? window)
    {
        if (window is null)
            return false;

        return window.PartitionBy.Any(ExpressionContainsSchemaQualification)
            || window.OrderBy.Any(orderBy => ExpressionContainsSchemaQualification(orderBy.Expression))
            || ExpressionContainsSchemaQualification(window.Frame?.Start.Offset)
            || ExpressionContainsSchemaQualification(window.Frame?.End.Offset);
    }

    private (string Path, bool ReadOnly) ResolveAttachmentPath(string requestedPath)
    {
        if (!requestedPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return (requestedPath, false);

        var queryStart = requestedPath.IndexOf('?', StringComparison.Ordinal);
        var escapedPath = queryStart < 0 ? requestedPath[5..] : requestedPath[5..queryStart];
        string path;
        try
        {
            if (escapedPath.StartsWith("//", StringComparison.Ordinal)
                && Uri.TryCreate(requestedPath, UriKind.Absolute, out var absoluteUri)
                && absoluteUri.IsFile)
            {
                path = absoluteUri.LocalPath;
            }
            else
            {
                path = Uri.UnescapeDataString(escapedPath);
            }
        }
        catch (UriFormatException exception)
        {
            throw new EmbeddedSqlException($"Invalid managed ATTACH URI path '{requestedPath}'.", exception);
        }

        var mode = "rwc";
        var query = queryStart < 0 ? string.Empty : requestedPath[(queryStart + 1)..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var name = Uri.UnescapeDataString(pieces[0]);
            if (!name.Equals("mode", StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"Managed ATTACH URI option '{name}' is not supported.");
            }

            mode = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }

        var readOnly = mode.Equals("ro", StringComparison.OrdinalIgnoreCase);
        var requireExisting = readOnly || mode.Equals("rw", StringComparison.OrdinalIgnoreCase);
        if (!readOnly
            && !mode.Equals("rw", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("rwc", StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException($"Managed ATTACH URI has unsupported access mode '{mode}'.");
        }
        if (requireExisting && (!_database.FileSystem.FileExists(path) || !_database.FileSystem.FileExists(path + "-wal")))
            throw new EmbeddedSqlException($"unable to open database file: {path}");

        return (path, readOnly);
    }

    private static string GetAttachmentPathIdentity(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new EmbeddedSqlException($"Invalid managed ATTACH file path '{path}'.", exception);
        }
    }

    private StringComparer GetAttachmentPathComparer()
    {
        var fileSystem = TursoEncryptionFileSystem.Unwrap(_database.FileSystem);
        return fileSystem is PhysicalFileSystem
            ? EmbeddedDatabase.PhysicalPathComparer
            : StringComparer.Ordinal;
    }

    private void EnsureAutocommitAttachmentLifecycle()
    {
        if (_transactionDatabases is not null)
            throw new EmbeddedSqlException("Managed ATTACH and DETACH are not supported inside a transaction.");
    }

    private TransactionDatabaseState? GetTransactionState(EmbeddedDatabase database)
    {
        if (_transactionDatabases is null)
            return null;
        if (!_transactionDatabases.TryGetValue(database, out var state))
            throw new InvalidOperationException("The managed transaction does not own the routed database.");

        return state;
    }

    private void BeginTransaction(bool openedBySavepoint)
    {
        var databases = _attachedDatabases.Values
            .OrderBy(attachment => attachment.PathIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(attachment => attachment.Database)
            .Prepend(_database);
        var states = new Dictionary<EmbeddedDatabase, TransactionDatabaseState>();
        try
        {
            foreach (var database in databases)
            {
                var snapshot = database.CreateTransactionSnapshot();
                states.Add(database, new TransactionDatabaseState(
                    snapshot.Catalog,
                    snapshot.Version,
                    snapshot.PragmaHeader));
            }
        }
        catch
        {
            foreach (var database in states.Keys)
                database.EndTransaction();
            throw;
        }

        _transactionDatabases = states;
        _transactionWriteDatabase = null;
        _transactionMutationDatabase = null;
        _transactionOpenedBySavepoint = openedBySavepoint;
        _savepoints.Clear();
    }

    private void EnsureTransactionMayMutate(EmbeddedDatabase database, ParsedStatement statement)
    {
        if (!EmbeddedDatabase.MayMutate(statement))
            return;
        if (database.IsReadOnly)
            throw new EmbeddedSqlException("attempt to write a readonly database");
        if (_transactionMutationDatabase is not null)
        {
            throw new EmbeddedSqlException(
                "Managed connections do not support reentrant writes from SQL callbacks.");
        }
        if (_transactionWriteDatabase is null
            || ReferenceEquals(_transactionWriteDatabase, database))
        {
            return;
        }

        throw new EmbeddedSqlException(
            "Managed ATTACH transactions cannot modify more than one database because independent WAL files cannot be committed atomically.");
    }

    private bool ReserveTransactionMutation(EmbeddedDatabase database, ParsedStatement statement)
    {
        EnsureTransactionMayMutate(database, statement);
        if (!EmbeddedDatabase.MayMutate(statement))
            return false;

        _transactionMutationDatabase = database;
        return true;
    }

    private ExecutionResult ExecuteWithMutationReservation(
        EmbeddedDatabase database,
        Func<ExecutionResult> operation)
    {
        if (_transactionMutationDatabase is not null)
        {
            throw new EmbeddedSqlException(
                "Managed connections do not support reentrant writes from SQL callbacks.");
        }

        _transactionMutationDatabase = database;
        try
        {
            return operation();
        }
        finally
        {
            ReleaseTransactionMutation(database);
        }
    }

    private void ReleaseTransactionMutation(EmbeddedDatabase database)
    {
        if (!ReferenceEquals(_transactionMutationDatabase, database))
            throw new InvalidOperationException("The managed transaction mutation reservation was lost.");

        _transactionMutationDatabase = null;
    }

    private void CommitTransaction()
    {
        if (_transactionDatabases is null)
            throw new InvalidOperationException("No managed transaction is active.");

        var changed = _transactionDatabases
            .Where(pair => pair.Value.HasChanges)
            .ToArray();
        if (changed.Length > 1)
            throw new InvalidOperationException("A managed ATTACH transaction reached an unsafe multi-database write state.");

        if (changed.Length == 1)
        {
            var (database, state) = changed[0];
            try
            {
                database.CommitTransaction(
                    state.Catalog,
                    state.Version,
                    database.IsFileBacked && !state.HasSnapshotPragmaHeader
                        ? null
                        : state.PragmaHeader);
            }
            catch (EmbeddedPostCommitMaintenanceException)
            {
                ResetTransactionState();
                throw;
            }
        }

        ResetTransactionState();
    }

    // Runs an eligible top-level VALUES statement through its prepared, cached lowering. The lowering is
    // owned and cached by the calling EmbeddedStatement; this method only re-checks connection liveness so
    // a disposed connection throws exactly as the general Execute path does. A VALUES row list references
    // no tables, so it is independent of the transaction catalog, query-only guard, and last-insert-rowid
    // bookkeeping the general path threads -- nothing here needs them.
    internal ExecutionResult ExecutePreparedValues(
        EmbeddedDatabase.PreparedValuesLowering lowering,
        SqlValue[] parameters)
    {
        ThrowIfDisposed();
        return EmbeddedDatabase.ExecutePreparedValues(lowering, parameters);
    }

    private ExecutionResult ExecutePragmaQueryOnly(PragmaQueryOnlyStatement statement)
    {
        if (statement.Enabled is { } enabled)
        {
            _queryOnly = enabled;
            return ExecutionResult.Empty;
        }

        return new ExecutionResult(["query_only"], [[SqlValue.Integer(_queryOnly ? 1 : 0)]], 0);
    }

    private ExecutionResult ExecutePragmaForeignKeys(PragmaForeignKeysStatement statement)
    {
        // SQLite leaves this connection setting unchanged while a transaction or savepoint
        // is active; it is neither transactional nor shared with sibling connections.
        if (statement.Enabled is { } enabled && _transactionDatabases is null)
            _foreignKeys = enabled;

        return statement.Enabled is null
            ? new ExecutionResult(["foreign_keys"], [[SqlValue.Integer(_foreignKeys ? 1 : 0)]], 0)
            : ExecutionResult.Empty;
    }

    private ExecutionResult ExecutePragmaRecursiveTriggers(PragmaRecursiveTriggersStatement statement)
    {
        if (statement.Enabled is { } enabled)
        {
            _recursiveTriggers = enabled;
            return ExecutionResult.Empty;
        }

        return new ExecutionResult(
            ["recursive_triggers"],
            [[SqlValue.Integer(_recursiveTriggers ? 1 : 0)]],
            0);
    }

    private ExecutionResult ExecutePragmaHeaderInteger(PragmaHeaderIntegerStatement statement)
    {
        var columnName = statement.Kind switch
        {
            PragmaHeaderIntegerKind.SchemaVersion => "schema_version",
            PragmaHeaderIntegerKind.UserVersion => "user_version",
            PragmaHeaderIntegerKind.ApplicationId => "application_id",
            _ => throw new InvalidOperationException($"Unknown PRAGMA header integer kind {statement.Kind}."),
        };

        if (statement.Value is null)
        {
            var metadata = GetTransactionState(_database)?.PragmaHeader ?? _database.GetPragmaHeaderMetadata();
            var value = statement.Kind switch
            {
                PragmaHeaderIntegerKind.SchemaVersion => metadata.SchemaVersion,
                PragmaHeaderIntegerKind.UserVersion => metadata.UserVersion,
                PragmaHeaderIntegerKind.ApplicationId => metadata.ApplicationId,
                _ => throw new InvalidOperationException($"Unknown PRAGMA header integer kind {statement.Kind}."),
            };
            return new ExecutionResult([columnName], [[SqlValue.Integer(value)]], 0);
        }

        if (_queryOnly)
            throw new EmbeddedSqlException("attempt to write a readonly database");

        if (_database.IsFileBacked)
        {
            if (_database.IsReadOnly)
                throw new EmbeddedSqlException("attempt to write a readonly database");
            throw new EmbeddedSqlException(
                $"Managed file-backed databases do not support writes to PRAGMA {columnName}.");
        }

        var current = GetTransactionState(_database)?.PragmaHeader ?? _database.GetPragmaHeaderMetadata();
        var updated = statement.Kind switch
        {
            PragmaHeaderIntegerKind.SchemaVersion => current with { SchemaVersion = statement.Value.Value },
            PragmaHeaderIntegerKind.UserVersion => current with { UserVersion = statement.Value.Value },
            PragmaHeaderIntegerKind.ApplicationId => current with { ApplicationId = statement.Value.Value },
            _ => throw new InvalidOperationException($"Unknown PRAGMA header integer kind {statement.Kind}."),
        };

        if (_transactionDatabases is null)
        {
            _database.SetInMemoryPragmaHeaderMetadata(updated);
        }
        else if (updated != current)
        {
            EnsureTransactionMayMutate(_database, statement);
            var state = GetTransactionState(_database)
                ?? throw new InvalidOperationException("The managed transaction lost its primary database state.");
            state.PragmaHeader = updated;
            state.HasChanges = true;
            _transactionWriteDatabase = _database;
        }

        return ExecutionResult.Empty;
    }

    private ExecutionResult ExecutePragmaJournalMode(PragmaJournalModeStatement statement)
    {
        var current = _database.IsFileBacked
            ? _database.GetJournalMode().ToString().ToLowerInvariant()
            : "memory";
        if (statement.Mode is null)
            return new ExecutionResult(["journal_mode"], [[SqlValue.Text(current)]], 0);

        if (!_database.IsFileBacked)
            return new ExecutionResult(["journal_mode"], [[SqlValue.Text(current)]], 0);
        if (!Enum.TryParse<SqliteJournalMode>(statement.Mode, ignoreCase: true, out var requested))
            return new ExecutionResult(["journal_mode"], [[SqlValue.Text(current)]], 0);
        if (requested == _database.GetJournalMode())
            return new ExecutionResult(["journal_mode"], [[SqlValue.Text(current)]], 0);
        if (_queryOnly || _database.IsReadOnly)
            return new ExecutionResult(["journal_mode"], [[SqlValue.Text(current)]], 0);
        if (_transactionDatabases is not null)
            throw new EmbeddedSqlException("cannot change journal mode while a transaction is active");
        if (_attachedDatabases.Count != 0)
        {
            throw new EmbeddedSqlException(
                "Managed journal-mode transitions require all attached databases to be detached.");
        }

        var result = _database.SwitchJournalMode(requested).ToString().ToLowerInvariant();
        return new ExecutionResult(["journal_mode"], [[SqlValue.Text(result)]], 0);
    }

    private ExecutionResult ExecutePragmaPageSize(PragmaPageSizeStatement statement)
    {
        var current = _database.GetPageSize();
        if (statement.Value is null)
            return new ExecutionResult(["page_size"], [[SqlValue.Integer(current)]], 0);

        if (_queryOnly || _database.IsReadOnly)
            return ExecutionResult.Empty;

        var requested = statement.Value.Value;
        if (requested < SqlitePageSize.Minimum
            || requested > SqlitePageSize.Maximum
            || (requested & (requested - 1)) != 0)
        {
            return ExecutionResult.Empty;
        }

        _pendingPageSize = _database.IsFileBacked ? requested : null;
        return ExecutionResult.Empty;
    }

    private ExecutionResult ExecuteVacuum()
    {
        if (_queryOnly || _database.IsReadOnly)
            throw new EmbeddedSqlException("attempt to write a readonly database");
        if (_transactionDatabases is not null)
            throw new EmbeddedSqlException("cannot VACUUM from within a transaction");
        if (_attachedDatabases.Count != 0)
            throw new EmbeddedSqlException("Managed VACUUM requires all attached databases to be detached.");
        if (!_database.IsFileBacked)
            return ExecutionResult.Empty;

        var targetPageSize = _database.GetJournalMode() == SqliteJournalMode.Wal
            ? _database.GetPageSize()
            : _pendingPageSize ?? _database.GetPageSize();
        _database.MigratePageSize(targetPageSize);
        _pendingPageSize = null;
        return ExecutionResult.Empty;
    }

    /// <summary>
    /// The rowid of the most recent successful INSERT on this connection, mirroring the
    /// value reported by <c>last_insert_rowid()</c>.
    /// </summary>
    public long LastInsertRowId => _lastInsertRowId;

    internal string[] DescribeColumns(ParsedStatement statement)
    {
        if (statement is ExplainStatement)
            return EmbeddedDatabase.ExplainColumns();
        if (statement is ExplainQueryPlanStatement)
            return EmbeddedDatabase.ExplainQueryPlanColumns();
        if (statement is PragmaTableInfoStatement)
            return ["cid", "name", "type", "notnull", "dflt_value", "pk"];
        if (statement is PragmaTableXInfoStatement)
            return ["cid", "name", "type", "notnull", "dflt_value", "pk", "hidden"];
        if (statement is PragmaIndexListStatement)
            return ["seq", "name", "unique", "origin", "partial"];
        if (statement is PragmaIndexInfoStatement)
            return ["seqno", "cid", "name"];
        if (statement is PragmaTableListStatement)
            return ["schema", "name", "type", "ncol", "wr", "strict"];
        if (statement is PragmaDatabaseListStatement)
            return ["seq", "name", "file"];
        if (statement is PragmaEncodingStatement)
            return ["encoding"];
        if (statement is PragmaQueryOnlyStatement { Enabled: null })
            return ["query_only"];
        if (statement is PragmaForeignKeysStatement { Enabled: null })
            return ["foreign_keys"];
        if (statement is PragmaRecursiveTriggersStatement { Enabled: null })
            return ["recursive_triggers"];
        if (statement is PragmaHeaderIntegerStatement { Value: null } headerInteger)
        {
            return headerInteger.Kind switch
            {
                PragmaHeaderIntegerKind.SchemaVersion => ["schema_version"],
                PragmaHeaderIntegerKind.UserVersion => ["user_version"],
                PragmaHeaderIntegerKind.ApplicationId => ["application_id"],
                _ => throw new InvalidOperationException(
                    $"Unknown PRAGMA header integer kind {headerInteger.Kind}."),
            };
        }
        if (statement is PragmaJournalModeStatement)
            return ["journal_mode"];
        if (statement is PragmaPageSizeStatement { Value: null })
            return ["page_size"];

        var routed = RouteStatement(statement);
        var transactionState = GetTransactionState(routed.Database);
        if (EmbeddedDatabase.TryGetReturning(routed.Statement, out var returningTable, out var returning))
        {
            return transactionState is null
                ? routed.Database.DescribeReturning(returningTable, returning)
                : routed.Database.DescribeReturning(returningTable, returning, transactionState.Catalog);
        }

        if (routed.Statement is not QueryStatement query)
            return [];

        return transactionState is null
            ? routed.Database.DescribeColumns(query)
            : DescribeQueryColumns(query, transactionState.Catalog);
    }

    private static string[] DescribeQueryColumns(
        QueryStatement query,
        EmbeddedDatabase.SchemaCatalog catalog)
        => EmbeddedDatabase.DescribeQuery(
            query,
            new EmbeddedDatabase.QueryContext(
                catalog.Tables,
                new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
                catalog.Views,
                catalog.Triggers));

    private void CreateSavepoint(string name)
    {
        // A SAVEPOINT issued outside an explicit BEGIN...COMMIT opens a transaction
        // that stays active until its outermost savepoint is released or rolled back.
        if (_transactionDatabases is null)
            BeginTransaction(openedBySavepoint: true);

        _savepoints.Add(new SavepointEntry(
            name,
            _transactionDatabases!.ToDictionary(
                pair => pair.Key,
                pair => new SavepointDatabaseState(
                    pair.Value.Catalog.Clone(),
                    pair.Value.HasChanges,
                    pair.Value.PragmaHeader,
                    pair.Value.HasSnapshotPragmaHeader)),
            _transactionWriteDatabase));
    }

    private void ReleaseSavepoint(string name)
    {
        var index = FindSavepointIndex(name);

        // Releasing the outermost savepoint of a savepoint-opened transaction commits it.
        if (index == 0 && _transactionOpenedBySavepoint)
        {
            CommitTransaction();
            return;
        }

        // RELEASE removes the named savepoint and every savepoint created after it,
        // keeping their changes in the enclosing scope.
        _savepoints.RemoveRange(index, _savepoints.Count - index);
    }

    private void RollbackToSavepoint(string name)
    {
        var index = FindSavepointIndex(name);
        var savepoint = _savepoints[index];

        // Restore the state captured when the savepoint was created. Clone so the
        // stored snapshot stays pristine for a later ROLLBACK TO the same savepoint.
        if (_transactionDatabases is null)
            throw new InvalidOperationException("The managed savepoint lost its transaction state.");
        foreach (var (database, savedState) in savepoint.Databases)
        {
            var state = _transactionDatabases[database];
            state.Catalog = savedState.Catalog.Clone();
            state.HasChanges = savedState.HasChanges;
            state.PragmaHeader = savedState.PragmaHeader;
            state.HasSnapshotPragmaHeader = savedState.HasSnapshotPragmaHeader;
        }
        _transactionWriteDatabase = savepoint.WriteDatabase;

        // ROLLBACK TO keeps the named savepoint but cancels any created after it.
        if (index + 1 < _savepoints.Count)
            _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);
    }

    private int FindSavepointIndex(string name)
    {
        for (var index = _savepoints.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_savepoints[index].Name, name, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        throw new EmbeddedSqlException($"no such savepoint: {name}");
    }

    private void ResetTransactionState()
    {
        var transactionDatabases = _transactionDatabases;
        _transactionDatabases = null;
        _transactionWriteDatabase = null;
        _transactionMutationDatabase = null;
        _transactionOpenedBySavepoint = false;
        _savepoints.Clear();
        if (transactionDatabases is not null)
        {
            foreach (var database in transactionDatabases.Keys)
                database.EndTransaction();
        }
    }

    private sealed record SavepointEntry(
        string Name,
        IReadOnlyDictionary<EmbeddedDatabase, SavepointDatabaseState> Databases,
        EmbeddedDatabase? WriteDatabase);

    private sealed record SavepointDatabaseState(
        EmbeddedDatabase.SchemaCatalog Catalog,
        bool HasChanges,
        PragmaHeaderMetadata PragmaHeader,
        bool HasSnapshotPragmaHeader);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class EmbeddedStatement : IDisposable
{
    private readonly EmbeddedConnection _connection;
    private readonly ParsedStatement _statement;
    private readonly SqlParameterMap _parameters;
    private readonly SqlValue[] _boundValues;
    private readonly bool[] _isBound;
    private string[]? _columnNames;
    private ExecutionResult? _result;
    private int _rowIndex = -1;
    private bool _disposed;

    // Per-prepared-statement cache for an eligible top-level VALUES lowering. The immutable program and
    // slot map are compiled at most once (on the first execution that resolves the shape) and reused across
    // every Reset/rebind, so a routed VALUES stops recompiling per Execute. Reset never clears this; only
    // Dispose releases it. The state is a tri-state so a fallback shape (a computed cell) is resolved once
    // to Ineligible and then always routed to the evaluator, and an eligible shape stays Cached.
    private EmbeddedDatabase.PreparedValuesLowering? _valuesLowering;
    private ValuesLoweringState _valuesLoweringState = ValuesLoweringState.Unresolved;
    private int _valuesLoweringCompilationCount;

    private enum ValuesLoweringState
    {
        Unresolved,
        Cached,
        Ineligible,
    }

    internal EmbeddedStatement(EmbeddedConnection connection, ParsedStatement statement, SqlParameterMap parameters)
    {
        _connection = connection;
        _statement = statement;
        _parameters = parameters;
        _boundValues = new SqlValue[parameters.Count + 1];
        _isBound = new bool[parameters.Count + 1];
    }

    public int ParameterCount => _parameters.Count;

    public int ColumnCount => _result?.Columns.Length ?? _columnNames?.Length ?? 0;

    public int RowsAffected => _result?.RowsAffected ?? 0;

    public int GetColumnCount()
    {
        ThrowIfDisposed();
        return GetColumnNames().Length;
    }

    public bool HasRows()
    {
        ThrowIfDisposed();
        if (_statement is (QueryStatement or PragmaTableInfoStatement or PragmaTableXInfoStatement
            or PragmaIndexListStatement or PragmaIndexInfoStatement or PragmaTableListStatement
            or PragmaDatabaseListStatement or PragmaEncodingStatement or ExplainStatement)
            || _statement is ExplainQueryPlanStatement
            || _statement is PragmaQueryOnlyStatement { Enabled: null }
            || _statement is PragmaForeignKeysStatement { Enabled: null }
            || _statement is PragmaRecursiveTriggersStatement { Enabled: null }
            || _statement is PragmaHeaderIntegerStatement { Value: null }
            || _statement is PragmaJournalModeStatement
            || _statement is PragmaPageSizeStatement { Value: null }
            || EmbeddedDatabase.TryGetReturning(_statement, out _, out _))
        {
            ExecuteIfNeeded();
            return _result!.Rows.Count > 0;
        }

        return false;
    }

    public int GetParameterIndex(string name)
    {
        ThrowIfDisposed();
        return _parameters.TryGetIndex(name, out var index) ? index : 0;
    }

    public string? GetParameterName(int index)
    {
        ThrowIfDisposed();
        if (index < 1 || index > ParameterCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _parameters.GetName(index);
    }

    public void Bind(int index, SqlValue value)
    {
        ThrowIfDisposed();
        if (index < 1 || index > ParameterCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        _boundValues[index] = value.WithoutJsonSubtype();
        _isBound[index] = true;
    }

    public bool Bind(string name, SqlValue value)
    {
        ThrowIfDisposed();
        return _parameters.TryGetIndex(name, out var index) && BindResolved(index, value);
    }

    public StatementStepResult Step(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteIfNeeded(cancellationToken);

        if (++_rowIndex < _result!.Rows.Count)
            return StatementStepResult.Row;

        return StatementStepResult.Done;
    }

    public SqlValue GetValue(int ordinal)
    {
        ThrowIfDisposed();
        if (_result is null || _rowIndex < 0 || _rowIndex >= _result.Rows.Count)
            throw new InvalidOperationException("Statement is not positioned on a row.");
        if (ordinal < 0 || ordinal >= _result.Columns.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        return _result.Rows[_rowIndex][ordinal];
    }

    public string GetColumnName(int ordinal)
    {
        ThrowIfDisposed();
        var columnNames = GetColumnNames();
        if (ordinal < 0 || ordinal >= columnNames.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        return columnNames[ordinal];
    }

    public void Reset()
    {
        ThrowIfDisposed();
        _result = null;
        _rowIndex = -1;
    }

    /// <summary>
    /// Diagnostic/test seam: the immutable VDBE program cached for this statement's eligible top-level
    /// <c>VALUES</c> lowering, or <c>null</c> when the statement is not an eligible <c>VALUES</c>, has not
    /// been executed yet, or fell back to the evaluator. The reference is stable across <see cref="Reset"/>
    /// and rebinds, so observing the same instance across executions proves the program is reused rather
    /// than recompiled per execution.
    /// </summary>
    public VdbeProgram? CompiledValuesProgram => _valuesLowering?.Program;

    /// <summary>
    /// Diagnostic/test seam: the number of times an eligible top-level <c>VALUES</c> lowering has been
    /// compiled for this statement. It reaches one on the first execution that resolves the lowering and
    /// never increases again, so repeated <see cref="Reset"/>/rebind cycles reuse the cached program.
    /// </summary>
    public int ValuesProgramCompilationCount => _valuesLoweringCompilationCount;

    public void Dispose()
    {
        _disposed = true;
        _result = null;

        // Release the cached lowering so its VdbeProgram is collectable; a disposed statement rejects
        // any further use, so no re-resolution can occur after this point.
        _valuesLowering = null;
    }

    private bool BindResolved(int index, SqlValue value)
    {
        Bind(index, value);
        return true;
    }

    private void ExecuteIfNeeded(CancellationToken cancellationToken = default)
    {
        if (_result is not null)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 1; index <= ParameterCount; index++)
        {
            if (!_isBound[index])
                throw new EmbeddedSqlException(_parameters.GetName(index) is { } name
                    ? $"Missing value for parameter {name}."
                    : $"Missing value for parameter at position {index}.");
        }

        // An eligible top-level VALUES reuses its cached lowering (compiled once for this statement) instead
        // of recompiling on every execution; every other statement -- and any fallback VALUES shape -- keeps
        // the general execution path unchanged.
        if (_statement is ValuesClause values
            && TryExecuteCachedValuesLowering(values, out var valuesResult))
        {
            _result = valuesResult;
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        _result = _connection.Execute(_statement, _boundValues, cancellationToken);
        if (!EmbeddedDatabase.MayMutate(_statement))
            cancellationToken.ThrowIfCancellationRequested();
    }

    // Routes an eligible top-level VALUES through its per-statement cached lowering. The lowering is
    // resolved lazily on the first execution: an eligible shape compiles its immutable program and slot map
    // once (bumping the compilation count) and is reused thereafter, while a fallback shape (a computed
    // cell) is resolved once to Ineligible and always defers to the evaluator. Resolution can throw the
    // unequal-width diagnostic; because the state is only advanced on a clean resolution, that error keeps
    // recurring on each execution exactly as the evaluator raises it, and each successful run builds a fresh
    // parameter binding from the current bound values, so no mutable binding is shared across executions.
    private bool TryExecuteCachedValuesLowering(ValuesClause values, out ExecutionResult result)
    {
        result = null!;
        if (_valuesLoweringState == ValuesLoweringState.Ineligible)
            return false;

        if (_valuesLoweringState == ValuesLoweringState.Unresolved)
        {
            if (EmbeddedDatabase.TryPrepareValuesLowering(values, out var lowering))
            {
                _valuesLowering = lowering;
                _valuesLoweringState = ValuesLoweringState.Cached;
                _valuesLoweringCompilationCount++;
            }
            else
            {
                _valuesLoweringState = ValuesLoweringState.Ineligible;
                return false;
            }
        }

        result = _connection.ExecutePreparedValues(_valuesLowering!, _boundValues);
        return true;
    }

    private string[] GetColumnNames()
    {
        if (_result is not null)
            return _result.Columns;

        return _columnNames ??= _connection.DescribeColumns(_statement);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class EmbeddedTable
{
    private readonly Dictionary<string, int> _columnIndices;

    public EmbeddedTable(
        string name,
        IReadOnlyList<EmbeddedColumn> columns,
        bool withoutRowid = false,
        IReadOnlyList<TablePrimaryKeyColumn>? tablePrimaryKey = null,
        IReadOnlyList<TableUniqueConstraint>? uniqueConstraints = null,
        IReadOnlyList<CheckConstraint>? checkConstraints = null,
        InsertConflictAlgorithm? primaryKeyConflictAlgorithm = null,
        string? primaryKeyConstraintName = null,
        int? primaryKeyDeclarationOrder = null)
    {
        Name = name;
        ColumnDefinitions = columns.ToArray();
        Columns = ColumnDefinitions.Select(column => column.Name).ToArray();
        _columnIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Columns.Length; index++)
        {
            if (!_columnIndices.TryAdd(Columns[index], index))
                throw new EmbeddedSqlException($"duplicate column name: {Columns[index]}");
        }

        WithoutRowid = withoutRowid;
        TableLevelPrimaryKey = tablePrimaryKey is null
            ? null
            : Array.AsReadOnly(tablePrimaryKey.ToArray());
        TableUniqueConstraints = Array.AsReadOnly((uniqueConstraints ?? []).ToArray());
        CheckConstraints = Array.AsReadOnly((checkConstraints ?? []).ToArray());
        TablePrimaryKeyConflictAlgorithm = primaryKeyConflictAlgorithm;
        TablePrimaryKeyConstraintName = primaryKeyConstraintName;
        TablePrimaryKeyDeclarationOrder = primaryKeyDeclarationOrder;
        PrimaryKeyColumns = Array.AsReadOnly(
            ResolvePrimaryKeyColumns(ColumnDefinitions, TableLevelPrimaryKey, _columnIndices).ToArray());
        PrimaryKeySchema = CreatePrimaryKeySchema(ColumnDefinitions, TableLevelPrimaryKey, PrimaryKeyColumns);

        // A WITHOUT ROWID table has no rowid, so no column can alias one; a rowid table
        // keeps SQLite's single-column INTEGER PRIMARY KEY alias rule.
        RowidAliasColumnIndex = withoutRowid
            ? -1
            : ComputeRowidAliasColumnIndex(
                ColumnDefinitions,
                TableLevelPrimaryKey,
                PrimaryKeyColumns);

        GeneratedColumnOrder = ValidateAndOrderGeneratedColumns(ColumnDefinitions, PrimaryKeyColumns, _columnIndices);
        ForeignKeys = Array.AsReadOnly(
            ColumnDefinitions
                .Where(column => column.ForeignKey is not null)
                .Select(column => column.ForeignKey!)
                .ToArray());

        CreateConstraintIndexes();
        ValidateSchemaExpressions();
    }

    private void CreateConstraintIndexes()
    {
        if (WithoutRowid)
        {
            CreateWithoutRowidConstraintIndexes();
            return;
        }

        var autoIndex = 0;
        for (var columnIndex = 0; columnIndex < ColumnDefinitions.Length; columnIndex++)
        {
            var column = ColumnDefinitions[columnIndex];
            if (!column.Unique)
                continue;

            autoIndex++;
            Indexes.Add(new EmbeddedIndex(
                $"sqlite_autoindex_{Name}_{autoIndex}",
                Unique: true,
                [new EmbeddedIndexColumn(column.Name, columnIndex, column.Collation, Descending: false)],
                EmbeddedIndexOrigin.UniqueConstraint,
                column.UniqueConflictAlgorithm));
        }

        foreach (var constraint in GetTableKeyConstraintsInDeclarationOrder())
        {
            if (constraint.IsPrimaryKey && HasRowidAlias)
                continue;

            autoIndex++;
            Indexes.Add(new EmbeddedIndex(
                $"sqlite_autoindex_{Name}_{autoIndex}",
                Unique: true,
                constraint.Columns,
                constraint.IsPrimaryKey
                    ? EmbeddedIndexOrigin.PrimaryKey
                    : EmbeddedIndexOrigin.UniqueConstraint,
                constraint.ConflictAlgorithm));
        }
    }

    private void CreateWithoutRowidConstraintIndexes()
    {
        var autoIndex = 0;
        var groups = new List<(
            EmbeddedIndexColumn[] Columns,
            bool IsPrimaryKey,
            EmbeddedIndex? Index)>();

        for (var columnIndex = 0; columnIndex < ColumnDefinitions.Length; columnIndex++)
        {
            var column = ColumnDefinitions[columnIndex];
            var key = new[]
            {
                new EmbeddedIndexColumn(
                    column.Name,
                    columnIndex,
                    column.Collation,
                    column.PrimaryKeyDescending),
            };
            if (column.PrimaryKey)
            {
                AddConstraint(
                    key,
                    isPrimaryKey: true,
                    column.PrimaryKeyConflictAlgorithm);
            }
            if (column.Unique)
            {
                AddConstraint(
                    key,
                    isPrimaryKey: false,
                    column.UniqueConflictAlgorithm);
            }
        }

        foreach (var constraint in GetTableKeyConstraintsInDeclarationOrder())
        {
            AddConstraint(
                constraint.Columns,
                constraint.IsPrimaryKey,
                constraint.ConflictAlgorithm);
        }

        void AddConstraint(
            EmbeddedIndexColumn[] columns,
            bool isPrimaryKey,
            InsertConflictAlgorithm? conflictAlgorithm)
        {
            var existingPosition = groups.FindIndex(group => SameConstraintKey(group.Columns, columns));
            if (existingPosition >= 0)
            {
                var existing = groups[existingPosition];
                if (isPrimaryKey && !existing.IsPrimaryKey)
                {
                    if (existing.Index is not null)
                        Indexes.Remove(existing.Index);
                    groups[existingPosition] = (existing.Columns, IsPrimaryKey: true, Index: null);
                }
                return;
            }

            autoIndex++;
            EmbeddedIndex? index = null;
            if (!isPrimaryKey)
            {
                index = new EmbeddedIndex(
                    $"sqlite_autoindex_{Name}_{autoIndex}",
                    Unique: true,
                    columns,
                    EmbeddedIndexOrigin.UniqueConstraint,
                    conflictAlgorithm);
                Indexes.Add(index);
            }

            groups.Add((columns, isPrimaryKey, index));
        }

        bool SameConstraintKey(
            IReadOnlyList<EmbeddedIndexColumn> left,
            IReadOnlyList<EmbeddedIndexColumn> right)
        {
            if (left.Count != right.Count)
                return false;

            for (var index = 0; index < left.Count; index++)
            {
                if (left[index].ColumnIndex != right[index].ColumnIndex)
                    return false;
                var leftCollation = left[index].Collation
                    ?? ColumnDefinitions[left[index].ColumnIndex].Collation
                    ?? "BINARY";
                var rightCollation = right[index].Collation
                    ?? ColumnDefinitions[right[index].ColumnIndex].Collation
                    ?? "BINARY";
                if (!string.Equals(leftCollation, rightCollation, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }

    private IReadOnlyList<(
        EmbeddedIndexColumn[] Columns,
        bool IsPrimaryKey,
        InsertConflictAlgorithm? ConflictAlgorithm)> GetTableKeyConstraintsInDeclarationOrder()
    {
        var constraints = new List<(
            int DeclarationOrder,
            int FallbackOrder,
            EmbeddedIndexColumn[] Columns,
            bool IsPrimaryKey,
            InsertConflictAlgorithm? ConflictAlgorithm)>();
        if (TableLevelPrimaryKey is not null)
        {
            constraints.Add((
                TablePrimaryKeyDeclarationOrder ?? -1,
                0,
                ResolveConstraintIndexColumns(TableLevelPrimaryKey, "PRIMARY KEY"),
                IsPrimaryKey: true,
                TablePrimaryKeyConflictAlgorithm));
        }

        for (var index = 0; index < TableUniqueConstraints.Count; index++)
        {
            var constraint = TableUniqueConstraints[index];
            constraints.Add((
                constraint.DeclarationOrder,
                index + 1,
                ResolveConstraintIndexColumns(constraint.Columns, "UNIQUE"),
                IsPrimaryKey: false,
                constraint.ConflictAlgorithm));
        }

        return constraints
            .OrderBy(constraint => constraint.DeclarationOrder)
            .ThenBy(constraint => constraint.FallbackOrder)
            .Select(constraint => (
                constraint.Columns,
                constraint.IsPrimaryKey,
                constraint.ConflictAlgorithm))
            .ToArray();
    }

    private EmbeddedIndexColumn[] ResolveConstraintIndexColumns(
        IReadOnlyList<TablePrimaryKeyColumn> terms,
        string constraint)
    {
        if (terms.Count == 0)
            throw new EmbeddedSqlException($"{constraint} constraint must contain at least one column");

        var columns = new EmbeddedIndexColumn[terms.Count];
        var seen = new HashSet<int>();
        for (var position = 0; position < terms.Count; position++)
        {
            var term = terms[position];
            if (!_columnIndices.TryGetValue(term.Name, out var columnIndex))
                throw new EmbeddedSqlException($"no such column: {term.Name}");
            if (!seen.Add(columnIndex))
                throw new EmbeddedSqlException($"duplicate column name: {term.Name}");

            columns[position] = new EmbeddedIndexColumn(
                ColumnDefinitions[columnIndex].Name,
                columnIndex,
                term.Collation ?? ColumnDefinitions[columnIndex].Collation,
                term.Descending);
        }

        return columns;
    }

    private void ValidateSchemaExpressions()
    {
        foreach (var column in ColumnDefinitions)
        {
            if (column.DefaultExpression is not null)
                ValidateConstraintExpression(column.DefaultExpression, allowColumns: false, "default value");
            foreach (var check in column.CheckConstraints)
                ValidateConstraintExpression(check.Expression, allowColumns: true, "CHECK constraint");
        }

        foreach (var check in CheckConstraints)
            ValidateConstraintExpression(check.Expression, allowColumns: true, "CHECK constraint");
    }

    private void ValidateConstraintExpression(Expression expression, bool allowColumns, string context)
    {
        switch (expression)
        {
            case LiteralExpression:
            case CurrentTimeExpression:
                return;
            case ColumnExpression column:
                if (!allowColumns)
                    throw new EmbeddedSqlException($"default value of column is not constant: {column.Name}");
                if (!IsConstraintColumn(column))
                    throw new EmbeddedSqlException($"no such column: {column.Name}");
                return;
            case ParameterExpression:
                throw new EmbeddedSqlException($"parameters are prohibited in {context}s");
            case ScalarSubqueryExpression or ExistsExpression or InSubqueryExpression:
                throw new EmbeddedSqlException($"subqueries are prohibited in {context}s");
            case StarExpression or QualifiedStarExpression:
                throw new EmbeddedSqlException($"cannot use '*' in a {context}");
            case FunctionExpression function:
                if (function.Window is not null || function.Filter is not null || function.CountStar || function.Distinct)
                    throw new EmbeddedSqlException($"aggregate and window functions are prohibited in {context}s");
                if (!IsAllowedConstraintFunction(function.Name))
                    throw new EmbeddedSqlException($"function {function.Name.ToLowerInvariant()}() is not allowed in a {context}");
                foreach (var argument in function.Arguments)
                    ValidateConstraintExpression(argument, allowColumns, context);
                return;
            case CollationExpression collation:
                ValidateConstraintExpression(collation.Expression, allowColumns, context);
                return;
            case CastExpression cast:
                ValidateConstraintExpression(cast.Expression, allowColumns, context);
                return;
            case CaseExpression @case:
                if (@case.Operand is not null)
                    ValidateConstraintExpression(@case.Operand, allowColumns, context);
                foreach (var clause in @case.Clauses)
                {
                    ValidateConstraintExpression(clause.When, allowColumns, context);
                    ValidateConstraintExpression(clause.Then, allowColumns, context);
                }
                if (@case.Else is not null)
                    ValidateConstraintExpression(@case.Else, allowColumns, context);
                return;
            case LikeExpression like:
                ValidateConstraintExpression(like.Value, allowColumns, context);
                ValidateConstraintExpression(like.Pattern, allowColumns, context);
                if (like.Escape is not null)
                    ValidateConstraintExpression(like.Escape, allowColumns, context);
                return;
            case GlobExpression glob:
                ValidateConstraintExpression(glob.Value, allowColumns, context);
                ValidateConstraintExpression(glob.Pattern, allowColumns, context);
                return;
            case InExpression @in:
                ValidateConstraintExpression(@in.Value, allowColumns, context);
                foreach (var value in @in.Values)
                    ValidateConstraintExpression(value, allowColumns, context);
                return;
            case BetweenExpression between:
                ValidateConstraintExpression(between.Value, allowColumns, context);
                ValidateConstraintExpression(between.Lower, allowColumns, context);
                ValidateConstraintExpression(between.Upper, allowColumns, context);
                return;
            case UnaryExpression unary:
                ValidateConstraintExpression(unary.Operand, allowColumns, context);
                return;
            case BinaryExpression binary:
                ValidateConstraintExpression(binary.Left, allowColumns, context);
                ValidateConstraintExpression(binary.Right, allowColumns, context);
                return;
            default:
                throw new EmbeddedSqlException($"expression is not allowed in a {context}");
        }
    }

    private bool IsConstraintColumn(ColumnExpression column)
    {
        if (column.Qualifier is not null
            && !string.Equals(column.Qualifier, Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var bare = column.UnqualifiedName ?? column.Name;
        return _columnIndices.ContainsKey(bare)
            || (HasRowid && IsRowidAliasName(bare));
    }

    public bool HasQualifiedCheckReferences()
    {
        foreach (var check in ColumnDefinitions.SelectMany(column => column.CheckConstraints).Concat(CheckConstraints))
        {
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (CollectColumnReferences(check.Expression, references))
                return true;
        }

        return false;
    }

    public string Name { get; private set; }

    public string[] Columns { get; private set; }

    public EmbeddedColumn[] ColumnDefinitions { get; private set; }

    public List<SqlValue[]> Rows { get; } = [];

    // Parallel to <see cref="Rows"/> (index-aligned): the SQLite rowid backing each row.
    // Every row-mutating site keeps this list the same length and order as Rows.
    public List<long> RowIds { get; } = [];

    // Index of the INTEGER PRIMARY KEY column that aliases the rowid, or -1 when the
    // table has a hidden rowid. A single column-level INTEGER PRIMARY KEY (declared type
    // exactly "INTEGER", ascending) is the alias, matching SQLite.
    public int RowidAliasColumnIndex { get; private set; }

    public bool HasRowidAlias => RowidAliasColumnIndex >= 0;

    public InsertConflictAlgorithm? RowidAliasConflictAlgorithm
        => !HasRowidAlias
            ? null
            : TableLevelPrimaryKey is not null
                ? TablePrimaryKeyConflictAlgorithm
                : ColumnDefinitions[RowidAliasColumnIndex].PrimaryKeyConflictAlgorithm;

    // True for an ordinary rowid table; false for a WITHOUT ROWID table, which has no
    // hidden rowid and therefore rejects rowid/_rowid_/oid references.
    public bool HasRowid => !WithoutRowid;

    public bool WithoutRowid { get; }

    // The table-level PRIMARY KEY(...) clause as declared, retained so the schema can be
    // regenerated verbatim; null when the primary key (if any) is column-level.
    public IReadOnlyList<TablePrimaryKeyColumn>? TableLevelPrimaryKey { get; }

    public IReadOnlyList<TableUniqueConstraint> TableUniqueConstraints { get; }

    public IReadOnlyList<CheckConstraint> CheckConstraints { get; }

    public InsertConflictAlgorithm? TablePrimaryKeyConflictAlgorithm { get; }

    public InsertConflictAlgorithm? PrimaryKeyConflictAlgorithm
        => TableLevelPrimaryKey is not null
            ? TablePrimaryKeyConflictAlgorithm
            : PrimaryKeyColumns.Count == 1
                ? ColumnDefinitions[PrimaryKeyColumns[0].Index].PrimaryKeyConflictAlgorithm
                : null;

    public string? TablePrimaryKeyConstraintName { get; }

    public int? TablePrimaryKeyDeclarationOrder { get; }

    // The resolved primary-key columns (index + direction) in key order. Empty when the
    // table has no primary key. Used for WITHOUT ROWID ordering/uniqueness and table_info.
    public IReadOnlyList<(int Index, bool Descending)> PrimaryKeyColumns { get; }

    // The immutable physical-key descriptor in declaration order. A table-level COLLATE
    // overrides the declared column collation; absent declarations use SQLite's BINARY
    // default. File-backed index-table support must consume this instead of reconstructing
    // key metadata from lossy column flags.
    public SqlitePrimaryKeySchema? PrimaryKeySchema { get; }

    // Column indices of generated columns in dependency (topological) order, so evaluating
    // them in sequence always sees the values a later generated column depends on.
    public IReadOnlyList<int> GeneratedColumnOrder { get; }

    public bool HasGeneratedColumns => GeneratedColumnOrder.Count > 0;

    public bool HasVirtualGeneratedColumns => ColumnDefinitions.Any(
        column => column.IsGenerated && !column.GeneratedStored);

    public bool HasCheckConstraints => CheckConstraints.Count > 0
        || ColumnDefinitions.Any(column => column.CheckConstraints.Count > 0);

    public bool HasNonDefaultConflictAlgorithms => TablePrimaryKeyConflictAlgorithm is not null
        || TableUniqueConstraints.Any(constraint => constraint.ConflictAlgorithm is not null)
        || ColumnDefinitions.Any(column =>
            column.PrimaryKeyConflictAlgorithm is not null
            || column.NotNullConflictAlgorithm is not null
            || column.UniqueConflictAlgorithm is not null);

    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; }

    // The 1-based position of a column within the primary key, or 0 when it is not part of
    // the primary key. Mirrors the value SQLite reports in PRAGMA table_info.pk.
    public int PrimaryKeyPosition(int columnIndex)
    {
        for (var position = 0; position < PrimaryKeyColumns.Count; position++)
        {
            if (PrimaryKeyColumns[position].Index == columnIndex)
                return position + 1;
        }

        return 0;
    }

    public bool IsPrimaryKeyColumn(int columnIndex) => PrimaryKeyPosition(columnIndex) > 0;

    // Applies the column's declared-type affinity to a value, used to coerce a computed
    // generated-column result the same way an inserted value is coerced.
    public static SqlValue ApplyColumnAffinity(EmbeddedColumn column, SqlValue value)
        => ApplyAffinity(column, value);

    // True when the column's declared-type affinity is numeric (INTEGER/REAL/NUMERIC): the
    // subset where a stored INTEGER/REAL/NULL value feeds arithmetic unchanged, so a compiled
    // Arithmetic opcode over the column matches the tree-walking evaluator byte-for-byte. TEXT
    // and BLOB affinity columns decline (fall back to the evaluator), which applies the numeric
    // affinity the Arithmetic opcode deliberately does not.
    public bool ColumnHasNumericAffinity(int columnIndex)
    {
        var affinity = GetAffinity(ColumnDefinitions[columnIndex].DeclaredType);
        return affinity is ColumnAffinity.Integer or ColumnAffinity.Real or ColumnAffinity.Numeric;
    }

    public List<EmbeddedIndex> Indexes { get; } = [];

    public int GetColumnIndex(string name)
    {
        if (!_columnIndices.TryGetValue(name, out var index))
            throw new EmbeddedSqlException($"table has no column named {name}");

        return index;
    }

    public bool TryGetColumnIndex(string name, out int index) => _columnIndices.TryGetValue(name, out index);

    // rowid/_rowid_/oid are interchangeable spellings of the rowid pseudo-column in SQLite.
    public static bool IsRowidAliasName(string name)
        => string.Equals(name, "rowid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "_rowid_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "oid", StringComparison.OrdinalIgnoreCase);

    // A rowid alias requires the declared type to be exactly "INTEGER" (case-insensitive,
    // trimmed). Broader integer affinities like "BIGINT" or "INT" are NOT rowid aliases.
    public static bool IsIntegerDeclaredType(string? declaredType)
        => declaredType is not null
            && string.Equals(declaredType.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase);

    private static int ComputeRowidAliasColumnIndex(
        IReadOnlyList<EmbeddedColumn> columns,
        IReadOnlyList<TablePrimaryKeyColumn>? tablePrimaryKey,
        IReadOnlyList<(int Index, bool Descending)> primaryKeyColumns)
    {
        if (tablePrimaryKey is not null)
        {
            if (primaryKeyColumns.Count != 1)
                return -1;

            var tableCandidate = primaryKeyColumns[0].Index;
            return IsIntegerDeclaredType(columns[tableCandidate].DeclaredType)
                ? tableCandidate
                : -1;
        }

        var primaryKeyCount = 0;
        var candidate = -1;
        for (var index = 0; index < columns.Count; index++)
        {
            if (!columns[index].PrimaryKey)
                continue;

            primaryKeyCount++;
            candidate = index;
        }

        // Only a single-column INTEGER PRIMARY KEY declared ascending aliases the rowid.
        if (primaryKeyCount != 1
            || !IsIntegerDeclaredType(columns[candidate].DeclaredType)
            || columns[candidate].PrimaryKeyDescending)
        {
            return -1;
        }

        return candidate;
    }

    // Resolves the effective primary-key columns. A table-level PRIMARY KEY(...) takes the
    // declared column order; otherwise column-level PRIMARY KEY markers are used. Declaring
    // both a table-level and column-level primary key is rejected, matching SQLite.
    private static IReadOnlyList<(int Index, bool Descending)> ResolvePrimaryKeyColumns(
        IReadOnlyList<EmbeddedColumn> columns,
        IReadOnlyList<TablePrimaryKeyColumn>? tablePrimaryKey,
        IReadOnlyDictionary<string, int> indices)
    {
        var columnLevel = new List<(int Index, bool Descending)>();
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].PrimaryKey)
                columnLevel.Add((index, columns[index].PrimaryKeyDescending));
        }

        if (tablePrimaryKey is null)
            return columnLevel;

        if (columnLevel.Count > 0)
            throw new EmbeddedSqlException("table has more than one primary key");

        var resolved = new List<(int Index, bool Descending)>(tablePrimaryKey.Count);
        foreach (var keyColumn in tablePrimaryKey)
        {
            if (!indices.TryGetValue(keyColumn.Name, out var index))
                throw new EmbeddedSqlException($"no such column: {keyColumn.Name}");

            resolved.Add((index, keyColumn.Descending));
        }

        return resolved;
    }

    private static SqlitePrimaryKeySchema? CreatePrimaryKeySchema(
        IReadOnlyList<EmbeddedColumn> columns,
        IReadOnlyList<TablePrimaryKeyColumn>? tablePrimaryKey,
        IReadOnlyList<(int Index, bool Descending)> primaryKeyColumns)
    {
        if (primaryKeyColumns.Count == 0)
            return null;

        if (tablePrimaryKey is not null && tablePrimaryKey.Count != primaryKeyColumns.Count)
            throw new EmbeddedSqlException("primary-key metadata is inconsistent");

        var terms = new SqlitePrimaryKeyTerm[primaryKeyColumns.Count];
        for (var position = 0; position < primaryKeyColumns.Count; position++)
        {
            var (columnIndex, descending) = primaryKeyColumns[position];
            var collation = tablePrimaryKey?[position].Collation ?? columns[columnIndex].Collation;
            terms[position] = new SqlitePrimaryKeyTerm(
                columnIndex,
                columns[columnIndex].Name,
                descending ? SqliteKeySortOrder.Descending : SqliteKeySortOrder.Ascending,
                collation is null ? SqliteKeyCollation.Binary : SqliteKeyCollation.FromName(collation));
        }

        return new SqlitePrimaryKeySchema(terms);
    }

    // Validates the generated columns and returns their evaluation order. The precedence of
    // checks matches SQLite: DEFAULT-on-generated, generated-in-PRIMARY-KEY, at-least-one
    // non-generated column, then per-expression validation and loop detection.
    private static IReadOnlyList<int> ValidateAndOrderGeneratedColumns(
        IReadOnlyList<EmbeddedColumn> columns,
        IReadOnlyList<(int Index, bool Descending)> primaryKeyColumns,
        IReadOnlyDictionary<string, int> indices)
    {
        var generated = new List<int>();
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].IsGenerated)
                generated.Add(index);
        }

        if (generated.Count == 0)
            return [];

        foreach (var index in generated)
        {
            if (columns[index].HasDefault)
                throw new EmbeddedSqlException("cannot use DEFAULT on a generated column");
        }

        foreach (var (index, _) in primaryKeyColumns)
        {
            if (columns[index].IsGenerated)
                throw new EmbeddedSqlException("generated columns cannot be part of the PRIMARY KEY");
        }

        if (generated.Count == columns.Count)
            throw new EmbeddedSqlException("must have at least one non-generated column");

        var dependencies = new Dictionary<int, List<int>>();
        foreach (var index in generated)
        {
            var expression = columns[index].GenerationExpression!;
            ValidateGenerationExpressionAllowed(expression);

            var referencedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectColumnReferences(expression, referencedNames);

            var generatedDependencies = new List<int>();
            foreach (var name in referencedNames)
            {
                if (!indices.TryGetValue(name, out var referencedIndex))
                    throw new EmbeddedSqlException($"no such column: {name}");

                if (columns[referencedIndex].IsGenerated)
                    generatedDependencies.Add(referencedIndex);
            }

            dependencies[index] = generatedDependencies;
        }

        var order = new List<int>(generated.Count);
        var state = new Dictionary<int, int>();
        foreach (var index in generated)
        {
            if (state.GetValueOrDefault(index) != 2)
                VisitGeneratedColumn(index, columns, dependencies, state, order);
        }

        return order;
    }

    private static void VisitGeneratedColumn(
        int node,
        IReadOnlyList<EmbeddedColumn> columns,
        Dictionary<int, List<int>> dependencies,
        Dictionary<int, int> state,
        List<int> order)
    {
        state[node] = 1;
        foreach (var dependency in dependencies[node])
        {
            var dependencyState = state.GetValueOrDefault(dependency);
            if (dependencyState == 1)
                throw new EmbeddedSqlException($"generated column loop on \"{columns[node].Name}\"");
            if (dependencyState != 2)
                VisitGeneratedColumn(dependency, columns, dependencies, state, order);
        }

        state[node] = 2;
        order.Add(node);
    }

    // Rejects constructs that cannot be evaluated deterministically inside a generated
    // column: bound parameters, subqueries, aggregate/window functions, and any scalar
    // function outside the deterministic allow-list.
    private static void ValidateGenerationExpressionAllowed(Expression expression)
    {
        switch (expression)
        {
            case LiteralExpression:
            case ColumnExpression:
                return;
            case ParameterExpression:
                throw new EmbeddedSqlException("cannot use a bound parameter in a generated column");
            case ScalarSubqueryExpression or ExistsExpression or InSubqueryExpression:
                throw new EmbeddedSqlException("subqueries are not allowed in a generated column");
            case StarExpression or QualifiedStarExpression:
                throw new EmbeddedSqlException("cannot use '*' in a generated column");
            case FunctionExpression function:
                if (function.Window is not null || function.Filter is not null || function.CountStar || function.Distinct)
                    throw new EmbeddedSqlException("aggregate and window functions are not allowed in a generated column");
                if (!IsAllowedGeneratedFunction(function.Name))
                    throw new EmbeddedSqlException($"function {function.Name.ToLowerInvariant()}() is not allowed in a generated column");
                foreach (var argument in function.Arguments)
                    ValidateGenerationExpressionAllowed(argument);
                return;
            case CollationExpression collation:
                ValidateGenerationExpressionAllowed(collation.Expression);
                return;
            case CastExpression cast:
                ValidateGenerationExpressionAllowed(cast.Expression);
                return;
            case CaseExpression @case:
                if (@case.Operand is not null)
                    ValidateGenerationExpressionAllowed(@case.Operand);
                foreach (var clause in @case.Clauses)
                {
                    ValidateGenerationExpressionAllowed(clause.When);
                    ValidateGenerationExpressionAllowed(clause.Then);
                }
                if (@case.Else is not null)
                    ValidateGenerationExpressionAllowed(@case.Else);
                return;
            case LikeExpression like:
                ValidateGenerationExpressionAllowed(like.Value);
                ValidateGenerationExpressionAllowed(like.Pattern);
                if (like.Escape is not null)
                    ValidateGenerationExpressionAllowed(like.Escape);
                return;
            case GlobExpression glob:
                ValidateGenerationExpressionAllowed(glob.Value);
                ValidateGenerationExpressionAllowed(glob.Pattern);
                return;
            case InExpression @in:
                ValidateGenerationExpressionAllowed(@in.Value);
                foreach (var value in @in.Values)
                    ValidateGenerationExpressionAllowed(value);
                return;
            case BetweenExpression between:
                ValidateGenerationExpressionAllowed(between.Value);
                ValidateGenerationExpressionAllowed(between.Lower);
                ValidateGenerationExpressionAllowed(between.Upper);
                return;
            case UnaryExpression unary:
                ValidateGenerationExpressionAllowed(unary.Operand);
                return;
            case BinaryExpression binary:
                ValidateGenerationExpressionAllowed(binary.Left);
                ValidateGenerationExpressionAllowed(binary.Right);
                return;
            default:
                throw new EmbeddedSqlException("expression is not allowed in a generated column");
        }
    }

    // The deterministic scalar functions the managed engine implements and can therefore
    // reproduce byte-for-byte inside a generated column. Non-deterministic or connection-
    // scoped functions (date/time, last_insert_rowid) are deliberately excluded.
    private static bool IsAllowedGeneratedFunction(string name)
    {
        return name.ToUpperInvariant() switch
        {
            "ABS" or "COALESCE" or "GLOB" or "HEX" or "IFNULL" or "JSON" or "JSON_ARRAY"
                or "JSON_ARRAY_LENGTH" or "JSON_ERROR_POSITION" or "JSON_EXTRACT" or "JSON_INSERT"
                or "JSON_OBJECT" or "JSON_PATCH" or "JSON_QUOTE" or "JSON_REMOVE" or "JSON_REPLACE"
                or "JSON_SET" or "JSON_TYPE" or "JSON_VALID" or "LENGTH" or "LIKE" or "LOWER"
                or "MAX" or "MIN" or "NULLIF" or "TYPEOF" or "UPPER" => true,
            _ => false,
        };
    }

    private static bool IsAllowedConstraintFunction(string name)
    {
        return IsAllowedGeneratedFunction(name)
            || name.ToUpperInvariant() is "DATE" or "DATETIME" or "INSTR" or "JULIANDAY"
                or "PRINTF" or "FORMAT" or "STRFTIME" or "TIME" or "UNIXEPOCH";
    }

    private static bool CollectColumnReferences(Expression expression, HashSet<string> names)
    {
        switch (expression)
        {
            case ColumnExpression column:
                names.Add(column.Name);
                return column.Qualifier is not null;
            case FunctionExpression function:
                var functionQualified = false;
                foreach (var argument in function.Arguments)
                    functionQualified |= CollectColumnReferences(argument, names);
                if (function.Filter is not null)
                    functionQualified |= CollectColumnReferences(function.Filter, names);
                return functionQualified;
            case CollationExpression collation:
                return CollectColumnReferences(collation.Expression, names);
            case CastExpression cast:
                return CollectColumnReferences(cast.Expression, names);
            case CaseExpression @case:
                var caseQualified = false;
                if (@case.Operand is not null)
                    caseQualified |= CollectColumnReferences(@case.Operand, names);
                foreach (var clause in @case.Clauses)
                {
                    caseQualified |= CollectColumnReferences(clause.When, names);
                    caseQualified |= CollectColumnReferences(clause.Then, names);
                }
                if (@case.Else is not null)
                    caseQualified |= CollectColumnReferences(@case.Else, names);
                return caseQualified;
            case LikeExpression like:
                var likeQualified = CollectColumnReferences(like.Value, names)
                    | CollectColumnReferences(like.Pattern, names);
                if (like.Escape is not null)
                    likeQualified |= CollectColumnReferences(like.Escape, names);
                return likeQualified;
            case GlobExpression glob:
                return CollectColumnReferences(glob.Value, names)
                    | CollectColumnReferences(glob.Pattern, names);
            case InExpression @in:
                var inQualified = CollectColumnReferences(@in.Value, names);
                foreach (var value in @in.Values)
                    inQualified |= CollectColumnReferences(value, names);
                return inQualified;
            case BetweenExpression between:
                return CollectColumnReferences(between.Value, names)
                    | CollectColumnReferences(between.Lower, names)
                    | CollectColumnReferences(between.Upper, names);
            case UnaryExpression unary:
                return CollectColumnReferences(unary.Operand, names);
            case BinaryExpression binary:
                return CollectColumnReferences(binary.Left, names)
                    | CollectColumnReferences(binary.Right, names);
            default:
                return false;
        }
    }


    public static bool TryCoerceRowid(SqlValue value, out long rowid)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
                rowid = value.AsInteger();
                return true;
            case SqlValueKind.Real:
                {
                    var real = value.AsReal();
                    if (real == Math.Truncate(real)
                        && real >= long.MinValue
                        && real < -(double)long.MinValue)
                    {
                        rowid = (long)real;
                        return true;
                    }

                    break;
                }
            case SqlValueKind.Text:
                {
                    var text = value.AsText().Trim();
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out rowid))
                        return true;
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                        && double.IsFinite(real)
                        && real == Math.Truncate(real)
                        && real >= long.MinValue
                        && real < -(double)long.MinValue)
                    {
                        rowid = (long)real;
                        return true;
                    }

                    break;
                }
        }

        rowid = 0;
        return false;
    }

    public void AddColumn(EmbeddedColumn column)
    {
        if (_columnIndices.ContainsKey(column.Name))
            throw new EmbeddedSqlException($"duplicate column name: {column.Name}");
        if (column.ForeignKey is not null)
            throw new EmbeddedSqlException("ALTER TABLE ADD COLUMN with REFERENCES is not supported.");
        if (column.PrimaryKey || column.Unique)
            throw new EmbeddedSqlException("Cannot add a PRIMARY KEY or UNIQUE column.");

        _ = new EmbeddedTable(
            Name,
            [.. ColumnDefinitions, column],
            WithoutRowid,
            TableLevelPrimaryKey,
            TableUniqueConstraints,
            CheckConstraints,
            TablePrimaryKeyConflictAlgorithm,
            TablePrimaryKeyConstraintName,
            TablePrimaryKeyDeclarationOrder);

        if (column.DefaultExpression is not null && Rows.Count > 0)
            throw new EmbeddedSqlException("Cannot add a column with non-constant default.");

        var defaultValue = ApplyAffinity(column, column.DefaultValue ?? SqlValue.Null);
        if (column.NotNull && Rows.Count > 0 && defaultValue.Kind == SqlValueKind.Null)
            throw new EmbeddedSqlException("Cannot add a NOT NULL column without a default value.");

        var index = Columns.Length;
        Columns = [.. Columns, column.Name];
        ColumnDefinitions = [.. ColumnDefinitions, column];
        _columnIndices.Add(column.Name, index);
        for (var rowIndex = 0; rowIndex < Rows.Count; rowIndex++)
        {
            var row = Rows[rowIndex];
            Array.Resize(ref row, index + 1);
            row[index] = defaultValue;
            Rows[rowIndex] = row;
        }
    }

    public void Rename(string newName)
    {
        var autoIndexPrefix = $"sqlite_autoindex_{Name}_";
        var renamedIndexes = new List<(int Position, string Name)>();
        for (var index = 0; index < Indexes.Count; index++)
        {
            if (Indexes[index].Origin is not (
                EmbeddedIndexOrigin.UniqueConstraint or EmbeddedIndexOrigin.PrimaryKey))
            {
                continue;
            }

            var indexName = Indexes[index].Name;
            if (!indexName.StartsWith(autoIndexPrefix, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(
                    indexName.AsSpan(autoIndexPrefix.Length),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var ordinal)
                || ordinal <= 0)
            {
                throw new InvalidOperationException(
                    $"Constraint index '{indexName}' does not match table '{Name}'.");
            }

            renamedIndexes.Add((index, $"sqlite_autoindex_{newName}_{ordinal}"));
        }

        Name = newName;
        foreach (var (position, name) in renamedIndexes)
            Indexes[position] = Indexes[position] with { Name = name };
    }

    public SqlValue[] CreateRowWithDefaults(Func<Expression, SqlValue> evaluate)
    {
        return ColumnDefinitions
            .Select(column => ApplyAffinity(
                column,
                column.DefaultExpression is { } expression
                    ? evaluate(expression)
                    : column.DefaultValue ?? SqlValue.Null))
            .ToArray();
    }

    public void RenameColumn(string name, string newName)
    {
        var index = GetColumnIndex(name);
        if (_columnIndices.ContainsKey(newName))
            throw new EmbeddedSqlException($"duplicate column name: {newName}");
        if (HasCheckConstraints
            || HasGeneratedColumns
            || TableLevelPrimaryKey is not null
            || TableUniqueConstraints.Count > 0
            || ColumnDefinitions[index].ForeignKey is not null)
        {
            throw new EmbeddedSqlException(
                "ALTER TABLE RENAME COLUMN cannot rewrite retained CHECK, generated, table-key, "
                + "or foreign-key schema expressions until managed schema token rewriting is implemented.");
        }

        Columns[index] = newName;
        ColumnDefinitions[index] = ColumnDefinitions[index] with { Name = newName };
        _columnIndices.Remove(name);
        _columnIndices.Add(newName, index);

        for (var indexPosition = 0; indexPosition < Indexes.Count; indexPosition++)
        {
            var definition = Indexes[indexPosition];
            if (definition.Columns.All(column => column.ColumnIndex != index))
                continue;

            var updatedColumns = definition.Columns
                .Select(column => column.ColumnIndex == index ? column with { Name = newName } : column)
                .ToArray();
            Indexes[indexPosition] = definition with { Columns = updatedColumns };
        }
    }

    public EmbeddedTable Clone()
    {
        var clone = new EmbeddedTable(
            Name,
            ColumnDefinitions,
            WithoutRowid,
            TableLevelPrimaryKey,
            TableUniqueConstraints,
            CheckConstraints,
            TablePrimaryKeyConflictAlgorithm,
            TablePrimaryKeyConstraintName,
            TablePrimaryKeyDeclarationOrder);
        foreach (var row in Rows)
            clone.Rows.Add(row.ToArray());

        clone.RowIds.AddRange(RowIds);
        clone.Indexes.RemoveAll(index => index.Origin == EmbeddedIndexOrigin.Explicit);
        clone.Indexes.AddRange(Indexes.Where(index => index.Origin == EmbeddedIndexOrigin.Explicit));
        return clone;
    }

    public void ApplyAffinities(SqlValue[] row)
    {
        for (var columnIndex = 0; columnIndex < ColumnDefinitions.Length; columnIndex++)
            row[columnIndex] = ApplyAffinity(ColumnDefinitions[columnIndex], row[columnIndex]);
    }

    public void ValidateRows(string tableName, IReadOnlyList<SqlValue[]> rows)
    {
        for (var columnIndex = 0; columnIndex < ColumnDefinitions.Length; columnIndex++)
        {
            // A WITHOUT ROWID primary key (and any table-level PRIMARY KEY) is validated
            // separately with table-qualified messages, so skip those columns here to avoid
            // an unqualified duplicate check.
            if ((WithoutRowid || TableLevelPrimaryKey is not null) && IsPrimaryKeyColumn(columnIndex))
                continue;

            var column = ColumnDefinitions[columnIndex];
            if (column.NotNull && rows.Any(row => row[columnIndex].Kind == SqlValueKind.Null))
            {
                throw new EmbeddedSqlException(
                    $"NOT NULL constraint failed: {tableName}.{column.Name}",
                    column.NotNullConflictAlgorithm);
            }

            if (!column.PrimaryKey)
                continue;

            var values = new HashSet<SqlValue>();
            foreach (var row in rows)
            {
                var value = row[columnIndex];
                if (value.Kind == SqlValueKind.Null)
                    continue;
                if (!values.Add(value))
                {
                    throw new EmbeddedSqlException(
                        $"UNIQUE constraint failed: {tableName}.{column.Name}",
                        column.PrimaryKeyConflictAlgorithm);
                }
            }
        }
    }

    private static SqlValue ApplyAffinity(EmbeddedColumn column, SqlValue value)
    {
        value = value.WithoutJsonSubtype();
        if (value.Kind is SqlValueKind.Null or SqlValueKind.Blob)
            return value;

        var affinity = GetAffinity(column.DeclaredType);
        if (affinity == ColumnAffinity.Blob)
            return value;
        if (affinity == ColumnAffinity.Text)
            return value.Kind == SqlValueKind.Text
                ? value
                : SqlValue.Text(ToText(value));

        if (!TryGetNumeric(value, out var numeric))
            return value;
        if (affinity == ColumnAffinity.Real)
            return numeric.Kind == SqlValueKind.Integer
                ? SqlValue.Real(numeric.AsInteger())
                : numeric;

        return ConvertToIntegerWhenExact(numeric);
    }

    private static ColumnAffinity GetAffinity(string? declaredType)
    {
        if (string.IsNullOrEmpty(declaredType)
            || declaredType.Contains("BLOB", StringComparison.OrdinalIgnoreCase))
        {
            return ColumnAffinity.Blob;
        }
        if (declaredType.Contains("INT", StringComparison.OrdinalIgnoreCase))
            return ColumnAffinity.Integer;
        if (declaredType.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
            || declaredType.Contains("CLOB", StringComparison.OrdinalIgnoreCase)
            || declaredType.Contains("TEXT", StringComparison.OrdinalIgnoreCase))
        {
            return ColumnAffinity.Text;
        }
        if (declaredType.Contains("REAL", StringComparison.OrdinalIgnoreCase)
            || declaredType.Contains("FLOA", StringComparison.OrdinalIgnoreCase)
            || declaredType.Contains("DOUB", StringComparison.OrdinalIgnoreCase))
        {
            return ColumnAffinity.Real;
        }

        return ColumnAffinity.Numeric;
    }

    private static bool TryGetNumeric(SqlValue value, out SqlValue numeric)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
            case SqlValueKind.Real:
                numeric = value;
                return true;
            case SqlValueKind.Text:
                {
                    var text = value.AsText().Trim();
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    {
                        numeric = SqlValue.Integer(integer);
                        return true;
                    }
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                        && double.IsFinite(real))
                    {
                        numeric = SqlValue.Real(real);
                        return true;
                    }

                    break;
                }
        }

        numeric = default;
        return false;
    }

    private static SqlValue ConvertToIntegerWhenExact(SqlValue numeric)
    {
        if (numeric.Kind == SqlValueKind.Integer)
            return numeric;

        var real = numeric.AsReal();
        return real == Math.Truncate(real)
            && real >= long.MinValue
            && real < -(double)long.MinValue
            ? SqlValue.Integer((long)real)
            : numeric;
    }

    private static string ToText(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Text => value.AsText(),
            _ => throw new InvalidOperationException($"Cannot convert {value.Kind} to text."),
        };
    }

}

internal enum ColumnAffinity
{
    Blob,
    Text,
    Numeric,
    Integer,
    Real,
}

internal sealed record ExecutionResult(
    string[] Columns,
    IReadOnlyList<SqlValue[]> Rows,
    int RowsAffected,
    bool Changed = false)
{
    public static ExecutionResult Empty { get; } = new([], [], 0);

    // The rowid assigned by the most recent INSERT this statement performed, surfaced so
    // the owning connection can answer last_insert_rowid(). Null for statements that did
    // not insert a row.
    public long? LastInsertRowId { get; init; }
}

internal readonly record struct BlobMutationIdentity
{
    public BlobMutationIdentity(string tableName, long rowId)
    {
        TableName = tableName.ToUpperInvariant();
        RowId = rowId;
    }

    public string TableName { get; }

    public long RowId { get; }
}

internal sealed class BlobMutationLease(EmbeddedDatabase database, BlobMutationIdentity identity) : IDisposable
{
    private EmbeddedDatabase? _database = database;

    public void Dispose()
    {
        var database = System.Threading.Interlocked.Exchange(ref _database, null);
        database?.ReleaseBlobMutationLease(identity);
    }
}

internal sealed record OutputColumn(string? Qualifier, string Name, int Index, int? CoalesceIndex = null);

internal sealed record SourceRow(
    string[] Columns,
    SqlValue[] Values,
    IReadOnlyDictionary<string, int>? QualifiedColumns = null,
    SourceRow? Parent = null,
    IReadOnlyList<OutputColumn>? OutputColumns = null,
    long? RowId = null,
    string? RowIdQualifier = null)
{
    public SqlValue GetValue(string name)
        => GetValue(name, allowQualifiedLookup: true);

    public SqlValue GetValue(ColumnExpression column)
    {
        if (column.Qualifier is null)
            return GetValue(column.Name, allowQualifiedLookup: false);

        if (QualifiedColumns is not null
            && QualifiedColumns.TryGetValue(column.Name, out var qualifiedIndex))
        {
            return Values[qualifiedIndex];
        }

        for (var index = 0; index < Columns.Length; index++)
        {
            if (string.Equals(Columns[index], column.Name, StringComparison.OrdinalIgnoreCase))
                return Values[index];
        }

        if (RowId is { } rowid
            && RowIdQualifier is not null
            && string.Equals(column.Qualifier, RowIdQualifier, StringComparison.OrdinalIgnoreCase)
            && column.UnqualifiedName is { } bareName
            && EmbeddedTable.IsRowidAliasName(bareName))
        {
            return SqlValue.Integer(rowid);
        }

        if (Parent is not null)
            return Parent.GetValue(column);

        throw new EmbeddedSqlException($"no such column: {column.Name}");
    }

    private SqlValue GetValue(string name, bool allowQualifiedLookup)
    {
        if (allowQualifiedLookup
            && QualifiedColumns is not null
            && QualifiedColumns.TryGetValue(name, out var qualifiedIndex))
        {
            return Values[qualifiedIndex];
        }

        // Columns joined with USING/NATURAL are coalesced: an unqualified reference to
        // such a column must resolve to COALESCE(left, right) so RIGHT/FULL joins report
        // the surviving side rather than the NULL-padded one.
        if (OutputColumns is not null && !name.Contains('.'))
        {
            foreach (var output in OutputColumns)
            {
                if (output.CoalesceIndex is { } coalesceIndex
                    && string.Equals(output.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    var primary = Values[output.Index];
                    return primary.Kind == SqlValueKind.Null ? Values[coalesceIndex] : primary;
                }
            }
        }

        for (var index = 0; index < Columns.Length; index++)
        {
            if (string.Equals(Columns[index], name, StringComparison.OrdinalIgnoreCase))
                return Values[index];
        }

        // rowid/_rowid_/oid resolve to the hidden rowid only after real columns are
        // consulted, so a user column that happens to be named "oid" shadows the alias,
        // exactly as SQLite does.
        if (RowId is { } rowid && EmbeddedTable.IsRowidAliasName(name))
            return SqlValue.Integer(rowid);

        if (Parent is not null)
            return Parent.GetValue(name, allowQualifiedLookup);

        throw new EmbeddedSqlException($"no such column: {name}");
    }

}

internal sealed record SourceData(
    string[] Columns,
    IReadOnlyList<SourceRow> Rows,
    IReadOnlyList<string?>? Collations = null);
