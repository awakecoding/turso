using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ahtola.Core.Storage;

/// <summary>
/// Raised when a physical managed database cannot obtain its required exclusive
/// client-ownership lock.
/// </summary>
public sealed class SqlitePagerClientOwnershipException : InvalidOperationException
{
    internal SqlitePagerClientOwnershipException(
        string databasePath,
        TimeSpan timeout,
        Exception innerException)
        : base(
            $"Managed WAL ownership for database '{databasePath}' could not be acquired within {timeout}. "
            + "Concurrent access from another process or an ordinary SQLite client is unsupported because "
            + "the managed pager does not maintain SQLite's WAL-index. Close the other client and reopen.",
            innerException)
    {
        DatabasePath = databasePath;
        Timeout = timeout;
    }

    /// <summary>The fully qualified database path whose ownership was rejected.</summary>
    public string DatabasePath { get; }

    /// <summary>The configured ownership acquisition timeout.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// Owns SQLite's complete main-file lock-byte range for every physical pager in
/// this process. This excludes ordinary SQLite and other managed processes while
/// allowing all managed pagers in the owning process to share one carrier lock.
/// Linux uses an open-file-description lock because closing any unrelated
/// descriptor releases every process-owned POSIX record lock for that file.
/// </summary>
internal sealed class SqliteManagedFileOwnership
{
    private const long PendingByte = 0x4000_0000;
    private const long LockRangeLength = 512;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly object _gate = new();
    private readonly string _databasePath;
    private FileStream? _carrierStream;
    private int _referenceCount;
    private bool _acquiring;
    private Exception? _failure;

    internal SqliteManagedFileOwnership(string databasePath)
        => _databasePath = databasePath;

    internal IDisposable Acquire(bool createNew, bool readOnly, TimeSpan timeout)
    {
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                ThrowIfFailed();
                if (_referenceCount != 0)
                {
                    if (createNew)
                        throw new IOException($"The managed database '{_databasePath}' already exists.");

                    _referenceCount++;
                    return new Lease(this);
                }

                if (!_acquiring)
                {
                    _acquiring = true;
                    break;
                }
                if (createNew)
                    throw new IOException($"The managed database '{_databasePath}' is already being opened.");

                var remaining = RemainingTimeout(timeout, stopwatch);
                if (remaining == TimeSpan.Zero)
                    throw CreateOwnershipException(timeout);
                Monitor.Wait(_gate, remaining);
            }
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            CompleteAcquisition(null);
            throw new PlatformNotSupportedException(
                "Managed physical databases require SQLite main-file byte-range locks, "
                + "which are supported here only on Windows and Linux.");
        }

        var mode = createNew ? FileMode.CreateNew : FileMode.Open;
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                _databasePath,
                mode,
                OperatingSystem.IsWindows() && readOnly ? FileAccess.Read : FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);
            AcquireRange(stream, timeout, stopwatch);
            lock (_gate)
            {
                if (!_acquiring || _referenceCount != 0 || _carrierStream is not null)
                    throw new InvalidOperationException("Managed SQLite client ownership acquisition state is inconsistent.");

                _carrierStream = stream;
                _referenceCount = 1;
                _acquiring = false;
                Monitor.PulseAll(_gate);
                return new Lease(this);
            }
        }
        catch
        {
            stream?.Dispose();
            CompleteAcquisition(null);
            throw;
        }
    }

    private void AcquireRange(FileStream stream, TimeSpan timeout, Stopwatch? stopwatch)
    {
        IOException? contention;
        while (true)
        {
            try
            {
                Lock(stream);
                return;
            }
            catch (IOException exception)
            {
                contention = exception;
            }

            if (!WaitForRetry(timeout, stopwatch))
                throw CreateOwnershipException(timeout, contention);
        }
    }

    private void CompleteAcquisition(FileStream? stream)
    {
        lock (_gate)
        {
            if (!_acquiring)
                throw new InvalidOperationException("Managed SQLite client ownership acquisition is not active.");

            _carrierStream = stream;
            _acquiring = false;
            Monitor.PulseAll(_gate);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_referenceCount == 0)
                throw new InvalidOperationException("Managed SQLite client ownership reference count underflow.");

            _referenceCount--;
            if (_referenceCount != 0)
                return;

            var stream = _carrierStream
                ?? throw new InvalidOperationException("Managed SQLite client ownership stream is missing.");
            _carrierStream = null;
            try
            {
                Unlock(stream);
            }
            catch (IOException exception)
            {
                _failure = exception;
                throw;
            }
            finally
            {
                stream.Dispose();
            }
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException(
                "Managed SQLite client ownership release failed; refusing later database opens.",
                _failure);
        }
    }

    private static void Lock(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            stream.Lock(PendingByte, LockRangeLength);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            LinuxOpenFileDescriptionLocks.Lock(
                stream.SafeFileHandle,
                PendingByte,
                LockRangeLength);
            return;
        }

        throw new PlatformNotSupportedException(
            "Managed physical databases require SQLite main-file byte-range locks, "
            + "which are supported here only on Windows and Linux.");
    }

    private static void Unlock(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            stream.Unlock(PendingByte, LockRangeLength);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            LinuxOpenFileDescriptionLocks.Unlock(
                stream.SafeFileHandle,
                PendingByte,
                LockRangeLength);
            return;
        }

        throw new PlatformNotSupportedException(
            "Managed physical databases require SQLite main-file byte-range locks, "
            + "which are supported here only on Windows and Linux.");
    }

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

    private static TimeSpan RemainingTimeout(TimeSpan timeout, Stopwatch? stopwatch)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;

        var remaining = timeout - stopwatch!.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return remaining > TimeSpan.FromMilliseconds(int.MaxValue)
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : remaining;
    }

    private SqlitePagerClientOwnershipException CreateOwnershipException(
        TimeSpan timeout,
        Exception? innerException = null)
        => new(
            _databasePath,
            timeout,
            innerException ?? new TimeoutException("Another local caller is acquiring managed SQLite ownership."));

    private sealed class Lease(SqliteManagedFileOwnership owner) : IDisposable
    {
        private SqliteManagedFileOwnership? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}

internal static partial class LinuxOpenFileDescriptionLocks
{
    private const int SetLockCommand = 37;
    private const short WriteLockType = 1;
    private const short UnlockType = 2;
    private const short SeekSet = 0;
    private const int AccessDenied = 13;
    private const int ResourceTemporarilyUnavailable = 11;

    internal static void Lock(SafeFileHandle handle, long offset, long length)
    {
        EnsureSupportedArchitecture();
        var fileLock = new FileLock(WriteLockType, SeekSet, offset, length);
        if (Fcntl(handle, SetLockCommand, ref fileLock) == 0)
            return;

        var error = Marshal.GetLastPInvokeError();
        var exception = new Win32Exception(error);
        if (error is AccessDenied or ResourceTemporarilyUnavailable)
        {
            throw new IOException(
                "The Linux SQLite main-file ownership range is held by another client.",
                exception);
        }

        throw new InvalidOperationException(
            "Linux open-file-description lock acquisition failed.",
            exception);
    }

    internal static void Unlock(SafeFileHandle handle, long offset, long length)
    {
        EnsureSupportedArchitecture();
        var fileLock = new FileLock(UnlockType, SeekSet, offset, length);
        if (Fcntl(handle, SetLockCommand, ref fileLock) == 0)
            return;

        var error = Marshal.GetLastPInvokeError();
        throw new IOException(
            "Linux open-file-description lock release failed.",
            new Win32Exception(error));
    }

    private static void EnsureSupportedArchitecture()
    {
        if (!Environment.Is64BitProcess || Marshal.SizeOf<FileLock>() != 32)
        {
            throw new PlatformNotSupportedException(
                "Managed physical databases require the 64-bit Linux fcntl lock layout.");
        }
    }

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(
        SafeFileHandle fileDescriptor,
        int command,
        ref FileLock fileLock);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileLock(short type, short whence, long start, long length)
    {
        internal short Type = type;
        internal short Whence = whence;
        internal long Start = start;
        internal long Length = length;
        internal int ProcessId;
    }
}

internal static class SqliteManagedFileOwnershipRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SqliteManagedFileOwnership> Owners = new(StringComparer.Ordinal);

    internal static IDisposable? Acquire(
        IFileSystem fileSystem,
        string databasePath,
        bool createNew,
        bool readOnly,
        TimeSpan timeout)
    {
        fileSystem = AhtolaEncryptionFileSystem.Unwrap(fileSystem);
        if (fileSystem is not PhysicalFileSystem)
            return null;

        var path = Path.GetFullPath(databasePath);
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        SqliteManagedFileOwnership owner;
        lock (Gate)
        {
            if (!Owners.TryGetValue(key, out owner!))
            {
                owner = new SqliteManagedFileOwnership(path);
                Owners.Add(key, owner);
            }
        }

        return owner.Acquire(createNew, readOnly, timeout);
    }
}
