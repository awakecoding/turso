using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>
/// A minimal page store over a SQLite-format database file. It owns an
/// <see cref="IFile"/> and exposes 1-based, page-aligned reads and writes while
/// enforcing the on-disk invariants the engine relies on: the file always holds
/// a whole number of fixed-size pages, page 1 carries a valid database header,
/// and the page size never changes for the life of the store.
/// </summary>
/// <remarks>
/// This is the durable-storage foundation only. A single <see cref="WritePage"/>
/// is one aligned, page-sized write — the atomic unit of the store — but
/// crash-atomic multi-page transactions require the pager's WAL or rollback
/// journal layer.
/// </remarks>
public sealed class SqlitePageStore : IDisposable
{
    private readonly IFile _file;
    private readonly AhtolaPageEncryption? _encryption;
    private SqliteDatabaseHeader _header;
    private bool _disposed;

    private SqlitePageStore(IFile file, SqliteDatabaseHeader header, AhtolaPageEncryption? encryption)
    {
        _file = file;
        _header = header;
        _encryption = encryption;
        PageSize = header.PageSize;
    }

    /// <summary>Page size in bytes; fixed for the life of the store.</summary>
    public int PageSize { get; }

    internal string Path { get; private init; } = string.Empty;

    /// <summary>Whether the underlying file was opened read-only.</summary>
    public bool IsReadOnly => _file.IsReadOnly;

    /// <summary>The database header currently stored on page 1.</summary>
    public SqliteDatabaseHeader Header
    {
        get
        {
            ThrowIfDisposed();
            return _header;
        }
    }

    /// <summary>
    /// Number of whole pages currently in the file. Reflects appends and
    /// truncations performed through this store.
    /// </summary>
    public uint PageCount
    {
        get
        {
            ThrowIfDisposed();
            return checked((uint)(_file.Length / PageSize));
        }
    }

    /// <summary>
    /// Opens an existing SQLite-format file, validating the header and that the
    /// file spans a whole number of pages.
    /// </summary>
    public static SqlitePageStore Open(
        IFileSystem fileSystem,
        string path,
        bool readOnly = false,
        AhtolaEncryptionOptions? encryption = null)
        => OpenCore(fileSystem, path, readOnly, encryption, allowTrailingPages: false);

    /// <summary>
    /// Opens a store for a pager that retains a committed WAL while completing a
    /// shrink checkpoint. Only the pager may accept physical pages after the
    /// authoritative database size, and it must prove they are recoverable from
    /// the retained WAL before exposing the database.
    /// </summary>
    internal static SqlitePageStore OpenForPager(
        IFileSystem fileSystem,
        string path,
        bool readOnly = false,
        AhtolaEncryptionOptions? encryption = null)
        => OpenCore(fileSystem, path, readOnly, encryption, allowTrailingPages: true);

    private static SqlitePageStore OpenCore(
        IFileSystem fileSystem,
        string path,
        bool readOnly,
        AhtolaEncryptionOptions? encryption,
        bool allowTrailingPages)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(path);

        AhtolaPageEncryption? pageEncryption = null;
        var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly);
        try
        {
            var length = file.Length;
            if (length < SqliteDatabaseHeader.Size)
                throw new InvalidDataException("File is too small to contain a SQLite database header.");

            Span<byte> rawHeader = stackalloc byte[SqliteDatabaseHeader.Size];
            if (file.Read(0, rawHeader) != SqliteDatabaseHeader.Size)
                throw new InvalidDataException("Failed to read the complete SQLite database header.");

            SqliteDatabaseHeader header;
            if (IsAhtolaEncrypted(rawHeader))
            {
                if (encryption is null)
                {
                    throw new InvalidDataException(
                        "Database is encrypted with Ahtola page encryption. Supply AhtolaEncryptionOptions; plaintext fallback is not permitted.");
                }

                var pageSize = SqlitePageSize.Decode(BinaryPrimitives.ReadUInt16BigEndian(rawHeader[16..]));
                if (length < pageSize)
                    throw new InvalidDataException("Encrypted database is smaller than its declared page size.");
                if (length % pageSize != 0)
                    throw new InvalidDataException("Encrypted database file is not a whole number of pages.");

                pageEncryption = encryption.CreatePageEncryption(pageSize);
                pageEncryption.ValidateEncryptedHeader(rawHeader);
                var encryptedFirstPage = new byte[pageSize];
                if (file.Read(0, encryptedFirstPage) != pageSize)
                    throw new InvalidDataException("Failed to read the complete encrypted first page.");

                header = SqliteDatabaseHeader.Parse(pageEncryption.DecryptPage(encryptedFirstPage, 1));
                if (header.ReservedSpace != AhtolaPageEncryption.MetadataSize)
                {
                    throw new InvalidDataException(
                        $"Encrypted Ahtola database reserves {header.ReservedSpace} bytes per page, but AES-GCM requires {AhtolaPageEncryption.MetadataSize}.");
                }
            }
            else
            {
                if (encryption is not null)
                {
                    throw new InvalidDataException(
                        "Encryption was requested, but the database contains a plaintext SQLite header. Plaintext fallback is not permitted.");
                }

                header = SqliteDatabaseHeader.Parse(rawHeader);
            }

            ValidateFileLayout(length, header, allowTrailingPages);

            return new SqlitePageStore(file, header, pageEncryption) { Path = path };
        }
        catch
        {
            file.Dispose();
            pageEncryption?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a fresh single-page SQLite database file whose root page is an
    /// empty table b-tree, using <paramref name="header"/> (or a default header)
    /// as page 1's database header.
    /// </summary>
    public static SqlitePageStore Create(
        IFileSystem fileSystem,
        string path,
        SqliteDatabaseHeader? header = null,
        bool overwrite = false,
        AhtolaEncryptionOptions? encryption = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(path);

        // A freshly created database has exactly one page, so the in-header size
        // is authoritative (change-counter equals version-valid-for).
        var effectiveHeader = (header ?? SqliteDatabaseHeader.CreateDefault()) with
        {
            ChangeCounter = 1,
            DatabaseSizeInPages = 1,
            VersionValidFor = 1,
        };
        AhtolaPageEncryption? pageEncryption = null;
        if (encryption is not null)
        {
            pageEncryption = encryption.CreatePageEncryption(effectiveHeader.PageSize);
            effectiveHeader = pageEncryption.PrepareHeader(effectiveHeader);
        }

        var pageSize = effectiveHeader.PageSize;
        var firstPage = new byte[pageSize];
        effectiveHeader.WriteTo(firstPage);
        SqliteBtreePageHeader
            .CreateEmpty(
                SqliteBtreePageType.TableLeaf,
                pageSize,
                isFirstPage: true,
                usableSpace: effectiveHeader.UsableSpace)
            .WriteTo(firstPage);

        var mode = overwrite ? FileOpenMode.OpenOrCreate : FileOpenMode.CreateNew;
        var file = fileSystem.OpenFile(path, mode);
        var createdArtifact = mode == FileOpenMode.CreateNew;
        try
        {
            file.SetLength(0);
            file.Write(0, pageEncryption is null ? firstPage : pageEncryption.EncryptPage(firstPage, 1));
            file.SetLength(pageSize);
            file.FlushToDisk();
            return new SqlitePageStore(file, effectiveHeader, pageEncryption) { Path = path };
        }
        catch
        {
            try
            {
                file.Dispose();
            }
            catch
            {
            }

            try
            {
                pageEncryption?.Dispose();
            }
            catch
            {
            }

            if (createdArtifact)
            {
                try
                {
                    fileSystem.DeleteFile(path);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Reads page <paramref name="pageNumber"/> (1-based) into
    /// <paramref name="destination"/>, which must be exactly <see cref="PageSize"/> bytes.
    /// </summary>
    public void ReadPage(uint pageNumber, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (destination.Length != PageSize)
            throw new ArgumentException($"Destination must be exactly {PageSize} bytes.", nameof(destination));

        var count = PageCount;
        if (pageNumber < 1 || pageNumber > count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Page number is out of range for a database of {count} page(s).");
        }

        var offset = PageOffset(pageNumber);
        if (_encryption is not null)
        {
            var encryptedPage = new byte[PageSize];
            var encryptedRead = _file.Read(offset, encryptedPage);
            if (encryptedRead != PageSize)
            {
                throw new InvalidDataException(
                    $"Short read on encrypted page {pageNumber}: expected {PageSize} bytes, got {encryptedRead}. The file may be truncated.");
            }

            _encryption.DecryptPage(encryptedPage, pageNumber).CopyTo(destination);
            return;
        }

        var read = _file.Read(offset, destination);
        if (read != PageSize)
        {
            throw new InvalidDataException(
                $"Short read on page {pageNumber}: expected {PageSize} bytes, got {read}. The file may be truncated.");
        }
    }

    /// <summary>Reads page <paramref name="pageNumber"/> (1-based) into a new array.</summary>
    public byte[] ReadPage(uint pageNumber)
    {
        var page = new byte[PageSize];
        ReadPage(pageNumber, page);
        return page;
    }

    internal byte[] ReadRawPage(uint pageNumber)
    {
        ThrowIfDisposed();
        var count = PageCount;
        if (pageNumber < 1 || pageNumber > count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Page number is out of range for a database of {count} page(s).");
        }

        var page = new byte[PageSize];
        var read = _file.Read(PageOffset(pageNumber), page);
        if (read != PageSize)
            throw new InvalidDataException($"Short raw read on page {pageNumber}: expected {PageSize} bytes, got {read}.");
        return page;
    }

    internal void RefreshHeader()
    {
        ThrowIfDisposed();
        var firstPage = ReadPage(1);
        var header = SqliteDatabaseHeader.Parse(firstPage);
        if (header.PageSize != PageSize)
            throw new InvalidDataException("SQLite database page size changed; dispose and reopen this pager.");
        _header = header;
        ValidateFileLayout(_file.Length, header, allowTrailingPages: false);
    }

    internal void ReplaceRawContent(IFile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        ThrowIfReadOnly();

        var sourceLength = source.Length;
        var buffer = new byte[64 * 1024];
        _file.SetLength(0);
        var offset = 0L;
        while (offset < sourceLength)
        {
            var count = checked((int)Math.Min(buffer.Length, sourceLength - offset));
            var read = source.Read(offset, buffer.AsSpan(0, count));
            if (read != count)
                throw new InvalidDataException("Replacement SQLite database file was truncated while being copied.");
            _file.Write(offset, buffer.AsSpan(0, count));
            offset += count;
        }

        _file.SetLength(sourceLength);
        _file.FlushToDisk();
    }

    /// <summary>
    /// Writes <paramref name="source"/> (exactly <see cref="PageSize"/> bytes) to
    /// page <paramref name="pageNumber"/> (1-based). Existing pages are overwritten
    /// in place; a page number one past the end appends a single page. Writing
    /// further past the end is rejected because it would leave an uninitialized
    /// gap and break the whole-pages invariant.
    /// </summary>
    public void WritePage(uint pageNumber, ReadOnlySpan<byte> source)
        => WritePageCore(pageNumber, source, allowShrinkPageOneHeader: false);

    /// <summary>
    /// Installs page 1 for the final phase of a pager-owned shrink checkpoint.
    /// The pager has already validated the retained WAL commit and will truncate
    /// the free suffix only after this page is durable.
    /// </summary>
    internal void WriteShrinkCheckpointPageOne(ReadOnlySpan<byte> source)
        => WritePageCore(pageNumber: 1, source, allowShrinkPageOneHeader: true);

    private void WritePageCore(
        uint pageNumber,
        ReadOnlySpan<byte> source,
        bool allowShrinkPageOneHeader)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        if (source.Length != PageSize)
            throw new ArgumentException($"Page data must be exactly {PageSize} bytes.", nameof(source));
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "Page numbers are 1-based.");

        var count = PageCount;
        if (pageNumber > count + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Cannot write page {pageNumber}: it would skip past the current end of {count} page(s).");
        }

        SqliteDatabaseHeader? updatedHeader = null;
        if (pageNumber == 1)
        {
            updatedHeader = SqliteDatabaseHeader.Parse(source);
            if (updatedHeader.PageSize != PageSize)
                throw new InvalidOperationException("Page 1 header cannot change the store's page size.");
            if (updatedHeader.VersionValidFor == updatedHeader.ChangeCounter
                && updatedHeader.DatabaseSizeInPages != count
                && (!allowShrinkPageOneHeader
                    || updatedHeader.DatabaseSizeInPages == 0
                    || updatedHeader.DatabaseSizeInPages >= count))
            {
                throw new InvalidOperationException(
                    "Page 1 header page count must match the current file size when it is authoritative.");
            }
        }
        else if (allowShrinkPageOneHeader)
        {
            throw new InvalidOperationException("Only page 1 may be installed through the shrink checkpoint path.");
        }

        if (pageNumber == count + 1)
        {
            WriteAppendedPage(pageNumber, source, count);
            return;
        }

        WriteRawPage(pageNumber, source);
        AssertPageAligned();
        if (updatedHeader is not null)
            _header = updatedHeader;
    }

    private void WriteAppendedPage(uint pageNumber, ReadOnlySpan<byte> source, uint previousPageCount)
    {
        var originalLength = checked((long)previousPageCount * PageSize);
        try
        {
            WriteRawPage(pageNumber, source);
            AssertPageAligned();
            UpdateHeaderPageCount(pageNumber);
        }
        catch (Exception writeException)
        {
            try
            {
                if (_file.Length != originalLength)
                    _file.SetLength(originalLength);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidDataException(
                    "Appending a page failed and the prior file length could not be restored.",
                    new AggregateException(writeException, rollbackException));
            }

            throw;
        }
    }

    private void UpdateHeaderPageCount(uint pageCount)
    {
        var updatedHeader = _header with
        {
            DatabaseSizeInPages = pageCount,
            VersionValidFor = _header.ChangeCounter,
        };
        if (_encryption is null)
        {
            Span<byte> headerBytes = stackalloc byte[SqliteDatabaseHeader.Size];
            updatedHeader.WriteTo(headerBytes);
            _file.Write(0, headerBytes);
        }
        else
        {
            var firstPage = ReadPage(1);
            updatedHeader.WriteTo(firstPage);
            WriteRawPage(1, firstPage);
        }
        _header = updatedHeader;
    }

    private void AssertPageAligned()
    {
        if (_file.Length % PageSize != 0)
            throw new InvalidDataException("Write left the database file at a non page-aligned length.");
    }

    /// <summary>Flushes all written pages to durable storage.</summary>
    public void Flush()
    {
        ThrowIfDisposed();
        if (!IsReadOnly)
            _file.FlushToDisk();
    }

    /// <summary>
    /// Removes only a verified free suffix after a pager checkpoint has durably
    /// installed page 1 with its new authoritative database size. This is
    /// intentionally internal: direct callers cannot safely coordinate it with
    /// the retained WAL that makes an interrupted shrink recoverable.
    /// </summary>
    internal void TruncateToPageCount(uint pageCount)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        if (pageCount == 0 || pageCount > PageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageCount),
                pageCount,
                $"Truncation page count must be between 1 and the current {PageCount} page(s).");
        }

        if (pageCount == PageCount)
            return;

        var firstPage = ReadPage(1);
        var header = SqliteDatabaseHeader.Parse(firstPage);
        if (header.VersionValidFor != header.ChangeCounter || header.DatabaseSizeInPages != pageCount)
        {
            throw new InvalidOperationException(
                "Cannot truncate a SQLite database before page 1 durably declares the requested authoritative page count.");
        }

        var targetLength = checked((long)pageCount * PageSize);
        _file.SetLength(targetLength);
        if (_file.Length != targetLength)
            throw new InvalidDataException("SQLite database truncation did not reach its requested page boundary.");

        AssertPageAligned();
        _header = header;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _file.Dispose();
        _encryption?.Dispose();
    }

    private long PageOffset(uint pageNumber) => (long)(pageNumber - 1) * PageSize;

    private void WriteRawPage(uint pageNumber, ReadOnlySpan<byte> page)
    {
        var onDiskPage = _encryption is null
            ? page
            : _encryption.EncryptPage(page, pageNumber);
        _file.Write(PageOffset(pageNumber), onDiskPage);
    }

    private static bool IsAhtolaEncrypted(ReadOnlySpan<byte> header)
        => header.Length >= 5 && header[..5].SequenceEqual("AHTLA"u8);

    private static void ValidateFileLayout(
        long length,
        SqliteDatabaseHeader header,
        bool allowTrailingPages)
    {
        var pageSize = header.PageSize;
        if (length < pageSize)
            throw new InvalidDataException("File is smaller than a single page.");
        if (length % pageSize != 0)
            throw new InvalidDataException("SQLite database file is not a whole number of pages.");

        // The in-header page count is only trustworthy when the change counter
        // matches the version-valid-for field; validate it only in that case.
        var pageCount = length / pageSize;
        if (header.DatabaseSizeInPages != 0
            && header.VersionValidFor == header.ChangeCounter
            && (header.DatabaseSizeInPages > pageCount
                || (!allowTrailingPages && header.DatabaseSizeInPages != pageCount)))
        {
            throw new InvalidDataException(
                $"Header page count {header.DatabaseSizeInPages} disagrees with file size ({pageCount} page(s)).");
        }
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new InvalidOperationException("The page store was opened read-only.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
