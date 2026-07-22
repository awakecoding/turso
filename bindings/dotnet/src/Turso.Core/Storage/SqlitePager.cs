using System.Diagnostics;

namespace Turso.Core.Storage;

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
/// <see cref="SqlitePagerLockManager"/>. Default physical-file managers
/// additionally hold SQLite's <c>-shm</c> lock bytes across managed processes
/// on Windows and Linux. Other platforms fail lock acquisition rather than
/// using process-local locks. They do not implement SQLite's WAL-index or
/// main-file shared lock, so this pager is not SQLite-client interoperable. It
/// never claims main-file writes are a multi-page atomic operation; only the
/// flushed WAL commit marker makes a transaction visible.
/// </remarks>
public sealed class SqlitePager : IDisposable
{
    /// <summary>
    /// Default maximum number of clean main-database page images retained by one
    /// pager instance.
    /// </summary>
    public const int DefaultPageCacheCapacity = 64;

    private readonly object _gate = new();
    private readonly SqlitePageStore _pageStore;
    private readonly SqliteWalFile _wal;
    private readonly SqlitePagerLockManager _lockManager;
    private readonly Dictionary<uint, byte[]> _walPageOverlay = [];
    private readonly SqlitePagerReadCache _pageCache;
    private readonly HashSet<SqlitePagerReadTransaction> _activeReadTransactions = [];
    private SqlitePagerTransaction? _activeTransaction;
    private SqliteWalRecoveryInfo _recoveryInfo;
    private SqliteWalRecoveryInfo _visibleRecoveryInfo;
    private uint _committedPageCount;
    private long _committedFrameCount;
    private long _lockGeneration;
    private SqlitePagerState _state;
    private TimeSpan _busyTimeout;

    private SqlitePager(
        SqlitePageStore pageStore,
        SqliteWalFile wal,
        SqlitePagerLockManager lockManager,
        int pageCacheCapacity)
    {
        _pageStore = pageStore;
        _wal = wal;
        _lockManager = lockManager;
        _pageCache = new SqlitePagerReadCache(pageCacheCapacity);
        _recoveryInfo = CreateEmptyRecoveryInfo();
        _visibleRecoveryInfo = CreateEmptyRecoveryInfo();
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
                return _pageStore.IsReadOnly || _wal.IsReadOnly;
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
        TursoEncryptionOptions? encryption = null,
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

        var effectiveLockManager = lockManager ?? SqlitePagerLockRegistry.Get(fileSystem, databasePath, walPath);
        var storageFileSystem = CreateStorageFileSystem(fileSystem);
        using var createLock = effectiveLockManager.EnterCheckpoint(busyTimeout);
        SqlitePageStore? pageStore = null;
        SqliteWalFile? wal = null;
        var databaseCreated = false;
        var walCreated = false;
        try
        {
            pageStore = SqlitePageStore.Create(
                storageFileSystem,
                databasePath,
                effectiveDatabaseHeader,
                encryption: encryption);
            databaseCreated = true;
            wal = SqliteWalFile.Create(storageFileSystem, walPath, walHeader, encryption);
            walCreated = true;

            var pager = new SqlitePager(pageStore, wal, effectiveLockManager, pageCacheCapacity);
            pager.InitializeCommittedView(wal.ScanRecovery());
            pager._lockGeneration = createLock.PublishStorageChange();
            pager._state = SqlitePagerState.Ready;
            pager._busyTimeout = busyTimeout ?? TimeSpan.Zero;
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
    }

    /// <summary>
    /// Opens a main database and WAL pair, rebuilding the visible page overlay
    /// from every valid transaction through the last commit marker.
    /// </summary>
    /// <remarks>
    /// Writable opens physically discard a corrupt, partial, or uncommitted WAL
    /// tail. Read-only opens expose the same recovered view but retain that tail.
    /// </remarks>
    public static SqlitePager Open(
        IFileSystem fileSystem,
        string databasePath,
        string walPath,
        bool readOnly = false,
        SqlitePagerLockManager? lockManager = null,
        TimeSpan? busyTimeout = null,
        TursoEncryptionOptions? encryption = null,
        int pageCacheCapacity = DefaultPageCacheCapacity)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(walPath);
        ValidateBusyTimeout(busyTimeout, nameof(busyTimeout));
        ValidatePageCacheCapacity(pageCacheCapacity, nameof(pageCacheCapacity));
        encryption ??= GetFileSystemEncryption(fileSystem);

        var effectiveLockManager = lockManager ?? SqlitePagerLockRegistry.Get(fileSystem, databasePath, walPath);
        var storageFileSystem = CreateStorageFileSystem(fileSystem);
        var configuredBusyTimeout = busyTimeout ?? TimeSpan.Zero;
        var lockStopwatch = configuredBusyTimeout == Timeout.InfiniteTimeSpan
            ? null
            : Stopwatch.StartNew();
        using var openLock = readOnly
            ? effectiveLockManager.EnterReader(busyTimeout)
            : effectiveLockManager.EnterWriter(busyTimeout);
        using var recoveryLock = readOnly
            ? null
            : effectiveLockManager.EnterRecoveryLock(
                SqlitePagerLockManager.RemainingFileLockTimeout(configuredBusyTimeout, lockStopwatch),
                configuredBusyTimeout);
        var pageStore = SqlitePageStore.OpenForPager(storageFileSystem, databasePath, readOnly, encryption);
        try
        {
            var wal = SqliteWalFile.Open(storageFileSystem, walPath, readOnly, encryption);
            try
            {
                var pager = new SqlitePager(pageStore, wal, effectiveLockManager, pageCacheCapacity);
                var recovery = readOnly
                    ? wal.ScanRecovery()
                    : wal.RecoverToLastCommittedFrame();
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
                pager._lockGeneration = readOnly
                    ? effectiveLockManager.Generation
                    : openLock.PublishStorageChange();
                pager._state = SqlitePagerState.Ready;
                pager._busyTimeout = busyTimeout ?? TimeSpan.Zero;
                return pager;
            }
            catch
            {
                wal.Dispose();
                throw;
            }
        }
        catch
        {
            pageStore.Dispose();
            throw;
        }
    }

    /// <summary>Reads a copy of one page from the committed WAL-overlay view.</summary>
    public byte[] ReadCommittedPage(uint pageNumber)
    {
        using var readerLock = _lockManager.EnterReader(ResolveBusyTimeout(null));
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
        using var readerLock = _lockManager.EnterReader(ResolveBusyTimeout(null));
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
        var readerLock = _lockManager.EnterReader(ResolveBusyTimeout(busyTimeout));
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
                    new Dictionary<uint, byte[]>(_walPageOverlay));
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
        var writerLock = _lockManager.EnterWriter(ResolveBusyTimeout(busyTimeout));
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ThrowIfReadOnly();
                SynchronizeCommittedView();
                try
                {
                    RecoverUncommittedTailUnderWriterLock(writerLock);
                }
                catch
                {
                    TransitionToFaulted();
                    throw;
                }
                if (_state != SqlitePagerState.Ready)
                    throw new InvalidOperationException($"Cannot begin a SQLite pager transaction while the pager is {_state}.");
                ArgumentOutOfRangeException.ThrowIfZero(targetDatabaseSizeInPages);

                var transaction = new SqlitePagerTransaction(this, targetDatabaseSizeInPages, writerLock);
                _activeTransaction = transaction;
                _state = SqlitePagerState.TransactionActive;
                return transaction;
            }
        }
        catch
        {
            writerLock.Dispose();
            throw;
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

    private SqliteCheckpointResult CheckpointToMainStoreCore(
        TimeSpan? busyTimeout,
        bool resetCommittedWal)
    {
        using var checkpointLock = _lockManager.EnterCheckpoint(ResolveBusyTimeout(busyTimeout));
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            SynchronizeCommittedView();
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
                    _wal.ResetAfterDurableCheckpoint(CanPublishCheckpointedRecoveryMarker());
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
        lock (_gate)
        {
            if (_state == SqlitePagerState.Disposed)
                return;

            transaction = _activeTransaction;
            readers = [.. _activeReadTransactions];
            _activeTransaction = null;
            _activeReadTransactions.Clear();
            _state = SqlitePagerState.Disposed;
            _wal.Dispose();
            _pageStore.Dispose();
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
                ValidateWalHasNotChanged();
                for (var index = 0; index < transaction.WriteOrder.Count; index++)
                {
                    var pageNumber = transaction.WriteOrder[index];
                    var databaseSizeInPages = index == transaction.WriteOrder.Count - 1
                        ? transaction.TargetDatabaseSizeInPages
                        : 0;
                    _wal.AppendFrame(pageNumber, transaction.GetPageImage(pageNumber), databaseSizeInPages);
                }

                _wal.Flush();
                var recovery = _wal.ScanRecovery();
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
                transaction.ReleaseWriterLock();
            }
            catch
            {
                TransitionToFaulted();
                transaction.ReleaseWriterLock();
                throw;
            }
        }
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
                if (pageNumber > _pageStore.PageCount)
                {
                    throw new InvalidDataException(
                        $"Snapshot page {pageNumber} is absent from both the WAL overlay and main database file.");
                }

                return _pageStore.ReadPage(pageNumber);
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

    private void SynchronizeCommittedView()
    {
        try
        {
            var generation = _lockManager.Generation;
            if (_lockGeneration == generation && !_lockManager.UsesFileBackedWalLocks)
                return;

            var recovery = _wal.ScanRecovery();
            if (!_lockManager.UsesFileBackedWalLocks
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

    private void RecoverUncommittedTailUnderWriterLock(SqlitePagerLockLease writerLock)
    {
        if (!_lockManager.UsesFileBackedWalLocks || !HasUncommittedOrInvalidTail(_recoveryInfo))
            return;

        var recovery = _wal.RecoverToLastCommittedFrame();
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
            TursoEncryptionFileSystem encrypted when encrypted.Inner is PhysicalFileSystem physicalFileSystem
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

    private static TursoEncryptionOptions? GetFileSystemEncryption(IFileSystem fileSystem)
        => fileSystem is TursoEncryptionFileSystem encrypted ? encrypted.Encryption : null;

    private static SqliteWalRecoveryInfo CreateEmptyRecoveryInfo()
        => new(
            LastValidFrameNumber: 0,
            LastCommittedFrameNumber: 0,
            LastCommittedDatabaseSizeInPages: 0,
            LastCommittedByteLength: SqliteWalHeader.Size,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile);

    private void InitializeCommittedView(SqliteWalRecoveryInfo recovery)
    {
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
            var frame = _wal.ReadFrame(frameNumber);
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
            || !_wal.HasCheckpointedRecoveryMarker)
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
        if (_pageStore.PageSize != _wal.PageSize)
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
        var recovery = _wal.ScanRecovery();
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
        if (_pageCache.TryGetValue(pageNumber, out var cachedPage))
            return cachedPage;
        if (pageNumber > _pageStore.PageCount)
        {
            throw new InvalidDataException(
                $"Committed SQLite page {pageNumber} is absent from both the WAL overlay and main database file.");
        }

        var page = _pageStore.ReadPage(pageNumber);
        _pageCache.Add(pageNumber, page);
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
    private SqlitePagerLockLease? _readerLock;

    internal SqlitePagerReadTransaction(
        SqlitePager pager,
        SqlitePagerLockLease readerLock,
        uint pageCount,
        IReadOnlyDictionary<uint, byte[]> walPageOverlay)
    {
        _pager = pager;
        _readerLock = readerLock;
        PageCount = pageCount;
        _walPageOverlay = walPageOverlay;
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

            return _pager.ReadSnapshotPage(_walPageOverlay, PageCount, pageNumber);
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
        SqlitePagerLockLease writerLock)
    {
        _pager = pager;
        _writerLock = writerLock;
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
