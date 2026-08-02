using System.Diagnostics;

namespace Ahtola.Core.Storage;

/// <summary>The current lifecycle state of a <see cref="SqlitePager"/>.</summary>
public enum SqlitePagerState
{
    Ready,
    TransactionActive,
    Checkpointing,
    Faulted,
    Disposed,
}

/// <summary>The lifecycle state of a <see cref="SqlitePagerTransaction"/>.</summary>
public enum SqlitePagerTransactionState
{
    Active,
    Committed,
    RolledBack,
    Faulted,
}

/// <summary>
/// Result of installing the committed WAL overlay into the main database file.
/// </summary>
/// <remarks>
/// <see cref="RetainedCommittedFrameCount"/> reports whether the caller retained
/// the checkpointed WAL history or durably reset it after installing the same
/// view in the main store. The lock-carrier <c>-shm</c> file remains intact.
/// </remarks>
public sealed record SqliteCheckpointResult(
    uint DatabaseSizeInPages,
    int InstalledPageCount,
    long RetainedCommittedFrameCount);

/// <summary>
/// A single-writer SQLite page cache and WAL overlay. It makes only frames up
/// to the last durable commit marker visible and retains WAL bytes during
/// checkpoint installation so a failed main-file write remains recoverable.
/// </summary>
/// <remarks>
/// Pagers that use the same <see cref="IFileSystem"/> and storage paths share a
/// <see cref="SqlitePagerLockManager"/>. Physical-file pagers additionally hold
/// SQLite's complete main-file lock-byte range for their lifetime. This grants
/// one process exclusive client ownership while allowing managed pagers inside
/// that process to share the database. The pager does not implement SQLite's
/// WAL-index, so ordinary SQLite clients and other managed processes must not
/// access the database until every pager in the owner process is disposed.
/// Other platforms fail ownership acquisition rather than silently using only
/// process-local locks. WAL commits become visible at their flushed commit
/// marker; DELETE-mode main-file writes are protected by a hot rollback journal.
/// See <c>docs/wal-interoperability-contract.md</c> for
/// the normative ownership, busy, recovery, and cache-invalidation contract.
/// </remarks>
public sealed class SqlitePager : IDisposable
{
    /// <summary>
    /// Default maximum number of clean main-database page images retained by one
    /// pager instance.
    /// </summary>
    public const int DefaultPageCacheCapacity = 64;

    private readonly object _gate = new();
    private readonly IFileSystem _fileSystem;
    private readonly string _databasePath;
    private readonly string _walPath;
    private readonly string _journalPath;
    private readonly SqlitePageStore _pageStore;
    private SqliteWalFile? _wal;
    private readonly SqlitePagerLockManager _lockManager;
    private IDisposable? _clientOwnership;
    private readonly Dictionary<uint, byte[]> _walPageOverlay = [];
    private readonly SqlitePagerReadCache _pageCache;
    private readonly HashSet<SqlitePagerReadTransaction> _activeReadTransactions = [];
    private SqlitePagerTransaction? _activeTransaction;
    private SqliteWalRecoveryInfo _recoveryInfo;
    private SqliteWalRecoveryInfo _visibleRecoveryInfo;
    private uint _committedPageCount;
    private long _committedFrameCount;
    private long _lockGeneration;
    private SqliteJournalMode _journalMode;
    private SqlitePagerState _state;
    private TimeSpan _busyTimeout;
    private readonly bool _foreignReadOnly;

    private SqlitePager(
        IFileSystem fileSystem,
        string databasePath,
        string walPath,
        SqlitePageStore pageStore,
        SqliteWalFile? wal,
        SqliteJournalMode journalMode,
        SqlitePagerLockManager lockManager,
        int pageCacheCapacity,
        bool foreignReadOnly = false)
    {
        _fileSystem = fileSystem;
        _foreignReadOnly = foreignReadOnly;
        _databasePath = databasePath;
        _walPath = walPath;
        _journalPath = databasePath + "-journal";
        _pageStore = pageStore;
        _wal = wal;
        _journalMode = journalMode;
        _lockManager = lockManager;
        _pageCache = new SqlitePagerReadCache(pageCacheCapacity);
        _recoveryInfo = CreateEmptyRecoveryInfo();
        _visibleRecoveryInfo = CreateEmptyRecoveryInfo();
    }

    /// <summary>The durable transaction format currently used by this pager.</summary>
    public SqliteJournalMode JournalMode
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _journalMode;
            }
        }
    }

    /// <summary>The fixed SQLite page size shared by the main store and WAL.</summary>
    public int PageSize
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _pageStore.PageSize;
            }
        }
    }

    /// <summary>The database size represented by the currently committed view.</summary>
    public uint CommittedPageCount
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _committedPageCount;
            }
        }
    }

    /// <summary>
    /// The maximum number of clean main-database page images this pager retains.
    /// WAL-overlay and transaction images are not part of this cache.
    /// </summary>
    public int PageCacheCapacity
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _pageCache.Capacity;
            }
        }
    }

    /// <summary>
    /// The current number of clean main-database page images retained by this
    /// pager. This is always at most <see cref="PageCacheCapacity"/>.
    /// </summary>
    public int CachedPageCount
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _pageCache.Count;
            }
        }
    }

    /// <summary>Whether either owned storage file is read-only.</summary>
    public bool IsReadOnly
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _pageStore.IsReadOnly || (_wal?.IsReadOnly ?? false);
            }
        }
    }

    /// <summary>The pager's explicit lifecycle state.</summary>
    public SqlitePagerState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>
    /// The reader/writer/checkpoint state machine used by this pager. Default
    /// physical-file managers also acquire matching <c>-shm</c> byte-range
    /// locks on Windows and Linux.
    /// </summary>
    public SqlitePagerLockManager LockManager => _lockManager;

    /// <summary>
    /// Default time to wait for a process-local reader, writer, or checkpoint
    /// lock. The default is zero, which reports contention immediately.
    /// File-backed locks retry external contention until this timeout expires.
    /// </summary>
    public TimeSpan BusyTimeout
    {
        get
        {
            lock (_gate)
                return _busyTimeout;
        }
        set
        {
            ValidateBusyTimeout(value, nameof(value));
            lock (_gate)
                _busyTimeout = value;
        }
    }

    /// <summary>
    /// The recovery-visible committed state used to establish this view. For a
    /// writable open, a corrupt or uncommitted tail has already been truncated to
    /// its last physical commit boundary. After a pager reset, an empty WAL can
    /// instead report its durable checkpoint marker; its zero valid-frame count
    /// distinguishes that state from retained WAL frames.
    /// </summary>
    public SqliteWalRecoveryInfo RecoveryInfo
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _visibleRecoveryInfo;
            }
        }
    }

    /// <summary>
    /// Creates a fresh SQLite database and a matching, empty SQLite WAL file.
    /// </summary>
    public static SqlitePager Create(
        IFileSystem fileSystem,
        string databasePath,
        string walPath,
        SqliteWalHeader walHeader,
        SqliteDatabaseHeader? databaseHeader = null,
        SqlitePagerLockManager? lockManager = null,
        TimeSpan? busyTimeout = null,
        AhtolaEncryptionOptions? encryption = null,
        int pageCacheCapacity = DefaultPageCacheCapacity)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(walPath);
        ArgumentNullException.ThrowIfNull(walHeader);
        ValidateBusyTimeout(busyTimeout, nameof(busyTimeout));
        ValidatePageCacheCapacity(pageCacheCapacity, nameof(pageCacheCapacity));
        encryption ??= GetFileSystemEncryption(fileSystem);
        var effectiveDatabaseHeader = databaseHeader ?? SqliteDatabaseHeader.CreateDefault();
        if (effectiveDatabaseHeader.PageSize != walHeader.PageSize)
            throw new InvalidOperationException("SQLite database and WAL page sizes must match.");
        if (effectiveDatabaseHeader.WriteVersion != SqliteFileFormatVersion.Wal
            || effectiveDatabaseHeader.ReadVersion != SqliteFileFormatVersion.Wal)
        {
            throw new InvalidOperationException("A SQLite WAL overlay requires WAL read and write format versions.");
        }

        var configuredBusyTimeout = busyTimeout ?? TimeSpan.Zero;
        var effectiveLockManager = lockManager ?? SqlitePagerLockRegistry.Get(fileSystem, databasePath, walPath);
        var storageFileSystem = CreateStorageFileSystem(fileSystem);
        var lockStopwatch = configuredBusyTimeout == Timeout.InfiniteTimeSpan
            ? null
            : Stopwatch.StartNew();
        var clientOwnership = SqliteManagedFileOwnershipRegistry.Acquire(
            fileSystem,
            databasePath,
            createNew: true,
            readOnly: false,
            configuredBusyTimeout);
        SqlitePageStore? pageStore = null;
        SqliteWalFile? wal = null;
        var databaseCreated = clientOwnership is not null;
        var walCreated = false;
        try
        {
            using var createLock = EnterLockWithinBudget(
                effectiveLockManager,
                SqlitePagerLockOperation.Checkpoint,
                configuredBusyTimeout,
                lockStopwatch,
                pagerReadOnly: false);
            pageStore = SqlitePageStore.Create(
                storageFileSystem,
                databasePath,
                effectiveDatabaseHeader,
                overwrite: clientOwnership is not null,
                encryption: encryption);
            databaseCreated = true;
            wal = SqliteWalFile.Create(storageFileSystem, walPath, walHeader, encryption);
            walCreated = true;

            var pager = new SqlitePager(
                storageFileSystem,
                databasePath,
                walPath,
                pageStore,
                wal,
                SqliteJournalMode.Wal,
                effectiveLockManager,
                pageCacheCapacity);
            pager.InitializeCommittedView(wal.ScanRecovery());
            pager._lockGeneration = createLock.PublishStorageChange();
            pager._state = SqlitePagerState.Ready;
            pager._busyTimeout = busyTimeout ?? TimeSpan.Zero;
            pager._clientOwnership = clientOwnership;
            clientOwnership = null;
            return pager;
        }
        catch
        {
            try
            {
                wal?.Dispose();
            }
            catch
            {
            }

            try
            {
                pageStore?.Dispose();
            }
            catch
            {
            }

            if (walCreated)
                TryDeleteCreatedArtifact(storageFileSystem, walPath);
            if (databaseCreated)
                TryDeleteCreatedArtifact(storageFileSystem, databasePath);

            throw;
        }
        finally
        {
            clientOwnership?.Dispose();
        }
    }

    /// <summary>
    /// Opens a main database and WAL pair, rebuilding the visible page overlay
    /// from every valid transaction through the last commit marker.
    /// </summary>
    /// <remarks>
    /// Writable opens physically discard a corrupt, partial, or uncommitted WAL
    /// tail. Read-only opens expose the same recovered view but retain that tail.
    /// A foreign read-only open (<paramref name="foreignReadOnly"/>) additionally
    /// skips ownership acquisition and the shared-memory lock coordinator entirely,
    /// so the database may be owned by another engine and its directory may be
    /// read-only. Foreign pagers rebuild their committed view from durable storage
    /// before every read and tolerate uncommitted WAL tails written by the owner;
    /// any structural inconsistency (hot rollback journal, torn or
    /// checksum-invalid pages) still faults the pager closed.
    /// </remarks>
    public static SqlitePager Open(
        IFileSystem fileSystem,
        string databasePath,
        string walPath,
        bool readOnly = false,
        SqlitePagerLockManager? lockManager = null,
        TimeSpan? busyTimeout = null,
        AhtolaEncryptionOptions? encryption = null,
        int pageCacheCapacity = DefaultPageCacheCapacity,
        bool foreignReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(walPath);
        ValidateBusyTimeout(busyTimeout, nameof(busyTimeout));
        ValidatePageCacheCapacity(pageCacheCapacity, nameof(pageCacheCapacity));
        if (foreignReadOnly && !readOnly)
            throw new ArgumentException("A foreign open is always read-only.", nameof(foreignReadOnly));
        if (foreignReadOnly && encryption is not null)
            throw new ArgumentException("A foreign open cannot combine with managed encryption.", nameof(foreignReadOnly));
        encryption ??= GetFileSystemEncryption(fileSystem);

        var configuredBusyTimeout = busyTimeout ?? TimeSpan.Zero;
        var effectiveLockManager = lockManager
            ?? (foreignReadOnly
                ? SqlitePagerLockRegistry.GetProcessLocal(fileSystem, databasePath, walPath)
                : SqlitePagerLockRegistry.Get(fileSystem, databasePath, walPath));
        var storageFileSystem = CreateStorageFileSystem(fileSystem);
        var lockStopwatch = configuredBusyTimeout == Timeout.InfiniteTimeSpan
            ? null
            : Stopwatch.StartNew();
        var clientOwnership = foreignReadOnly
            ? null
            : SqliteManagedFileOwnershipRegistry.Acquire(
                fileSystem,
                databasePath,
                createNew: false,
                readOnly,
                configuredBusyTimeout);
        try
        {
            var openOperation = readOnly
                ? SqlitePagerLockOperation.Reader
                : SqlitePagerLockOperation.Writer;
            using var openLock = EnterLockWithinBudget(
                effectiveLockManager,
                openOperation,
                configuredBusyTimeout,
                lockStopwatch,
                pagerReadOnly: readOnly);
            using var recoveryLock = readOnly
                ? null
                : effectiveLockManager.EnterRecoveryLock(
                    SqlitePagerLockManager.RemainingFileLockTimeout(configuredBusyTimeout, lockStopwatch),
                    configuredBusyTimeout);
            SqliteRollbackJournal.RecoverIfPresent(
                storageFileSystem,
                databasePath,
                databasePath + "-journal",
                readOnly);
            var pageStore = SqlitePageStore.OpenForPager(storageFileSystem, databasePath, readOnly, encryption);
            try
            {
                var header = pageStore.Header;
                if (header.WriteVersion != header.ReadVersion)
                {
                    throw new InvalidDataException(
                        "SQLite database read and write format versions must match for managed storage.");
                }

                var journalMode = header.WriteVersion switch
                {
                    SqliteFileFormatVersion.Legacy => SqliteJournalMode.Delete,
                    SqliteFileFormatVersion.Wal => SqliteJournalMode.Wal,
                    _ => throw new InvalidDataException(
                        $"Managed storage does not support SQLite file format version {header.WriteVersion}."),
                };
                SqliteWalFile? wal = null;
                try
                {
                    if (journalMode == SqliteJournalMode.Wal)
                    {
                        if (storageFileSystem.FileExists(walPath))
                        {
                            wal = SqliteWalFile.Open(storageFileSystem, walPath, readOnly, encryption);
                        }
                        else if (!readOnly)
                        {
                            wal = SqliteWalFile.Create(
                                storageFileSystem,
                                walPath,
                                SqliteWalHeader.Create(
                                    pageStore.PageSize,
                                    unchecked((uint)Random.Shared.NextInt64()),
                                    unchecked((uint)Random.Shared.NextInt64())),
                                encryption);
                        }
                    }
                    else if (!readOnly && storageFileSystem.FileExists(walPath))
                    {
                        TryDeleteCreatedArtifact(storageFileSystem, walPath);
                    }

                    var pager = new SqlitePager(
                        storageFileSystem,
                        databasePath,
                        walPath,
                        pageStore,
                        wal,
                        journalMode,
                        effectiveLockManager,
                        pageCacheCapacity,
                        foreignReadOnly);
                    if (journalMode == SqliteJournalMode.Wal && wal is not null)
                    {
                        var recovery = wal.ScanRecovery();
                        try
                        {
                            pager.InitializeCommittedView(recovery);
                        }
                        catch (InvalidDataException exception) when (readOnly)
                        {
                            throw new InvalidDataException(
                                "Cannot safely open the SQLite database read-only because its WAL cannot establish a non-mutating committed snapshot. "
                                + "Open it writable to recover the WAL.",
                                exception);
                        }
                        if (!readOnly)
                        {
                            var repairRecovery = wal.RecoverToLastCommittedFrame();
                            if (repairRecovery != recovery)
                            {
                                throw new InvalidDataException(
                                    "SQLite WAL changed between authenticated recovery scanning and tail repair.");
                            }
                        }
                    }
                    else if (journalMode == SqliteJournalMode.Delete)
                    {
                        pager.InitializeRollbackView();
                    }
                    else
                    {
                        pager.InitializeCleanWalView();
                    }

                    pager._lockGeneration = readOnly
                        ? effectiveLockManager.Generation
                        : openLock.PublishStorageChange();
                    pager._state = SqlitePagerState.Ready;
                    pager._busyTimeout = busyTimeout ?? TimeSpan.Zero;
                    pager._clientOwnership = clientOwnership;
                    clientOwnership = null;
                    return pager;
                }
                catch
                {
                    wal?.Dispose();
                    throw;
                }
            }
            catch
            {
                pageStore.Dispose();
                throw;
            }
        }
        finally
        {
            clientOwnership?.Dispose();
        }
    }

    private static SqlitePagerLockLease EnterLockWithinBudget(
        SqlitePagerLockManager lockManager,
        SqlitePagerLockOperation operation,
        TimeSpan configuredTimeout,
        Stopwatch? stopwatch,
        bool pagerReadOnly)
    {
        var remaining = SqlitePagerLockManager.RemainingFileLockTimeout(configuredTimeout, stopwatch);
        if (remaining == TimeSpan.Zero && configuredTimeout != TimeSpan.Zero)
            throw new SqlitePagerBusyException(operation, configuredTimeout);

        try
        {
            return operation switch
            {
                SqlitePagerLockOperation.Reader => lockManager.EnterReader(remaining, pagerReadOnly),
                SqlitePagerLockOperation.Writer => lockManager.EnterWriter(remaining),
                SqlitePagerLockOperation.Checkpoint => lockManager.EnterCheckpoint(remaining),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown SQLite lock operation."),
            };
        }
        catch (SqlitePagerBusyException exception)
        {
            throw new SqlitePagerBusyException(operation, configuredTimeout, exception);
        }
    }

    /// <summary>Reads a copy of one page from the committed WAL-overlay view.</summary>
    public byte[] ReadCommittedPage(uint pageNumber)
    {
        using var readerLock = _lockManager.EnterReader(ResolveBusyTimeout(null), IsReadOnly);
        lock (_gate)
        {
            ThrowIfNotReadable();
            SynchronizeCommittedView();
            var page = new byte[_pageStore.PageSize];
            ReadCommittedPageCore(pageNumber, page);
            return page;
        }
    }

    /// <summary>
    /// Reads one page from the committed WAL-overlay view into an exact page-sized
    /// destination.
    /// </summary>
    public void ReadCommittedPage(uint pageNumber, Span<byte> destination)
    {
        using var readerLock = _lockManager.EnterReader(ResolveBusyTimeout(null), IsReadOnly);
        lock (_gate)
        {
            ThrowIfNotReadable();
            SynchronizeCommittedView();
            ReadCommittedPageCore(pageNumber, destination);
        }
    }

    /// <summary>
    /// Begins a stable committed snapshot. Readers do not block the WAL writer,
    /// but an active snapshot prevents a checkpoint from installing its pages.
    /// </summary>
    public SqlitePagerReadTransaction BeginReadTransaction(TimeSpan? busyTimeout = null)
    {
        var readerLock = _lockManager.EnterReader(ResolveBusyTimeout(busyTimeout), IsReadOnly);
        try
        {
            lock (_gate)
            {
                ThrowIfNotReadable();
                SynchronizeCommittedView();
                var transaction = new SqlitePagerReadTransaction(
                    this,
                    readerLock,
                    _committedPageCount,
                    new Dictionary<uint, byte[]>(_walPageOverlay),
                    _lockGeneration);
                _activeReadTransactions.Add(transaction);
                return transaction;
            }
        }
        catch
        {
            readerLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Begins one in-memory transaction. New pages must be materialized before
    /// commit; pages are never implicitly zero-filled or skipped.
    /// </summary>
    public SqlitePagerTransaction BeginTransaction(uint targetDatabaseSizeInPages, TimeSpan? busyTimeout = null)
    {
        var configuredBusyTimeout = ResolveBusyTimeout(busyTimeout);
        var lockStopwatch = configuredBusyTimeout == Timeout.InfiniteTimeSpan
            ? null
            : Stopwatch.StartNew();
        while (true)
        {
            var requireExclusiveReaders = JournalMode == SqliteJournalMode.Delete;
            var remaining = SqlitePagerLockManager.RemainingFileLockTimeout(
                configuredBusyTimeout,
                lockStopwatch);
            var transactionLock = requireExclusiveReaders
                ? _lockManager.EnterCheckpoint(remaining)
                : _lockManager.EnterWriter(remaining);
            var retry = false;
            try
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ThrowIfReadOnly();
                    SynchronizeCommittedView();
                    if ((_journalMode == SqliteJournalMode.Delete) != requireExclusiveReaders)
                    {
                        retry = true;
                    }
                    else
                    {
                        if (_lockManager.UsesFileBackedWalLocks
                            && HasUncommittedOrInvalidTail(_recoveryInfo))
                        {
                            var recoveryLock = _lockManager.EnterRecoveryLock(
                                SqlitePagerLockManager.RemainingFileLockTimeout(configuredBusyTimeout, lockStopwatch),
                                configuredBusyTimeout);
                            try
                            {
                                using (recoveryLock)
                                    RecoverUncommittedTailUnderWriterLock(transactionLock);
                            }
                            catch
                            {
                                TransitionToFaulted();
                                throw;
                            }
                        }
                        if (_state != SqlitePagerState.Ready)
                        {
                            throw new InvalidOperationException(
                                $"Cannot begin a SQLite pager transaction while the pager is {_state}.");
                        }
                        ArgumentOutOfRangeException.ThrowIfZero(targetDatabaseSizeInPages);

                        var transaction = new SqlitePagerTransaction(this, targetDatabaseSizeInPages, transactionLock);
                        _activeTransaction = transaction;
                        _state = SqlitePagerState.TransactionActive;
                        return transaction;
                    }
                }
            }
            catch
            {
                transactionLock.Dispose();
                throw;
            }

            transactionLock.Dispose();
            if (!retry)
                throw new InvalidOperationException("SQLite transaction lock selection did not produce a transaction.");
        }
    }

    /// <summary>
    /// Begins a rewrite while holding the exclusive checkpoint lease from before
    /// the first write through WAL installation and reset.
    /// </summary>
    internal SqlitePagerTransaction BeginExclusiveRewriteTransaction(
        uint targetDatabaseSizeInPages,
        TimeSpan? busyTimeout = null)
    {
        var configuredBusyTimeout = ResolveBusyTimeout(busyTimeout);
        var lockStopwatch = configuredBusyTimeout == Timeout.InfiniteTimeSpan
            ? null
            : Stopwatch.StartNew();
        var transactionLock = _lockManager.EnterCheckpoint(configuredBusyTimeout);
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ThrowIfReadOnly();
                SynchronizeCommittedView();
                if (_lockManager.UsesFileBackedWalLocks
                    && HasUncommittedOrInvalidTail(_recoveryInfo))
                {
                    var recoveryLock = _lockManager.EnterRecoveryLock(
                        SqlitePagerLockManager.RemainingFileLockTimeout(
                            configuredBusyTimeout,
                            lockStopwatch),
                        configuredBusyTimeout);
                    try
                    {
                        using (recoveryLock)
                            RecoverUncommittedTailUnderWriterLock(transactionLock);
                    }
                    catch
                    {
                        TransitionToFaulted();
                        throw;
                    }
                }
                if (_state != SqlitePagerState.Ready)
                {
                    throw new InvalidOperationException(
                        $"Cannot begin an exclusive SQLite rewrite while the pager is {_state}.");
                }
                ArgumentOutOfRangeException.ThrowIfZero(targetDatabaseSizeInPages);

                var transaction = new SqlitePagerTransaction(
                    this,
                    targetDatabaseSizeInPages,
                    transactionLock,
                    checkpointWalAfterCommit: true);
                _activeTransaction = transaction;
                _state = SqlitePagerState.TransactionActive;
                return transaction;
            }
        }
        catch
        {
            transactionLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Discards an uncommitted, partial, or corrupt WAL tail without publishing a
    /// new transaction. The recovery runs while holding the managed writer and
    /// recovery locks, so it honors the supplied busy timeout and cannot race a
    /// managed writer or recovery operation. A recovery-lease release failure
    /// faults this pager, while the writer lease still releases its local owner.
    /// </summary>
    public void RecoverUncommittedWalTail(TimeSpan? busyTimeout = null)
    {
        if (JournalMode != SqliteJournalMode.Wal)
            throw new InvalidOperationException("Rollback-journal mode does not have a WAL tail to recover.");

        var configuredBusyTimeout = ResolveBusyTimeout(busyTimeout);
        var lockStopwatch = configuredBusyTimeout == Timeout.InfiniteTimeSpan
            ? null
            : Stopwatch.StartNew();
        using var writerLock = _lockManager.EnterWriter(configuredBusyTimeout);
        var recoveryLock = _lockManager.EnterRecoveryLock(
            SqlitePagerLockManager.RemainingFileLockTimeout(configuredBusyTimeout, lockStopwatch),
            configuredBusyTimeout);
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ThrowIfReadOnly();
                if (_state != SqlitePagerState.Ready)
                    throw new InvalidOperationException($"Cannot recover a SQLite WAL while the pager is {_state}.");

                try
                {
                    var wal = RequireWal();
                    var recovery = wal.ScanRecovery();
                    InitializeCommittedView(recovery);
                    _lockGeneration = _lockManager.Generation;
                    if (!HasUncommittedOrInvalidTail(recovery))
                        return;

                    wal.RecoverToLastCommittedFrame();
                    recovery = wal.ScanRecovery();
                    if (HasUncommittedOrInvalidTail(recovery))
                        throw new InvalidDataException("SQLite WAL recovery did not remove its uncommitted or invalid tail.");

                    InitializeCommittedView(recovery);
                    _lockGeneration = writerLock.PublishStorageChange();
                }
                catch
                {
                    TransitionToFaulted();
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                recoveryLock?.Dispose();
            }
            catch
            {
                lock (_gate)
                {
                    if (_state != SqlitePagerState.Disposed)
                        TransitionToFaulted();
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Commits a complete table-leaf mutation through this pager's WAL overlay.
    /// Its source page count must match the currently committed view.
    /// </summary>
    public void CommitMutation(SqliteTableLeafMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        using var transaction = BeginTransaction(mutation.TargetDatabaseSizeInPages);
        lock (_gate)
        {
            if (mutation.PageSize != _pageStore.PageSize)
                throw new InvalidOperationException("SQLite table-leaf mutation and pager page sizes do not match.");
            if (mutation.SourceDatabaseSizeInPages != _committedPageCount)
            {
                throw new InvalidOperationException(
                    "SQLite table-leaf mutation was prepared against a different committed database size.");
            }
        }

        foreach (var overflowPage in mutation.OverflowPages)
            transaction.WritePage(overflowPage.PageNumber, overflowPage.Page.Span);
        transaction.WritePage(mutation.TableLeafPageNumber, mutation.TableLeafPage.Span);
        transaction.Commit();
    }

    /// <summary>
    /// Installs the visible WAL page images into the main database file while
    /// retaining the WAL. The operation is allowed only when every page it needs
    /// is recoverable from the still-retained WAL and no transaction is active.
    /// </summary>
    public SqliteCheckpointResult CheckpointToMainStore(TimeSpan? busyTimeout = null)
        => CheckpointToMainStoreCore(busyTimeout, resetCommittedWal: false);

    /// <summary>
    /// Exclusively installs the committed WAL view into the durable main database
    /// file, then reclaims the WAL frames and in-memory overlay that it replaced.
    /// </summary>
    /// <remarks>
    /// The WAL is reset only after main-store writes and flushes succeed, the main
    /// file has the committed page count, and a second WAL validation confirms no
    /// external change occurred while checkpointing. Any failure leaves the pager
    /// faulted and does not intentionally discard WAL recovery evidence.
    /// </remarks>
    public SqliteCheckpointResult CheckpointToMainStoreAndResetWal(TimeSpan? busyTimeout = null)
        => CheckpointToMainStoreCore(busyTimeout, resetCommittedWal: true);

    /// <summary>
    /// Changes between WAL and DELETE mode only after the current committed view
    /// is durable in the main file. The header transition itself is protected by
    /// a rollback journal, so interruption preserves either complete format.
    /// </summary>
    public SqliteJournalMode SwitchJournalMode(
        SqliteJournalMode journalMode,
        TimeSpan? busyTimeout = null)
    {
        if (JournalMode == journalMode)
            return journalMode;

        using var checkpointLock = _lockManager.EnterCheckpoint(ResolveBusyTimeout(busyTimeout));
        if (journalMode == SqliteJournalMode.Delete)
            CheckpointToMainStoreUnderLock(checkpointLock, resetCommittedWal: true);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            SynchronizeCommittedView();
            if (_journalMode == journalMode)
                return journalMode;
            if (_state != SqlitePagerState.Ready)
                throw new InvalidOperationException($"Cannot change journal mode while the SQLite pager is {_state}.");

            var pageOne = _pageStore.ReadPage(1);
            var currentHeader = SqliteDatabaseHeader.Parse(pageOne);
            var nextCounter = unchecked(currentHeader.ChangeCounter + 1);
            var formatVersion = journalMode == SqliteJournalMode.Wal
                ? SqliteFileFormatVersion.Wal
                : SqliteFileFormatVersion.Legacy;
            var nextHeader = currentHeader with
            {
                WriteVersion = formatVersion,
                ReadVersion = formatVersion,
                ChangeCounter = nextCounter,
                VersionValidFor = nextCounter,
                DatabaseSizeInPages = _committedPageCount,
            };
            nextHeader.WriteTo(pageOne);

            SqliteWalFile? createdWal = null;
            try
            {
                if (journalMode == SqliteJournalMode.Wal)
                {
                    if (_fileSystem.FileExists(_walPath))
                        TryDeleteCreatedArtifact(_fileSystem, _walPath);
                    createdWal = SqliteWalFile.Create(
                        _fileSystem,
                        _walPath,
                        SqliteWalHeader.Create(
                            _pageStore.PageSize,
                            unchecked((uint)Random.Shared.NextInt64()),
                            unchecked((uint)Random.Shared.NextInt64())),
                        GetFileSystemEncryption(_fileSystem));
                }

                SqliteRollbackJournal.Commit(
                    _fileSystem,
                    _journalPath,
                    _pageStore,
                    [1],
                    () =>
                    {
                        _pageStore.WritePage(1, pageOne);
                        _pageStore.Flush();
                    });

                _journalMode = journalMode;
                if (journalMode == SqliteJournalMode.Wal)
                {
                    _wal = createdWal;
                    createdWal = null;
                    _recoveryInfo = RequireWal().ScanRecovery();
                    _visibleRecoveryInfo = _recoveryInfo;
                }
                else
                {
                    _wal?.Dispose();
                    _wal = null;
                    _recoveryInfo = CreateEmptyRecoveryInfo();
                    _visibleRecoveryInfo = _recoveryInfo;
                    TryDeleteCreatedArtifact(_fileSystem, _walPath);
                }

                _walPageOverlay.Clear();
                _pageCache.Clear();
                _committedFrameCount = 0;
                _lockGeneration = checkpointLock.PublishStorageChange();
                return _journalMode;
            }
            catch
            {
                createdWal?.Dispose();
                if (_journalMode == SqliteJournalMode.Delete)
                    TryDeleteCreatedArtifact(_fileSystem, _walPath);
                _lockGeneration = checkpointLock.PublishStorageChange();
                TransitionToFaulted();
                throw;
            }
        }
    }

    internal void ReplaceDatabaseFile(string replacementPath, TimeSpan? busyTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(replacementPath);
        using var checkpointLock = _lockManager.EnterCheckpoint(ResolveBusyTimeout(busyTimeout));
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            SynchronizeCommittedView();
            if (_journalMode != SqliteJournalMode.Delete)
                throw new InvalidOperationException("SQLite database replacement requires DELETE journal mode.");
            if (_state != SqlitePagerState.Ready)
                throw new InvalidOperationException($"Cannot replace the SQLite database while the pager is {_state}.");

            using var replacement = _fileSystem.OpenFile(replacementPath, FileOpenMode.OpenExisting, readOnly: true);
            var pages = Enumerable.Range(1, checked((int)_pageStore.PageCount))
                .Select(pageNumber => checked((uint)pageNumber))
                .ToArray();
            try
            {
                SqliteRollbackJournal.Commit(
                    _fileSystem,
                    _journalPath,
                    _pageStore,
                    pages,
                    () => _pageStore.ReplaceRawContent(replacement));
                _lockGeneration = checkpointLock.PublishStorageChange();
                TransitionToFaulted();
            }
            catch
            {
                _lockGeneration = checkpointLock.PublishStorageChange();
                TransitionToFaulted();
                throw;
            }
        }
    }

    private SqliteCheckpointResult CheckpointToMainStoreCore(
        TimeSpan? busyTimeout,
        bool resetCommittedWal)
    {
        using var checkpointLock = _lockManager.EnterCheckpoint(ResolveBusyTimeout(busyTimeout));
        return CheckpointToMainStoreUnderLock(checkpointLock, resetCommittedWal);
    }

    private SqliteCheckpointResult CheckpointToMainStoreUnderLock(
        SqlitePagerLockLease checkpointLock,
        bool resetCommittedWal)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            SynchronizeCommittedView();
            if (_journalMode == SqliteJournalMode.Delete)
            {
                if (_state != SqlitePagerState.Ready)
                    throw new InvalidOperationException($"Cannot checkpoint while the SQLite pager is {_state}.");
                return new SqliteCheckpointResult(_committedPageCount, 0, 0);
            }
            if (HasUncommittedOrInvalidTail(_recoveryInfo))
            {
                throw new InvalidOperationException(
                    "Cannot checkpoint a SQLite WAL with an uncommitted or invalid tail; recover it under the writer lock first.");
            }
            if (_state != SqlitePagerState.Ready)
                throw new InvalidOperationException($"Cannot checkpoint while the SQLite pager is {_state}.");
            _state = SqlitePagerState.Checkpointing;
            try
            {
                ValidateWalHasNotChanged();
                var originalStorePageCount = _pageStore.PageCount;
                var installedPageCount = 0;

                for (var pageNumber = originalStorePageCount + 1;
                     pageNumber <= _committedPageCount;
                     pageNumber++)
                {
                    if (!_walPageOverlay.TryGetValue(pageNumber, out var page))
                    {
                        throw new InvalidDataException(
                            $"Committed WAL view is missing required appended page {pageNumber}.");
                    }

                    _pageStore.WritePage(pageNumber, page);
                    installedPageCount++;
                    if (pageNumber == uint.MaxValue)
                        break;
                }

                foreach (var pageNumber in _walPageOverlay.Keys
                             .Where(pageNumber => pageNumber <= Math.Min(originalStorePageCount, _committedPageCount)
                                                 && pageNumber != 1)
                             .OrderBy(pageNumber => pageNumber))
                {
                    _pageStore.WritePage(pageNumber, _walPageOverlay[pageNumber]);
                    installedPageCount++;
                }

                if (_committedPageCount < originalStorePageCount)
                    ValidateShrinkCheckpointPageOne();

                if (_walPageOverlay.TryGetValue(1, out var firstPage))
                {
                    if (_committedPageCount < originalStorePageCount)
                        _pageStore.WriteShrinkCheckpointPageOne(firstPage);
                    else
                        _pageStore.WritePage(1, firstPage);
                    installedPageCount++;
                }

                _pageStore.Flush();
                if (_committedPageCount < originalStorePageCount)
                {
                    _pageStore.TruncateToPageCount(_committedPageCount);
                    _pageStore.Flush();
                }

                var retainedCommittedFrameCount = _committedFrameCount;
                if (resetCommittedWal)
                {
                    if (_pageStore.PageCount != _committedPageCount)
                    {
                        throw new InvalidDataException(
                            "Cannot reset a SQLite WAL before the main database file reaches the committed page count.");
                    }

                    // The exclusive checkpoint lease excludes managed readers and
                    // writers, but validate again before a destructive reset so a
                    // bypassing writer cannot lose frames it appended meanwhile.
                    ValidateWalHasNotChanged();
                    RequireWal().ResetAfterDurableCheckpoint(CanPublishCheckpointedRecoveryMarker());
                    _walPageOverlay.Clear();
                    _committedFrameCount = 0;
                    _recoveryInfo = CreateEmptyRecoveryInfo();
                    _visibleRecoveryInfo = CreateRecoveryVisibleInfo(_recoveryInfo);
                    retainedCommittedFrameCount = 0;
                }

                _pageCache.Clear();
                _lockGeneration = checkpointLock.PublishStorageChange();
                _state = SqlitePagerState.Ready;
                return new SqliteCheckpointResult(
                    _committedPageCount,
                    installedPageCount,
                    retainedCommittedFrameCount);
            }
            catch
            {
                TransitionToFaulted();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqlitePagerTransaction? transaction;
        SqlitePagerReadTransaction[] readers;
        try
        {
            lock (_gate)
            {
                if (_state == SqlitePagerState.Disposed)
                    return;

                transaction = _activeTransaction;
                readers = [.. _activeReadTransactions];
                _activeTransaction = null;
                _activeReadTransactions.Clear();
                _state = SqlitePagerState.Disposed;
                _wal?.Dispose();
                _pageStore.Dispose();
            }
        }
        finally
        {
            _lockManager.ReleaseRetainedSharedReaderLock();
            _clientOwnership?.Dispose();
        }

        transaction?.AbortFromPagerDispose();
        foreach (var reader in readers)
            reader.InvalidateFromPagerDispose();
    }

    internal void CommitTransaction(SqlitePagerTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state != SqlitePagerState.TransactionActive || _activeTransaction != transaction)
                throw new InvalidOperationException("This SQLite pager transaction is not active.");

            ValidateTransaction(transaction);

            try
            {
                if (_journalMode == SqliteJournalMode.Delete)
                {
                    CommitRollbackTransaction(transaction);
                    _lockGeneration = transaction.PublishStorageChange();
                    _activeTransaction = null;
                    _state = SqlitePagerState.Ready;
                    transaction.ReleaseWriterLock();
                    return;
                }

                var wal = RequireWal();
                ValidateWalHasNotChanged();
                for (var index = 0; index < transaction.WriteOrder.Count; index++)
                {
                    var pageNumber = transaction.WriteOrder[index];
                    var databaseSizeInPages = index == transaction.WriteOrder.Count - 1
                        ? transaction.TargetDatabaseSizeInPages
                        : 0;
                    wal.AppendFrame(pageNumber, transaction.GetPageImage(pageNumber), databaseSizeInPages);
                }

                wal.Flush();
                var recovery = wal.ScanRecovery();
                if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
                    || recovery.LastCommittedFrameNumber != recovery.LastValidFrameNumber
                    || recovery.LastCommittedDatabaseSizeInPages != transaction.TargetDatabaseSizeInPages)
                {
                    throw new InvalidDataException("SQLite WAL did not preserve the transaction commit boundary.");
                }

                PublishCommittedTransaction(transaction, recovery);
                _lockGeneration = transaction.PublishStorageChange();
                _activeTransaction = null;
                _state = SqlitePagerState.Ready;
                if (transaction.CheckpointWalAfterCommit)
                {
                    _ = CheckpointToMainStoreUnderLock(
                        transaction.TransactionLock,
                        resetCommittedWal: true);
                }
                transaction.ReleaseWriterLock();
            }
            catch
            {
                _lockGeneration = transaction.PublishStorageChange();
                TransitionToFaulted();
                transaction.ReleaseWriterLock();
                throw;
            }
        }
    }

    private void CommitRollbackTransaction(SqlitePagerTransaction transaction)
    {
        var originalPageCount = _committedPageCount;
        var pagesToJournal = new HashSet<uint>(
            transaction.WriteOrder.Where(pageNumber => pageNumber <= originalPageCount));
        if (transaction.TargetDatabaseSizeInPages > originalPageCount)
            pagesToJournal.Add(1);
        if (transaction.TargetDatabaseSizeInPages < originalPageCount)
        {
            for (var pageNumber = transaction.TargetDatabaseSizeInPages + 1;
                 pageNumber <= originalPageCount;
                 pageNumber++)
            {
                pagesToJournal.Add(pageNumber);
                if (pageNumber == uint.MaxValue)
                    break;
            }
        }

        SqliteRollbackJournal.Commit(
            _fileSystem,
            _journalPath,
            _pageStore,
            pagesToJournal,
            () =>
            {
                foreach (var pageNumber in transaction.WriteOrder
                             .Where(pageNumber => pageNumber != 1 && pageNumber <= originalPageCount)
                             .OrderBy(pageNumber => pageNumber))
                {
                    _pageStore.WritePage(pageNumber, transaction.GetPageImage(pageNumber));
                }

                foreach (var pageNumber in transaction.WriteOrder
                             .Where(pageNumber => pageNumber > originalPageCount)
                             .OrderBy(pageNumber => pageNumber))
                {
                    _pageStore.WritePage(pageNumber, transaction.GetPageImage(pageNumber));
                }

                if (transaction.PageImages.TryGetValue(1, out var pageOne))
                {
                    if (transaction.TargetDatabaseSizeInPages < originalPageCount)
                        _pageStore.WriteShrinkCheckpointPageOne(pageOne);
                    else
                        _pageStore.WritePage(1, pageOne);
                }

                _pageStore.Flush();
                if (transaction.TargetDatabaseSizeInPages < originalPageCount)
                {
                    _pageStore.TruncateToPageCount(transaction.TargetDatabaseSizeInPages);
                    _pageStore.Flush();
                }
            });

        _committedPageCount = transaction.TargetDatabaseSizeInPages;
        _committedFrameCount = 0;
        _recoveryInfo = CreateEmptyRecoveryInfo();
        _visibleRecoveryInfo = _recoveryInfo;
        _walPageOverlay.Clear();
        _pageCache.Clear();
    }

    internal void RollbackTransaction(SqlitePagerTransaction transaction)
    {
        lock (_gate)
        {
            if (_state == SqlitePagerState.TransactionActive && _activeTransaction == transaction)
            {
                _activeTransaction = null;
                _state = SqlitePagerState.Ready;
                transaction.ReleaseWriterLock();
            }
            else if (_state == SqlitePagerState.Faulted)
            {
                transaction.ReleaseWriterLock();
            }
        }
    }

    internal byte[] ReadSnapshotPage(
        IReadOnlyDictionary<uint, byte[]> walPageOverlay,
        uint pageCount,
        long cacheGeneration,
        uint pageNumber)
    {
        lock (_gate)
        {
            ThrowIfNotReadable();
            if (pageNumber == 0 || pageNumber > pageCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageNumber),
                    pageNumber,
                    $"Page number is out of range for snapshot database size {pageCount}.");
            }

            if (walPageOverlay.TryGetValue(pageNumber, out var walPage))
                return [.. walPage];

            try
            {
                // The copied overlay is the snapshot authority; only a matching
                // clean-main-store generation may be consulted after it.
                if (_pageCache.TryGetValue(pageNumber, cacheGeneration, out var cachedPage))
                    return [.. cachedPage];

                if (pageNumber > _pageStore.PageCount)
                {
                    throw new InvalidDataException(
                        $"Snapshot page {pageNumber} is absent from both the WAL overlay and main database file.");
                }

                var page = _pageStore.ReadPage(pageNumber);
                _pageCache.Add(pageNumber, cacheGeneration, page);
                return [.. page];
            }
            catch
            {
                TransitionToFaulted();
                throw;
            }
        }
    }

    internal void EndReadTransaction(SqlitePagerReadTransaction transaction)
    {
        lock (_gate)
            _activeReadTransactions.Remove(transaction);
    }

    private void ReadCommittedPageCore(uint pageNumber, Span<byte> destination)
    {
        if (destination.Length != _pageStore.PageSize)
            throw new ArgumentException($"Destination must be exactly {_pageStore.PageSize} bytes.", nameof(destination));
        ValidateVisiblePageNumber(pageNumber);
        try
        {
            GetCommittedPageImage(pageNumber).CopyTo(destination);
        }
        catch
        {
            TransitionToFaulted();
            throw;
        }
    }

    /// <summary>
    /// Whether an unchanged lock generation is insufficient proof that this
    /// pager's committed view is current.
    /// </summary>
    /// <remarks>
    /// A physical database holds SQLite's complete main-file lock-byte range
    /// exclusively for the lifetime of every pager in this process, so no
    /// ordinary SQLite client and no other managed process can change durable
    /// storage. Every in-process commit, checkpoint, and recovery publishes a
    /// new lock generation, so an unchanged generation proves the committed
    /// view still matches durable storage. A file-backed lock coordinator
    /// without that ownership carrier has no such proof and must rescan.
    /// </remarks>
    private bool RequiresSharedStorageRescan
        => _foreignReadOnly || (_lockManager.UsesFileBackedWalLocks && _clientOwnership is null);

    /// <summary>
    /// The shared per-file storage generation, published after any WAL commit or
    /// checkpoint by any connection on the file. A cheap race-free signal that the
    /// committed view may have changed: reading it takes the lock-manager gate
    /// briefly and never rescans the WAL. See <see cref="SqlitePagerLockManager.Generation"/>.
    /// </summary>
    internal long CommittedViewGeneration => _lockManager.Generation;

    /// <summary>
    /// Captures a token identifying the pager's current committed view after a
    /// fresh rescan. Foreign read-only callers compare tokens across statement
    /// boundaries to detect durable changes made by the database owner. WAL
    /// commits change the frame count or salts, but a checkpoint can rewrite
    /// the main file in place without touching any header field, so the token
    /// also carries the on-disk write stamps of both files.
    /// </summary>
    internal SqlitePagerViewToken CaptureCommittedViewToken()
    {
        lock (_gate)
        {
            SynchronizeCommittedView();
            var pageOne = new byte[_pageStore.PageSize];
            ReadCommittedPageCore(1, pageOne);
            var header = SqliteDatabaseHeader.Parse(pageOne);
            return new SqlitePagerViewToken(
                header.ChangeCounter,
                _committedFrameCount,
                _wal?.Header.Salt1 ?? 0,
                _wal?.Header.Salt2 ?? 0,
                _committedPageCount,
                _fileSystem.GetWriteStamp(_databasePath),
                _fileSystem.GetWriteStamp(_walPath));
        }
    }

    /// <summary>
    /// The number of times this pager rebuilt its committed view from durable
    /// storage. Reads that reuse an unchanged view do not increment it.
    /// </summary>
    internal long CommittedViewRescanCount { get; private set; }

    private void SynchronizeCommittedView()
    {
        try
        {
            var generation = _lockManager.Generation;
            if (_lockGeneration == generation && !RequiresSharedStorageRescan)
                return;

            CommittedViewRescanCount++;
            if (SqliteRollbackJournal.IsHot(_fileSystem, _journalPath))
            {
                throw new InvalidDataException(
                    "SQLite database has a hot rollback journal; dispose and reopen it writable to recover.");
            }
            if (_lockGeneration != generation)
                ValidateMainFileFormat();

            if (_journalMode == SqliteJournalMode.Delete)
            {
                _pageStore.RefreshHeader();
                _committedPageCount = _pageStore.PageCount;
                _walPageOverlay.Clear();
                _pageCache.Clear();
                _recoveryInfo = CreateEmptyRecoveryInfo();
                _visibleRecoveryInfo = _recoveryInfo;
                _lockGeneration = generation;
                return;
            }
            if (_foreignReadOnly)
                ReconcileForeignWalIncarnation();

            if (_wal is null)
            {
                _pageStore.RefreshHeader();
                InitializeCleanWalView();
                _lockGeneration = generation;
                return;
            }

            var recovery = RequireWal().ScanRecovery();
            if (!_foreignReadOnly
                && !_lockManager.UsesFileBackedWalLocks
                && HasUncommittedOrInvalidTail(recovery))
            {
                throw new InvalidDataException(
                    "SQLite WAL changed outside the process-local pager lock state; reopen and recover before continuing.");
            }

            InitializeCommittedView(recovery);
            _lockGeneration = generation;
        }
        catch
        {
            TransitionToFaulted();
            throw;
        }
    }

    private void ValidateMainFileFormat()
    {
        var header = SqliteDatabaseHeader.Parse(_pageStore.ReadPage(1));
        if (header.PageSize != _pageStore.PageSize)
        {
            throw new InvalidDataException(
                "SQLite database page size changed while this pager was open; dispose and reopen it.");
        }

        var expectedVersion = _journalMode == SqliteJournalMode.Wal
            ? SqliteFileFormatVersion.Wal
            : SqliteFileFormatVersion.Legacy;
        if (header.WriteVersion != expectedVersion || header.ReadVersion != expectedVersion)
        {
            throw new InvalidDataException(
                "SQLite journal mode changed while this pager was open; dispose and reopen it.");
        }
        if (_journalMode == SqliteJournalMode.Wal)
            ValidateWalIncarnation();
    }

    private void ValidateWalIncarnation()
    {
        if (_wal is null || !_fileSystem.FileExists(_walPath))
        {
            throw new InvalidDataException(
                "SQLite WAL storage changed while this pager was open; dispose and reopen it.");
        }

        using var currentWal = SqliteWalFile.Open(
            _fileSystem,
            _walPath,
            readOnly: true,
            GetFileSystemEncryption(_fileSystem));
        if (currentWal.Header.Salt1 != _wal.Header.Salt1
            || currentWal.Header.Salt2 != _wal.Header.Salt2)
        {
            throw new InvalidDataException(
                "SQLite WAL storage changed while this pager was open; dispose and reopen it.");
        }
    }

    /// <summary>
    /// A foreign reader shares no lock state with the database owner, so the owner
    /// may legitimately recycle (checkpoint and reset) or remove the WAL between
    /// rescans. Rather than faulting like <see cref="ValidateWalIncarnation"/>,
    /// adopt the current WAL incarnation — or the fully checkpointed main file
    /// when the WAL is gone — exactly as a freshly opened SQLite read-only
    /// connection would.
    /// </summary>
    private void ReconcileForeignWalIncarnation()
    {
        if (!_fileSystem.FileExists(_walPath))
        {
            if (_wal is not null)
            {
                _wal.Dispose();
                _wal = null;
            }

            return;
        }

        if (_wal is not null)
        {
            using var currentWal = SqliteWalFile.Open(
                _fileSystem,
                _walPath,
                readOnly: true,
                GetFileSystemEncryption(_fileSystem));
            if (currentWal.Header.Salt1 == _wal.Header.Salt1
                && currentWal.Header.Salt2 == _wal.Header.Salt2)
            {
                return;
            }

            _wal.Dispose();
            _wal = null;
        }

        _wal = SqliteWalFile.Open(
            _fileSystem,
            _walPath,
            readOnly: true,
            GetFileSystemEncryption(_fileSystem));
    }

    private void RecoverUncommittedTailUnderWriterLock(SqlitePagerLockLease writerLock)
    {
        if (_journalMode != SqliteJournalMode.Wal)
            return;
        if (!_lockManager.UsesFileBackedWalLocks || !HasUncommittedOrInvalidTail(_recoveryInfo))
            return;

        var wal = RequireWal();
        wal.RecoverToLastCommittedFrame();
        var recovery = wal.ScanRecovery();
        if (HasUncommittedOrInvalidTail(recovery))
            throw new InvalidDataException("SQLite WAL recovery did not remove its uncommitted or invalid tail.");

        InitializeCommittedView(recovery);
        _lockGeneration = writerLock.PublishStorageChange();
    }

    private static bool HasUncommittedOrInvalidTail(SqliteWalRecoveryInfo recovery)
        => recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
           || recovery.LastCommittedFrameNumber != recovery.LastValidFrameNumber;

    private static IFileSystem CreateStorageFileSystem(IFileSystem fileSystem)
        => fileSystem switch
        {
            AhtolaEncryptionFileSystem encrypted when encrypted.Inner is PhysicalFileSystem physicalFileSystem
                => encrypted.WithInner(new SqlitePagerPhysicalFileSystem(physicalFileSystem)),
            PhysicalFileSystem physicalFileSystem => new SqlitePagerPhysicalFileSystem(physicalFileSystem),
            _ => fileSystem,
        };

    private static void TryDeleteCreatedArtifact(IFileSystem fileSystem, string path)
    {
        try
        {
            fileSystem.DeleteFile(path);
        }
        catch
        {
        }
    }

    private static AhtolaEncryptionOptions? GetFileSystemEncryption(IFileSystem fileSystem)
        => fileSystem is AhtolaEncryptionFileSystem encrypted ? encrypted.Encryption : null;

    private static SqliteWalRecoveryInfo CreateEmptyRecoveryInfo()
        => new(
            LastValidFrameNumber: 0,
            LastCommittedFrameNumber: 0,
            LastCommittedDatabaseSizeInPages: 0,
            LastCommittedByteLength: SqliteWalHeader.Size,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile);

    private void InitializeRollbackView()
    {
        var header = _pageStore.Header;
        if (header.WriteVersion != SqliteFileFormatVersion.Legacy
            || header.ReadVersion != SqliteFileFormatVersion.Legacy)
        {
            throw new InvalidDataException(
                "A SQLite rollback-journal pager requires legacy read and write format versions.");
        }
        if (header.VersionValidFor == header.ChangeCounter
            && header.DatabaseSizeInPages != 0
            && header.DatabaseSizeInPages != _pageStore.PageCount)
        {
            throw new InvalidDataException(
                "SQLite rollback-journal database header page count does not match the main file.");
        }

        _committedPageCount = _pageStore.PageCount;
        _committedFrameCount = 0;
        _walPageOverlay.Clear();
        _pageCache.Clear();
        _recoveryInfo = CreateEmptyRecoveryInfo();
        _visibleRecoveryInfo = _recoveryInfo;
    }

    private void InitializeCleanWalView()
    {
        var header = _pageStore.Header;
        if (header.WriteVersion != SqliteFileFormatVersion.Wal
            || header.ReadVersion != SqliteFileFormatVersion.Wal)
        {
            throw new InvalidDataException("A clean SQLite WAL view requires WAL read and write format versions.");
        }
        if (header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != _pageStore.PageCount)
        {
            throw new InvalidDataException(
                "A SQLite WAL database without a WAL file must have an authoritative main-database header.");
        }

        _committedPageCount = _pageStore.PageCount;
        _committedFrameCount = 0;
        _walPageOverlay.Clear();
        _pageCache.Clear();
        _recoveryInfo = CreateEmptyRecoveryInfo();
        _visibleRecoveryInfo = _recoveryInfo;
    }

    private void InitializeCommittedView(SqliteWalRecoveryInfo recovery)
    {
        var wal = RequireWal();
        ValidateStoragePair();
        _recoveryInfo = recovery;
        _committedFrameCount = recovery.LastCommittedFrameNumber;
        _committedPageCount = _pageStore.PageCount;
        _walPageOverlay.Clear();
        _pageCache.Clear();

        var transactionPages = new Dictionary<uint, byte[]>();
        var finalTransactionHasPageOne = false;
        for (var frameNumber = 1L; frameNumber <= recovery.LastCommittedFrameNumber; frameNumber++)
        {
            var frame = wal.ReadFrame(frameNumber);
            transactionPages[frame.Header.PageNumber] = frame.PageData;
            if (!frame.Header.IsCommit)
                continue;

            ValidateRecoveredTransaction(transactionPages, frame.Header.DatabaseSizeInPages);
            if (frameNumber == recovery.LastCommittedFrameNumber)
                finalTransactionHasPageOne = transactionPages.ContainsKey(1);
            PublishRecoveredTransaction(transactionPages, frame.Header.DatabaseSizeInPages);
            transactionPages.Clear();
        }

        if (transactionPages.Count != 0)
            throw new InvalidDataException("SQLite WAL recovery stopped before a reported committed transaction boundary.");

        ValidateTrailingMainDatabasePages(recovery, finalTransactionHasPageOne);
        _visibleRecoveryInfo = CreateRecoveryVisibleInfo(recovery);
    }

    private SqliteWalRecoveryInfo CreateRecoveryVisibleInfo(SqliteWalRecoveryInfo recovery)
    {
        if (recovery.LastValidFrameNumber != 0
            || recovery.LastCommittedFrameNumber != 0
            || recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || !RequireWal().HasCheckpointedRecoveryMarker)
        {
            return recovery;
        }

        var header = _pageStore.Header;
        var pageCount = _pageStore.PageCount;
        if (pageCount == 0
            || header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != pageCount)
        {
            throw new InvalidDataException(
                "SQLite WAL checkpoint recovery marker does not have an authoritative durable main-database state.");
        }

        return new SqliteWalRecoveryInfo(
            LastValidFrameNumber: 0,
            LastCommittedFrameNumber: 1,
            LastCommittedDatabaseSizeInPages: pageCount,
            LastCommittedByteLength: SqliteWalHeader.Size,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile);
    }

    private bool CanPublishCheckpointedRecoveryMarker()
    {
        var header = _pageStore.Header;
        return _walPageOverlay.ContainsKey(1)
               && header.VersionValidFor == header.ChangeCounter
               && header.DatabaseSizeInPages == _committedPageCount;
    }

    private void ValidateStoragePair()
    {
        var wal = RequireWal();
        if (_pageStore.PageSize != wal.PageSize)
            throw new InvalidDataException("SQLite database and WAL page sizes do not match.");
        if (_pageStore.Header.WriteVersion != SqliteFileFormatVersion.Wal
            || _pageStore.Header.ReadVersion != SqliteFileFormatVersion.Wal)
        {
            throw new InvalidDataException("A SQLite WAL overlay requires WAL read and write format versions.");
        }
    }

    private void ValidateRecoveredTransaction(
        IReadOnlyDictionary<uint, byte[]> transactionPages,
        uint targetDatabaseSizeInPages)
    {
        if (targetDatabaseSizeInPages == 0)
            throw new InvalidDataException("SQLite WAL commit frame has a zero database size.");

        foreach (var pageNumber in transactionPages.Keys)
        {
            if (pageNumber > targetDatabaseSizeInPages)
            {
                throw new InvalidDataException(
                    $"SQLite WAL transaction writes page {pageNumber} beyond committed database size {targetDatabaseSizeInPages}.");
            }
        }

        ValidatePageOneImage(transactionPages, targetDatabaseSizeInPages);
    }

    private void PublishRecoveredTransaction(
        IReadOnlyDictionary<uint, byte[]> transactionPages,
        uint targetDatabaseSizeInPages)
    {
        foreach (var pageNumber in _walPageOverlay.Keys
                     .Where(pageNumber => pageNumber > targetDatabaseSizeInPages)
                     .ToArray())
        {
            _walPageOverlay.Remove(pageNumber);
            _pageCache.Remove(pageNumber);
        }

        foreach (var (pageNumber, page) in transactionPages)
            _walPageOverlay[pageNumber] = page;

        _committedPageCount = targetDatabaseSizeInPages;
        ValidateVisiblePageSources();
    }

    private void ValidateTransaction(SqlitePagerTransaction transaction)
    {
        if (transaction.WriteOrder.Count == 0)
            throw new InvalidOperationException("A SQLite WAL transaction must contain at least one complete page image.");

        foreach (var pageNumber in transaction.WriteOrder)
        {
            if (pageNumber == 0 || pageNumber > transaction.TargetDatabaseSizeInPages)
            {
                throw new InvalidOperationException(
                    $"SQLite WAL transaction page {pageNumber} is outside its committed database size.");
            }
        }

        ValidatePageOneImage(transaction.PageImages, transaction.TargetDatabaseSizeInPages);

        if (transaction.TargetDatabaseSizeInPages < _committedPageCount)
            ValidateShrinkTransactionPageOne(transaction.PageImages, transaction.TargetDatabaseSizeInPages);

        if (transaction.TargetDatabaseSizeInPages <= _committedPageCount)
            return;

        var requiredNewPageCount = (ulong)transaction.TargetDatabaseSizeInPages - _committedPageCount;
        var providedNewPageCount = transaction.WriteOrder.Count(
            pageNumber => pageNumber > _committedPageCount
                          && pageNumber <= transaction.TargetDatabaseSizeInPages);
        if ((ulong)providedNewPageCount != requiredNewPageCount)
        {
            throw new InvalidOperationException(
                "Every newly committed SQLite page must have an explicit page image in the WAL transaction.");
        }
    }

    private void PublishCommittedTransaction(
        SqlitePagerTransaction transaction,
        SqliteWalRecoveryInfo recovery)
    {
        foreach (var pageNumber in transaction.WriteOrder)
        {
            var image = transaction.GetPageImage(pageNumber).ToArray();
            _walPageOverlay[pageNumber] = image;
            _pageCache.Remove(pageNumber);
        }

        _committedPageCount = transaction.TargetDatabaseSizeInPages;
        _committedFrameCount = recovery.LastCommittedFrameNumber;
        _recoveryInfo = recovery;
        _visibleRecoveryInfo = recovery;
        ValidateVisiblePageSources();
    }

    private void ValidatePageOneImage(
        IReadOnlyDictionary<uint, byte[]> transactionPages,
        uint targetDatabaseSizeInPages)
    {
        if (!transactionPages.TryGetValue(1, out var pageOne))
            return;

        var header = SqliteDatabaseHeader.Parse(pageOne);
        if (header.PageSize != _pageStore.PageSize)
            throw new InvalidDataException("SQLite WAL page 1 changes the database page size.");
        var expectedVersion = _journalMode == SqliteJournalMode.Wal
            ? SqliteFileFormatVersion.Wal
            : SqliteFileFormatVersion.Legacy;
        if (header.WriteVersion != expectedVersion || header.ReadVersion != expectedVersion)
        {
            throw new InvalidDataException(
                $"SQLite transaction page 1 does not match the active {_journalMode} journal mode.");
        }
        if (header.VersionValidFor == header.ChangeCounter
            && header.DatabaseSizeInPages != 0
            && header.DatabaseSizeInPages != targetDatabaseSizeInPages)
        {
            throw new InvalidDataException(
                "SQLite WAL page 1 has an authoritative page count different from its commit frame.");
        }
    }

    private void ValidateShrinkTransactionPageOne(
        IReadOnlyDictionary<uint, byte[]> transactionPages,
        uint targetDatabaseSizeInPages)
    {
        if (!transactionPages.TryGetValue(1, out var pageOne))
        {
            throw new InvalidOperationException(
                "A database-shrinking SQLite WAL transaction must rewrite page 1 with the new authoritative page count.");
        }

        var header = SqliteDatabaseHeader.Parse(pageOne);
        if (header.VersionValidFor != header.ChangeCounter || header.DatabaseSizeInPages != targetDatabaseSizeInPages)
        {
            throw new InvalidDataException(
                "A database-shrinking SQLite WAL transaction must make page 1's page count authoritative and equal to its commit frame.");
        }
    }

    private void ValidateShrinkCheckpointPageOne()
    {
        if (!_walPageOverlay.TryGetValue(1, out var pageOne))
        {
            throw new InvalidDataException(
                "Cannot checkpoint a database-shrinking WAL view without its committed page 1 image.");
        }

        var header = SqliteDatabaseHeader.Parse(pageOne);
        if (header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != _committedPageCount)
        {
            throw new InvalidDataException(
                "Cannot checkpoint a database-shrinking WAL view whose page 1 does not authoritatively declare the committed size.");
        }
    }

    private void ValidateTrailingMainDatabasePages(
        SqliteWalRecoveryInfo recovery,
        bool finalTransactionHasPageOne)
    {
        if (_pageStore.PageCount <= _committedPageCount)
        {
            var header = _pageStore.Header;
            if (header.VersionValidFor == header.ChangeCounter
                && header.DatabaseSizeInPages != 0
                && header.DatabaseSizeInPages < _pageStore.PageCount)
            {
                throw new InvalidDataException(
                    "SQLite database header declares a smaller authoritative size without a recoverable shrinking WAL commit.");
            }

            return;
        }

        if (recovery.LastCommittedFrameNumber == 0
            || !finalTransactionHasPageOne
            || !_walPageOverlay.TryGetValue(1, out var pageOne))
        {
            throw new InvalidDataException(
                "SQLite database has pages beyond its authoritative size without a recoverable shrinking WAL commit.");
        }

        var mainHeader = _pageStore.Header;
        var walHeader = SqliteDatabaseHeader.Parse(pageOne);
        if (walHeader.VersionValidFor != walHeader.ChangeCounter
            || walHeader.DatabaseSizeInPages != _committedPageCount)
        {
            throw new InvalidDataException(
                "SQLite database has pages beyond its authoritative size, but its retained WAL does not contain the shrinking transaction's authoritative page 1.");
        }

        // Before page 1 is installed the main database still names its original
        // physical size. Once page 1 is durable, it must exactly match the
        // retained WAL. No third state is safe to expose.
        if (mainHeader.VersionValidFor == mainHeader.ChangeCounter
            && mainHeader.DatabaseSizeInPages == _pageStore.PageCount)
        {
            return;
        }
        if (mainHeader.DatabaseSizeInPages != _committedPageCount || walHeader != mainHeader)
        {
            throw new InvalidDataException(
                "SQLite database has pages beyond its authoritative size, but its retained WAL does not prove a matching interrupted shrink checkpoint.");
        }
    }

    private void ValidateVisiblePageSources()
    {
        if (_committedPageCount <= _pageStore.PageCount)
            return;

        var requiredOverlayPageCount = (ulong)_committedPageCount - _pageStore.PageCount;
        var availableOverlayPageCount = _walPageOverlay.Keys.LongCount(
            pageNumber => pageNumber > _pageStore.PageCount && pageNumber <= _committedPageCount);
        if ((ulong)availableOverlayPageCount != requiredOverlayPageCount)
        {
            throw new InvalidDataException(
                "SQLite WAL commit declares appended pages that are absent from both the WAL and main database file.");
        }
    }

    private void ValidateWalHasNotChanged()
    {
        var recovery = RequireWal().ScanRecovery();
        if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || recovery.LastValidFrameNumber != _committedFrameCount
            || recovery.LastCommittedFrameNumber != _committedFrameCount
            || (recovery.LastCommittedFrameNumber != 0
                && recovery.LastCommittedDatabaseSizeInPages != _committedPageCount))
        {
            throw new InvalidDataException(
                "SQLite WAL changed outside this pager; reopen and recover before checkpointing.");
        }
    }

    private byte[] GetCommittedPageImage(uint pageNumber)
    {
        if (_walPageOverlay.TryGetValue(pageNumber, out var walPage))
            return walPage;
        if (_pageCache.TryGetValue(pageNumber, _lockGeneration, out var cachedPage))
            return cachedPage;
        if (pageNumber > _pageStore.PageCount)
        {
            throw new InvalidDataException(
                $"Committed SQLite page {pageNumber} is absent from both the WAL overlay and main database file.");
        }

        var page = _pageStore.ReadPage(pageNumber);
        _pageCache.Add(pageNumber, _lockGeneration, page);
        return page;
    }

    private void ValidateVisiblePageNumber(uint pageNumber)
    {
        if (pageNumber == 0 || pageNumber > _committedPageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Page number is out of range for committed database size {_committedPageCount}.");
        }
    }

    private void ThrowIfNotReadable()
    {
        ThrowIfDisposed();
        if (_state is not SqlitePagerState.Ready and not SqlitePagerState.TransactionActive)
            throw new InvalidOperationException($"Cannot read a committed SQLite pager view while the pager is {_state}.");
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new InvalidOperationException("The SQLite pager was opened read-only.");
    }

    private void TransitionToFaulted()
    {
        _activeTransaction = null;
        _pageCache.Clear();
        _state = SqlitePagerState.Faulted;
    }

    private TimeSpan ResolveBusyTimeout(TimeSpan? busyTimeout)
    {
        if (busyTimeout is { } timeout)
        {
            ValidateBusyTimeout(timeout, nameof(busyTimeout));
            return timeout;
        }

        lock (_gate)
            return _busyTimeout;
    }

    private static void ValidateBusyTimeout(TimeSpan? busyTimeout, string parameterName)
    {
        if (busyTimeout is { } timeout)
            ValidateBusyTimeout(timeout, parameterName);
    }

    private static void ValidateBusyTimeout(TimeSpan busyTimeout, string parameterName)
    {
        if (busyTimeout < TimeSpan.Zero && busyTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName, "Busy timeout must be non-negative or infinite.");
    }

    private static void ValidatePageCacheCapacity(int pageCacheCapacity, string parameterName)
        => ArgumentOutOfRangeException.ThrowIfLessThan(pageCacheCapacity, 1, parameterName);

    private void ThrowIfDisposed()
    {
        if (_state == SqlitePagerState.Disposed)
            throw new ObjectDisposedException(nameof(SqlitePager));
    }

    private SqliteWalFile RequireWal()
        => _wal ?? throw new InvalidOperationException("The SQLite pager does not have an open WAL.");
}

/// <summary>
/// A stable committed SQLite WAL snapshot. It remains valid across later WAL
/// commits and prevents checkpoint installation until it is disposed.
/// </summary>
public sealed class SqlitePagerReadTransaction : IDisposable
{
    private readonly object _gate = new();
    private readonly SqlitePager _pager;
    private readonly IReadOnlyDictionary<uint, byte[]> _walPageOverlay;
    private readonly long _cacheGeneration;
    private SqlitePagerLockLease? _readerLock;

    internal SqlitePagerReadTransaction(
        SqlitePager pager,
        SqlitePagerLockLease readerLock,
        uint pageCount,
        IReadOnlyDictionary<uint, byte[]> walPageOverlay,
        long cacheGeneration)
    {
        _pager = pager;
        _readerLock = readerLock;
        PageCount = pageCount;
        _walPageOverlay = walPageOverlay;
        _cacheGeneration = cacheGeneration;
    }

    /// <summary>The database size captured when this snapshot began.</summary>
    public uint PageCount { get; }

    /// <summary>Whether this read snapshot is still active.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _readerLock is not null;
        }
    }

    /// <summary>Reads a copy of one page from this transaction's snapshot.</summary>
    public byte[] ReadPage(uint pageNumber)
    {
        lock (_gate)
        {
            if (_readerLock is null)
                throw new ObjectDisposedException(nameof(SqlitePagerReadTransaction));

            return _pager.ReadSnapshotPage(_walPageOverlay, PageCount, _cacheGeneration, pageNumber);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqlitePagerLockLease? readerLock;
        lock (_gate)
        {
            readerLock = _readerLock;
            _readerLock = null;
            if (readerLock is null)
                return;

            _pager.EndReadTransaction(this);
            readerLock.Dispose();
        }
    }

    internal void InvalidateFromPagerDispose()
    {
        lock (_gate)
        {
            var readerLock = _readerLock;
            _readerLock = null;
            readerLock?.Dispose();
        }
    }
}

/// <summary>
/// An in-memory collection of page images that becomes visible only after its
/// final WAL frame and WAL flush succeed.
/// </summary>
public sealed class SqlitePagerTransaction : IDisposable
{
    private readonly object _gate = new();
    private readonly SqlitePager _pager;
    private SqlitePagerLockLease? _writerLock;
    private readonly Dictionary<uint, byte[]> _pageImages = [];
    private readonly List<uint> _writeOrder = [];
    private SqlitePagerTransactionState _state = SqlitePagerTransactionState.Active;

    internal SqlitePagerTransaction(
        SqlitePager pager,
        uint targetDatabaseSizeInPages,
        SqlitePagerLockLease writerLock,
        bool checkpointWalAfterCommit = false)
    {
        _pager = pager;
        _writerLock = writerLock;
        CheckpointWalAfterCommit = checkpointWalAfterCommit;
        TargetDatabaseSizeInPages = targetDatabaseSizeInPages;
    }

    /// <summary>The database size written into this transaction's commit frame.</summary>
    public uint TargetDatabaseSizeInPages { get; }

    /// <summary>The transaction's explicit lifecycle state.</summary>
    public SqlitePagerTransactionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    internal IReadOnlyDictionary<uint, byte[]> PageImages => _pageImages;

    internal IReadOnlyList<uint> WriteOrder => _writeOrder;

    internal bool CheckpointWalAfterCommit { get; }

    internal SqlitePagerLockLease TransactionLock
    {
        get
        {
            lock (_gate)
            {
                return _writerLock
                    ?? throw new InvalidOperationException("SQLite pager transaction no longer owns its lock.");
            }
        }
    }

    /// <summary>
    /// Stages a complete SQLite page image. Replacing a page retains its original
    /// WAL order and writes only the final image when the transaction commits.
    /// </summary>
    public void WritePage(uint pageNumber, ReadOnlySpan<byte> page)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            if (pageNumber == 0 || pageNumber > TargetDatabaseSizeInPages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageNumber),
                    pageNumber,
                    $"Page number must be between 1 and {TargetDatabaseSizeInPages}.");
            }
            if (page.Length != _pager.PageSize)
                throw new ArgumentException($"Page data must be exactly {_pager.PageSize} bytes.", nameof(page));

            if (!_pageImages.ContainsKey(pageNumber))
                _writeOrder.Add(pageNumber);
            _pageImages[pageNumber] = page.ToArray();
        }
    }

    /// <summary>
    /// Reads the transaction's latest staged page image, falling back to the
    /// pager's committed view when the page has not been written in this transaction.
    /// </summary>
    public byte[] ReadPage(uint pageNumber)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            if (_pageImages.TryGetValue(pageNumber, out var page))
                return [.. page];

            return _pager.ReadCommittedPage(pageNumber);
        }
    }

    /// <summary>
    /// Appends all staged images and makes them visible only after the WAL commit
    /// frame and WAL flush both complete.
    /// </summary>
    public void Commit()
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            try
            {
                _pager.CommitTransaction(this);
                _state = SqlitePagerTransactionState.Committed;
            }
            catch
            {
                if (_pager.State == SqlitePagerState.Faulted)
                {
                    _state = SqlitePagerTransactionState.Faulted;
                    ReleaseWriterLock();
                }
                throw;
            }
        }
    }

    /// <summary>Discards staged page images before any WAL frame is appended.</summary>
    public void Rollback()
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            _pageImages.Clear();
            _writeOrder.Clear();
            _pager.RollbackTransaction(this);
            _state = SqlitePagerTransactionState.RolledBack;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == SqlitePagerTransactionState.Active)
            {
                _pageImages.Clear();
                _writeOrder.Clear();
                _pager.RollbackTransaction(this);
                _state = SqlitePagerTransactionState.RolledBack;
            }
        }
    }

    internal byte[] GetPageImage(uint pageNumber)
    {
        if (!_pageImages.TryGetValue(pageNumber, out var page))
            throw new InvalidOperationException($"SQLite pager transaction has no image for page {pageNumber}.");

        return page;
    }

    internal long PublishStorageChange()
    {
        lock (_gate)
        {
            var writerLock = _writerLock
                ?? throw new InvalidOperationException("SQLite pager transaction no longer owns the writer lock.");
            return writerLock.PublishStorageChange();
        }
    }

    internal void ReleaseWriterLock()
    {
        lock (_gate)
        {
            var writerLock = _writerLock;
            _writerLock = null;
            writerLock?.Dispose();
        }
    }

    internal void AbortFromPagerDispose()
    {
        lock (_gate)
        {
            if (_state == SqlitePagerTransactionState.Active)
            {
                _pageImages.Clear();
                _writeOrder.Clear();
                _state = SqlitePagerTransactionState.RolledBack;
            }

            var writerLock = _writerLock;
            _writerLock = null;
            writerLock?.Dispose();
        }
    }

    private void ThrowIfNotActive()
    {
        if (_state != SqlitePagerTransactionState.Active)
            throw new InvalidOperationException($"SQLite pager transaction is {_state}.");
    }
}

/// <summary>
/// Identifies one committed pager view for change detection. See
/// <see cref="SqlitePager.CaptureCommittedViewToken"/>.
/// </summary>
internal readonly record struct SqlitePagerViewToken(
    uint ChangeCounter,
    long CommittedFrameCount,
    uint WalSalt1,
    uint WalSalt2,
    uint CommittedPageCount,
    FileWriteStamp? DatabaseStamp,
    FileWriteStamp? WalStamp);
