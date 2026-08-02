namespace Ahtola.Core.Storage;

/// <summary>
/// Raised when an incremental b-tree mutation would require page merging,
/// rebalancing, defragmentation, or freelist reuse.
/// </summary>
/// <remarks>
/// The incremental writer deliberately implements only the growth half of
/// SQLite's balancing rules: it splits full pages and promotes separators, but
/// it never merges under-full pages, never rewrites the freelist, and never
/// removes a child pointer from a parent. Anything that needs those operations
/// is reported here so the caller can fall back to the complete catalog
/// rewrite, which is always able to represent the mutation.
/// </remarks>
public sealed class SqliteBtreeMaintenanceRequiredException : Exception
{
    public SqliteBtreeMaintenanceRequiredException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The single logical page boundary crossed by incremental b-tree mutation.
/// </summary>
/// <remarks>
/// Every page a cursor reads, every page a mutation dirties, and every page a
/// split allocates passes through this interface. It is therefore the seam at
/// which synthetic I/O failures (short writes, fsync failures, ENOSPC) can be
/// injected without the b-tree code being aware of them: a decorator that
/// throws from <see cref="ReadPage"/>, <see cref="WritePage"/>, or
/// <see cref="AllocatePage"/> exercises the exact failure paths a real device
/// produces.
/// </remarks>
public interface ISqliteBtreePageIo
{
    /// <summary>The physical page size shared by every page.</summary>
    int PageSize { get; }

    /// <summary>The portion of each page usable by SQLite.</summary>
    int UsableSpace { get; }

    /// <summary>The number of pages currently addressable, including allocations.</summary>
    uint PageCount { get; }

    /// <summary>Returns a private copy of one page image.</summary>
    byte[] ReadPage(uint pageNumber);

    /// <summary>Replaces one page image.</summary>
    void WritePage(uint pageNumber, ReadOnlySpan<byte> image);

    /// <summary>Reserves one new page beyond the current page count.</summary>
    uint AllocatePage();
}

/// <summary>
/// An <see cref="ISqliteBtreePageIo"/> that reads through to a committed page
/// source and buffers every dirtied or allocated page in memory.
/// </summary>
/// <remarks>
/// Buffering is what lets a mutation be applied cursor-by-cursor while still
/// being published as one pager transaction whose final size is known before
/// the first frame is written. Only the buffered pages are written, so the cost
/// of a commit is proportional to the pages the mutation actually touched
/// rather than to the size of the database.
/// </remarks>
public sealed class SqliteStagedBtreePageIo : ISqliteBtreePageIo
{
    private readonly Func<uint, byte[]> _readCommittedPage;
    private readonly Dictionary<uint, byte[]> _staged = [];
    private readonly uint _committedPageCount;
    private uint _pageCount;

    /// <summary>Creates a staging layer over a committed page source.</summary>
    public SqliteStagedBtreePageIo(
        Func<uint, byte[]> readCommittedPage,
        uint committedPageCount,
        int pageSize,
        int usableSpace)
    {
        ArgumentNullException.ThrowIfNull(readCommittedPage);
        ArgumentOutOfRangeException.ThrowIfZero(committedPageCount);
        if (usableSpace < SqliteDatabaseHeader.MinimumUsableSpace || usableSpace > pageSize)
            throw new ArgumentOutOfRangeException(nameof(usableSpace));

        _readCommittedPage = readCommittedPage;
        _committedPageCount = committedPageCount;
        _pageCount = committedPageCount;
        PageSize = pageSize;
        UsableSpace = usableSpace;
    }

    /// <inheritdoc />
    public int PageSize { get; }

    /// <inheritdoc />
    public int UsableSpace { get; }

    /// <inheritdoc />
    public uint PageCount => _pageCount;

    /// <summary>The page count this staging layer started from.</summary>
    public uint CommittedPageCount => _committedPageCount;

    /// <summary>The dirtied and newly allocated page images, keyed by page number.</summary>
    public IReadOnlyDictionary<uint, byte[]> StagedPages => _staged;

    /// <summary>The number of pages read from the committed source.</summary>
    public int CommittedPageReadCount { get; private set; }

    /// <inheritdoc />
    public byte[] ReadPage(uint pageNumber)
    {
        ValidatePageNumber(pageNumber);
        if (_staged.TryGetValue(pageNumber, out var staged))
            return (byte[])staged.Clone();

        CommittedPageReadCount++;
        var page = _readCommittedPage(pageNumber);
        if (page.Length != PageSize)
        {
            throw new InvalidDataException(
                $"SQLite page {pageNumber} has {page.Length} bytes, expected {PageSize}.");
        }

        return page;
    }

    /// <inheritdoc />
    public void WritePage(uint pageNumber, ReadOnlySpan<byte> image)
    {
        ValidatePageNumber(pageNumber);
        if (image.Length != PageSize)
            throw new ArgumentException($"A SQLite page image must be exactly {PageSize} bytes.", nameof(image));

        _staged[pageNumber] = image.ToArray();
    }

    /// <inheritdoc />
    public uint AllocatePage()
    {
        var pageNumber = checked(_pageCount + 1);
        _pageCount = pageNumber;
        _staged[pageNumber] = new byte[PageSize];
        return pageNumber;
    }

    private void ValidatePageNumber(uint pageNumber)
    {
        if (pageNumber == 0 || pageNumber > _pageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"SQLite page numbers must be within 1..{_pageCount}.");
        }
    }
}

/// <summary>
/// Allocates and writes a SQLite overflow chain through an
/// <see cref="ISqliteBtreePageIo"/>.
/// </summary>
public static class SqliteOverflowChainWriter
{
    /// <summary>
    /// Writes <paramref name="overflowPayload"/> across freshly allocated
    /// overflow pages and returns the first page of the chain.
    /// </summary>
    public static uint Write(ISqliteBtreePageIo pageIo, ReadOnlySpan<byte> overflowPayload)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        if (overflowPayload.Length == 0)
            throw new ArgumentException("A SQLite overflow chain requires at least one payload byte.", nameof(overflowPayload));

        var capacity = pageIo.UsableSpace - SqliteOverflowPageView.HeaderLength;
        var pageCount = (overflowPayload.Length + capacity - 1) / capacity;
        var pageNumbers = new uint[pageCount];
        for (var index = 0; index < pageCount; index++)
            pageNumbers[index] = pageIo.AllocatePage();

        for (var index = 0; index < pageCount; index++)
        {
            var offset = index * capacity;
            var length = Math.Min(capacity, overflowPayload.Length - offset);
            var next = index + 1 == pageCount ? 0U : pageNumbers[index + 1];
            pageIo.WritePage(
                pageNumbers[index],
                SqliteOverflowPageView.Create(
                    pageIo.PageSize,
                    pageIo.UsableSpace,
                    next,
                    overflowPayload.Slice(offset, length)).ToArray());
        }

        return pageNumbers[0];
    }
}
