namespace Ahtola.Core.Storage;

/// <summary>
/// A page number reserved for a pending SQLite page mutation.
/// </summary>
public readonly record struct SqlitePageAllocation
{
    /// <summary>Validates and creates one page allocation.</summary>
    public SqlitePageAllocation(uint pageNumber, uint databaseSizeInPages)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");
        if (databaseSizeInPages < pageNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(databaseSizeInPages),
                "The database page count cannot be smaller than an allocated page number.");
        }

        PageNumber = pageNumber;
        DatabaseSizeInPages = databaseSizeInPages;
    }

    /// <summary>The non-zero SQLite page number reserved for the mutation.</summary>
    public uint PageNumber { get; }

    /// <summary>The database page count after this allocation is made durable.</summary>
    public uint DatabaseSizeInPages { get; }
}

/// <summary>
/// Reserves unique page numbers for a pending SQLite mutation.
/// </summary>
/// <remarks>
/// Allocation only reserves a page number; it does not write a page image. A
/// future freelist-backed implementation can return a reusable page while
/// preserving the same mutation contract.
/// </remarks>
public interface ISqlitePageAllocator
{
    /// <summary>Reserves one unique page for a pending mutation.</summary>
    SqlitePageAllocation Allocate();
}

/// <summary>
/// A reservation-only allocator that assigns new page numbers at the end of a
/// <see cref="SqlitePageStore"/>.
/// </summary>
/// <remarks>
/// This allocator does not inspect or reclaim the SQLite freelist. Reserved
/// page numbers become durable only when the caller writes the corresponding
/// mutation through a WAL or page-store checkpoint path.
/// </remarks>
public sealed class SqliteAppendOnlyPageAllocator : ISqlitePageAllocator
{
    private uint _nextPageNumber;

    /// <summary>Creates an allocator beginning one page after the store's current end.</summary>
    public SqliteAppendOnlyPageAllocator(SqlitePageStore pageStore)
    {
        ArgumentNullException.ThrowIfNull(pageStore);
        SourceDatabaseSizeInPages = pageStore.PageCount;
        _nextPageNumber = SourceDatabaseSizeInPages == uint.MaxValue
            ? 0
            : SourceDatabaseSizeInPages + 1;
    }

    /// <summary>
    /// Creates an allocator beginning one page after a committed pager view.
    /// </summary>
    public SqliteAppendOnlyPageAllocator(uint sourceDatabaseSizeInPages)
    {
        if (sourceDatabaseSizeInPages == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceDatabaseSizeInPages));

        SourceDatabaseSizeInPages = sourceDatabaseSizeInPages;
        _nextPageNumber = SourceDatabaseSizeInPages == uint.MaxValue
            ? 0
            : SourceDatabaseSizeInPages + 1;
    }

    /// <summary>The page count observed when this allocator was created.</summary>
    public uint SourceDatabaseSizeInPages { get; }

    /// <summary>The next append-only page number, or zero once the range is exhausted.</summary>
    public uint NextPageNumber => _nextPageNumber;

    /// <inheritdoc />
    public SqlitePageAllocation Allocate()
    {
        if (_nextPageNumber == 0)
            throw new InvalidOperationException("SQLite cannot allocate a page beyond UInt32.MaxValue.");

        var pageNumber = _nextPageNumber;
        _nextPageNumber = pageNumber == uint.MaxValue ? 0 : pageNumber + 1;
        return new SqlitePageAllocation(pageNumber, pageNumber);
    }
}
