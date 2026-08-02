using System.Diagnostics;

namespace Ahtola.Core.Storage;

/// <summary>
/// Acquires byte-range locks in SQLite's WAL shared-memory file. The component
/// uses the file only as a lock carrier: it neither maps nor writes the
/// WAL-index. Physical pagers separately exclude other client processes with a
/// lifetime main-file ownership lock.
/// </summary>
/// <remarks>
/// SQLite reserves bytes 120 through 127 of <c>database-shm</c> for the WAL
/// write, checkpoint, recovery, and five reader locks. A checkpoint locks the
/// complete range; readers occupy one reader byte per process. The lock file is
/// deliberately retained on close because another process can still be using it.
/// The coordinator keeps one carrier handle while it owns any range. This is
/// necessary on Linux, where closing any descriptor for a file releases every
/// process-owned POSIX record lock for that file. Windows and Linux
/// <see cref="FileStream.Lock"/> report contention immediately, and this
/// component retries with a bounded delay until the pager busy timeout expires.
/// See <c>docs/wal-interoperability-contract.md</c> for
/// the normative lock-byte map and the staged transition to a real WAL-index.
/// </remarks>
internal sealed class SqliteWalSharedMemoryLocks : ISqlitePagerLockCoordinator
{
    private const long WriteLockOffset = 120;
    private const long RecoveryLockOffset = 122;
    private const long FirstReaderLockOffset = 123;
    private const int ReaderLockCount = 5;
    private const long LockRangeLength = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly object _gate = new();
    private readonly string _path;
    private FileStream? _readOnlyCarrierStream;
    private FileStream? _readWriteCarrierStream;
    private Exception? _failure;
    private long? _readerLockOffset;
    private FileStream? _readerLockStream;
    private int _readerReferenceCount;
    private int _activeRangeCount;

    internal SqliteWalSharedMemoryLocks(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _path = string.Concat(Path.GetFullPath(databasePath), "-shm");
    }

    public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout)
        => Acquire(operation, timeout, pagerReadOnly: true);

    /// <summary>
    /// Acquires a range on behalf of one pager. A read-write pager may create the
    /// missing lock carrier on demand exactly like a native read-write connection
    /// does; a read-only pager must never create it (see the interoperability
    /// contract), so its reader locks fail while the carrier is absent.
    /// </summary>
    internal IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout, bool pagerReadOnly)
        => operation switch
        {
            SqlitePagerLockOperation.Reader => AcquireReader(timeout, pagerReadOnly),
            SqlitePagerLockOperation.Writer => AcquireRange(
                operation,
                WriteLockOffset,
                length: 1,
                timeout),
            SqlitePagerLockOperation.Checkpoint => AcquireRange(
                operation,
                WriteLockOffset,
                LockRangeLength,
                timeout),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown SQLite WAL lock operation."),
        };

    public IDisposable AcquireRecovery(TimeSpan timeout)
        => AcquireRange(SqlitePagerLockOperation.Writer, RecoveryLockOffset, length: 1, timeout);

    private IDisposable AcquireReader(TimeSpan timeout, bool pagerReadOnly)
    {
        var stopwatch = StartTimeout(timeout);
        while (true)
        {
            IOException? lastContention = null;
            lock (_gate)
            {
                ThrowIfFailed();
                if (_readerReferenceCount != 0)
                {
                    _readerReferenceCount++;
                    return new ReaderLease(this);
                }

                for (var slot = 0; slot < ReaderLockCount; slot++)
                {
                    if (TryLockRange(
                            FirstReaderLockOffset + slot,
                            length: 1,
                            readOnly: pagerReadOnly,
                            out var lockStream,
                            out lastContention))
                    {
                        _readerLockOffset = FirstReaderLockOffset + slot;
                        _readerLockStream = lockStream;
                        _readerReferenceCount = 1;
                        _activeRangeCount++;
                        return new ReaderLease(this);
                    }
                }

                CloseCarrierStreamIfUnused();
            }

            if (!WaitForRetry(timeout, stopwatch))
                throw CreateBusyException(SqlitePagerLockOperation.Reader, timeout, lastContention);
        }
    }

    private IDisposable AcquireRange(
        SqlitePagerLockOperation operation,
        long offset,
        long length,
        TimeSpan timeout)
    {
        var stopwatch = StartTimeout(timeout);
        while (true)
        {
            IOException? contention;
            lock (_gate)
            {
                ThrowIfFailed();
                if (TryLockRange(offset, length, readOnly: false, out var lockStream, out contention))
                {
                    _activeRangeCount++;
                    return new RangeLease(this, lockStream, offset, length);
                }

                CloseCarrierStreamIfUnused();
            }

            if (!WaitForRetry(timeout, stopwatch))
                throw CreateBusyException(operation, timeout, contention);
        }
    }

    private bool TryLockRange(
        long offset,
        long length,
        bool readOnly,
        out FileStream lockStream,
        out IOException? contention)
    {
        var stream = GetOrOpenCarrierStream(readOnly);
        try
        {
            Lock(stream, offset, length);
            lockStream = stream;
            contention = null;
            return true;
        }
        catch (IOException exception)
        {
            lockStream = null!;
            contention = exception;
            return false;
        }
    }

    private FileStream GetOrOpenCarrierStream(bool readOnly)
    {
        if (readOnly)
        {
            if (_readWriteCarrierStream is not null)
                return _readWriteCarrierStream;
            if (_readOnlyCarrierStream is not null)
                return _readOnlyCarrierStream;

            try
            {
                _readOnlyCarrierStream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.None);
                return _readOnlyCarrierStream;
            }
            catch (FileNotFoundException exception)
            {
                throw new InvalidOperationException(
                    "Cannot safely open the managed database read-only because its WAL lock file is missing. "
                    + "Creating that file would mutate storage.",
                    exception);
            }
        }

        if (_readWriteCarrierStream is not null)
            return _readWriteCarrierStream;

        _readWriteCarrierStream = new FileStream(
            _path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);
        return _readWriteCarrierStream;
    }

    private void ReleaseReader()
    {
        lock (_gate)
        {
            if (_readerReferenceCount == 0)
                throw new InvalidOperationException("SQLite WAL reader lock reference count underflow.");

            if (_readerReferenceCount != 1)
            {
                _readerReferenceCount--;
                return;
            }

            var readerLockOffset = _readerLockOffset
                ?? throw new InvalidOperationException("SQLite WAL reader lock ownership is inconsistent.");
            var readerLockStream = _readerLockStream
                ?? throw new InvalidOperationException("SQLite WAL reader lock stream ownership is inconsistent.");
            UnlockRange(readerLockStream, readerLockOffset, length: 1);
            _readerReferenceCount = 0;
            _readerLockOffset = null;
            _readerLockStream = null;
        }
    }

    private void ReleaseRange(FileStream lockStream, long offset, long length)
    {
        lock (_gate)
            UnlockRange(lockStream, offset, length);
    }

    private void UnlockRange(FileStream lockStream, long offset, long length)
    {
        if (_activeRangeCount == 0)
            throw new InvalidOperationException("SQLite WAL shared-memory lock ownership is inconsistent.");

        try
        {
            Unlock(lockStream, offset, length);
        }
        catch (IOException exception)
        {
            _failure = exception;
            throw;
        }

        _activeRangeCount--;
        CloseCarrierStreamIfUnused();
    }

    private static void Lock(FileStream stream, long offset, long length)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            stream.Lock(offset, length);
            return;
        }

        throw CreatePlatformNotSupportedException();
    }

    private static void Unlock(FileStream stream, long offset, long length)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            stream.Unlock(offset, length);
            return;
        }

        throw CreatePlatformNotSupportedException();
    }

    private static PlatformNotSupportedException CreatePlatformNotSupportedException()
        => new(
            "Managed SQLite WAL locking requires FileStream byte-range locks, which are supported here only on Windows and Linux.");

    private void CloseCarrierStreamIfUnused()
    {
        if (_activeRangeCount != 0)
            return;

        _readOnlyCarrierStream?.Dispose();
        _readOnlyCarrierStream = null;
        _readWriteCarrierStream?.Dispose();
        _readWriteCarrierStream = null;
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException(
                "SQLite WAL shared-memory lock release failed; refusing later lock acquisitions.",
                _failure);
        }
    }

    private static SqlitePagerBusyException CreateBusyException(
        SqlitePagerLockOperation operation,
        TimeSpan timeout,
        IOException? innerException)
        => new SqlitePagerBusyException(operation, timeout, innerException);

    private static Stopwatch? StartTimeout(TimeSpan timeout)
        => timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();

    private static bool WaitForRetry(TimeSpan timeout, Stopwatch? stopwatch)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            Thread.Sleep(RetryDelay);
            return true;
        }

        var remaining = timeout - stopwatch!.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return false;

        Thread.Sleep(remaining < RetryDelay ? remaining : RetryDelay);
        return true;
    }

    private sealed class ReaderLease : IDisposable
    {
        private SqliteWalSharedMemoryLocks? _owner;

        internal ReaderLease(SqliteWalSharedMemoryLocks owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseReader();
        }
    }

    private sealed class RangeLease : IDisposable
    {
        private SqliteWalSharedMemoryLocks? _owner;
        private readonly long _offset;
        private readonly long _length;
        private readonly FileStream _lockStream;

        internal RangeLease(SqliteWalSharedMemoryLocks owner, FileStream lockStream, long offset, long length)
        {
            _owner = owner;
            _lockStream = lockStream;
            _offset = offset;
            _length = length;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseRange(_lockStream, _offset, _length);
        }
    }
}
