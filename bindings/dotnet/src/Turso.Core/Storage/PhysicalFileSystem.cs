using Microsoft.Win32.SafeHandles;

namespace Turso.Core.Storage;

/// <summary>
/// Production <see cref="IFileSystem"/> backed by the host file system. Files
/// use an OS handle with positional <see cref="RandomAccess"/> I/O, which is
/// safe for concurrent offset-addressed reads and writes on a single handle.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    /// <summary>A shared, stateless instance.</summary>
    public static PhysicalFileSystem Instance { get; } = new();

    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.Exists(path);
    }

    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        => OpenFile(path, mode, readOnly, FileShare.Read);

    internal IFile OpenPagerFile(string path, FileOpenMode mode, bool readOnly = false)
        => OpenFile(path, mode, readOnly, FileShare.ReadWrite | FileShare.Delete);

    private IFile OpenFile(string path, FileOpenMode mode, bool readOnly, FileShare share)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (readOnly && mode == FileOpenMode.CreateNew)
            throw new ArgumentException("A newly created file cannot be opened read-only.", nameof(readOnly));

        var fileMode = mode switch
        {
            FileOpenMode.OpenExisting => FileMode.Open,
            FileOpenMode.OpenOrCreate => FileMode.OpenOrCreate,
            FileOpenMode.CreateNew => FileMode.CreateNew,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported file open mode."),
        };

        var access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        var handle = File.OpenHandle(path, fileMode, access, share, FileOptions.None);
        return new PhysicalFile(handle, readOnly);
    }

    public void DeleteFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.Delete(path);
    }
}

/// <summary>
/// Gives <see cref="SqlitePager"/> its required shared data handles without
/// weakening the default sharing policy for direct page-store users.
/// </summary>
internal sealed class SqlitePagerPhysicalFileSystem(PhysicalFileSystem fileSystem) : IFileSystem
{
    public bool FileExists(string path) => fileSystem.FileExists(path);

    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        => fileSystem.OpenPagerFile(path, mode, readOnly);

    public void DeleteFile(string path) => fileSystem.DeleteFile(path);
}

/// <summary>
/// A host file handle exposing positional I/O over <see cref="RandomAccess"/>.
/// </summary>
public sealed class PhysicalFile : IFile
{
    private readonly SafeFileHandle _handle;

    internal PhysicalFile(SafeFileHandle handle, bool readOnly)
    {
        _handle = handle;
        IsReadOnly = readOnly;
    }

    public bool IsReadOnly { get; }

    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return RandomAccess.GetLength(_handle);
        }
    }

    public int Read(long position, Span<byte> destination)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(_handle, destination[total..], position + total);
            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    public void Write(long position, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot write to a file opened read-only.");

        // The span overload of RandomAccess.Write writes the entire buffer or throws.
        RandomAccess.Write(_handle, source, position);
    }

    public void SetLength(long length)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot resize a file opened read-only.");

        RandomAccess.SetLength(_handle, length);
    }

    public void FlushToDisk()
    {
        ThrowIfDisposed();
        if (IsReadOnly)
            return;

        RandomAccess.FlushToDisk(_handle);
    }

    public void Dispose() => _handle.Dispose();

    private void ThrowIfDisposed()
    {
        if (_handle.IsClosed)
            throw new ObjectDisposedException(nameof(PhysicalFile));
    }
}
