namespace Ahtola.Core.Storage;

/// <summary>
/// A cursor-based incremental writer for SQLite rowid-table b-trees.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation descends from the root to exactly one leaf, so the pages read
/// and the pages dirtied are bounded by the tree's height plus the pages a
/// split creates. Nothing else in the tree, and nothing in any other tree, is
/// read or rewritten.
/// </para>
/// <para>
/// Only the growth half of SQLite's balancing rules is implemented: a full page
/// splits and promotes separators into its parent, and a full root deepens the
/// tree while keeping its catalog page number. Merging under-full pages,
/// defragmenting a page's free space, and returning pages to the freelist are
/// deliberately absent; those cases raise
/// <see cref="SqliteBtreeMaintenanceRequiredException"/> so the caller can fall
/// back to a complete rewrite.
/// </para>
/// <para>
/// The managed loader requires an interior separator to equal the exact maximum
/// rowid of its left child. That invariant is preserved without extra work on
/// insertion, because a descent only routes a rowid into a non-right-most child
/// when the rowid is at most that child's separator, and it is restored
/// explicitly after a deletion removes a child's maximum rowid.
/// </para>
/// </remarks>
public sealed class SqliteIncrementalTableBtree
{
    private const int MaximumDepth = 64;

    private readonly ISqliteBtreePageIo _io;

    /// <summary>Creates a writer over one page-access boundary.</summary>
    public SqliteIncrementalTableBtree(ISqliteBtreePageIo pageIo)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        _io = pageIo;
    }

    /// <summary>
    /// Inserts <paramref name="record"/> at a <paramref name="rowId"/> the tree
    /// must not already contain.
    /// </summary>
    /// <remarks>
    /// The absence check is not defensive bookkeeping: it verifies the caller's
    /// idea of the committed contents against the pages actually on disk, at the
    /// cost of the search the insert already performs.
    /// </remarks>
    public void Insert(uint rootPage, long rowId, ReadOnlySpan<byte> record)
    {
        var path = Descend(rootPage, rowId);
        var view = ParseLeaf(path[^1].PageNumber);
        var search = view.Search(rowId);
        if (search.IsExact)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Rowid {rowId} already exists, so the caller's view of the committed table is stale.");
        }

        var cells = view.Cells.Select(cell => cell.Cell).ToList();
        cells.Insert(search.Index, CreateLeafCell(rowId, record));
        WriteLeafAndPropagate(path, cells, appendedAtEnd: search.Index == cells.Count - 1);
    }

    /// <summary>
    /// Replaces the record stored at a <paramref name="rowId"/> the tree must
    /// already contain.
    /// </summary>
    public void Update(uint rootPage, long rowId, ReadOnlySpan<byte> record)
    {
        var path = Descend(rootPage, rowId);
        var view = ParseLeaf(path[^1].PageNumber);
        var search = view.Search(rowId);
        if (!search.IsExact)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Rowid {rowId} is absent, so the caller's view of the committed table is stale.");
        }

        var cells = view.Cells.Select(cell => cell.Cell).ToList();
        if (cells[search.Index].FirstOverflowPage is not null)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Replacing rowid {rowId} would release its overflow chain, which requires freelist maintenance.");
        }

        cells.RemoveAt(search.Index);
        cells.Insert(search.Index, CreateLeafCell(rowId, record));
        WriteLeafAndPropagate(path, cells, appendedAtEnd: false);
    }

    /// <summary>
    /// Removes the row stored at a <paramref name="rowId"/> the tree must
    /// already contain.
    /// </summary>
    public void Delete(uint rootPage, long rowId)
    {
        var path = Descend(rootPage, rowId);
        var leafPage = path[^1].PageNumber;
        var view = ParseLeaf(leafPage);
        var search = view.Search(rowId);
        if (!search.IsExact)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Rowid {rowId} is absent, so the caller's view of the committed table is stale.");
        }

        var cells = view.Cells.Select(cell => cell.Cell).ToList();
        if (cells[search.Index].FirstOverflowPage is not null)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Deleting rowid {rowId} would release its overflow chain, which requires freelist maintenance.");
        }

        var removedMaximum = search.Index == cells.Count - 1;
        cells.RemoveAt(search.Index);
        if (cells.Count == 0 && path.Count > 1)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Deleting rowid {rowId} would empty child page {leafPage}, which requires page merging.");
        }

        WriteSinglePage(leafPage, BuildLeafImage(leafPage, cells));
        if (removedMaximum && cells.Count > 0)
            UpdateSeparatorChain(path, cells[^1].RowId);
    }

    private void WriteLeafAndPropagate(
        List<PathEntry> path,
        List<SqliteTableLeafCell> cells,
        bool appendedAtEnd)
    {
        var leafPage = path[^1].PageNumber;
        var groups = PartitionLeafCells(leafPage, cells, appendedAtEnd);
        if (groups.Count == 1)
        {
            WriteSinglePage(leafPage, BuildLeafImage(leafPage, groups[0]));
            return;
        }

        // Splitting the leaf replaces one child pointer in the parent with one
        // pointer per new page. The right-most group keeps the original page's
        // upper bound, so no ancestor separator above the parent can change.
        var children = new List<ChildLink>(groups.Count);
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var pageNumber = index == 0 && path.Count > 1 ? leafPage : _io.AllocatePage();
            WriteSinglePage(pageNumber, BuildLeafImage(pageNumber, group));
            children.Add(new ChildLink(pageNumber, group[^1].RowId));
        }

        if (path.Count == 1)
        {
            // The root cannot move, so every split page is newly allocated and
            // the root becomes an interior page one level deeper.
            ReplaceRoot(leafPage, children);
            return;
        }

        ReplaceChildLinks(path, path.Count - 2, children);
    }

    /// <summary>
    /// Replaces the child pointer at <paramref name="level"/> with
    /// <paramref name="children"/>, splitting and deepening as required.
    /// </summary>
    private void ReplaceChildLinks(List<PathEntry> path, int level, List<ChildLink> children)
    {
        while (true)
        {
            var entry = path[level];
            var view = ParseInterior(entry.PageNumber);
            var links = ReadChildLinks(view);
            links.RemoveAt(entry.ChildIndex);
            links.InsertRange(entry.ChildIndex, children);

            var groups = PartitionInteriorChildren(entry.PageNumber, links);
            if (groups.Count == 1)
            {
                WriteSinglePage(entry.PageNumber, BuildInteriorImage(entry.PageNumber, groups[0]));
                return;
            }

            var promoted = new List<ChildLink>(groups.Count);
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var pageNumber = index == 0 && level > 0 ? entry.PageNumber : _io.AllocatePage();
                WriteSinglePage(pageNumber, BuildInteriorImage(pageNumber, group));
                promoted.Add(new ChildLink(pageNumber, group[^1].MaximumRowId));
            }

            if (level == 0)
            {
                ReplaceRoot(entry.PageNumber, promoted);
                return;
            }

            children = promoted;
            level--;
        }
    }

    private void ReplaceRoot(uint rootPage, List<ChildLink> children)
    {
        var groups = PartitionInteriorChildren(rootPage, children);
        while (groups.Count > 1)
        {
            var promoted = new List<ChildLink>(groups.Count);
            foreach (var group in groups)
            {
                var pageNumber = _io.AllocatePage();
                WriteSinglePage(pageNumber, BuildInteriorImage(pageNumber, group));
                promoted.Add(new ChildLink(pageNumber, group[^1].MaximumRowId));
            }

            groups = PartitionInteriorChildren(rootPage, promoted);
        }

        WriteSinglePage(rootPage, BuildInteriorImage(rootPage, groups[0]));
    }

    /// <summary>
    /// Restores exact-maximum separators after a deletion lowered the maximum
    /// rowid of the subtree at the end of <paramref name="path"/>.
    /// </summary>
    private void UpdateSeparatorChain(List<PathEntry> path, long maximumRowId)
    {
        for (var level = path.Count - 2; level >= 0; level--)
        {
            var entry = path[level];
            var view = ParseInterior(entry.PageNumber);
            if (entry.ChildIndex == view.Cells.Count)
                continue;

            var links = ReadChildLinks(view);
            links[entry.ChildIndex] = links[entry.ChildIndex] with { MaximumRowId = maximumRowId };
            WriteSinglePage(entry.PageNumber, BuildInteriorImage(entry.PageNumber, links));
            return;
        }
    }

    private List<PathEntry> Descend(uint rootPage, long rowId)
    {
        var path = new List<PathEntry>(8);
        var pageNumber = rootPage;
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            var header = SqliteBtreePageHeader.Parse(_io.ReadPage(pageNumber), IsFirstPage(pageNumber));
            switch (header.PageType)
            {
                case SqliteBtreePageType.TableLeaf:
                    path.Add(new PathEntry(pageNumber, -1));
                    return path;
                case SqliteBtreePageType.TableInterior:
                    {
                        var view = ParseInterior(pageNumber);
                        var child = view.SearchChild(rowId);
                        path.Add(new PathEntry(pageNumber, child.ChildIndex));
                        pageNumber = child.ChildPage;
                        break;
                    }
                default:
                    throw new InvalidDataException(
                        $"SQLite page {pageNumber} is not part of a rowid-table b-tree.");
            }
        }

        throw new InvalidDataException($"SQLite table b-tree rooted at page {rootPage} is deeper than {MaximumDepth} levels.");
    }

    private SqliteTableLeafPageView ParseLeaf(uint pageNumber)
        => SqliteTableLeafPageView.Parse(_io.ReadPage(pageNumber), _io.UsableSpace, IsFirstPage(pageNumber));

    private SqliteTableInteriorPageView ParseInterior(uint pageNumber)
        => SqliteTableInteriorPageView.Parse(_io.ReadPage(pageNumber), _io.UsableSpace, IsFirstPage(pageNumber));

    private static List<ChildLink> ReadChildLinks(SqliteTableInteriorPageView view)
    {
        var links = new List<ChildLink>(view.Cells.Count + 1);
        foreach (var cell in view.Cells)
            links.Add(new ChildLink(cell.Cell.LeftChildPage, cell.Cell.RowId));

        // The right-most child has no separator; long.MaxValue is its implicit
        // upper bound and is replaced when the child is promoted below a parent.
        links.Add(new ChildLink(view.Header.RightMostChildPage, long.MaxValue));
        return links;
    }

    private SqliteTableLeafCell CreateLeafCell(long rowId, ReadOnlySpan<byte> record)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)record.Length),
            _io.UsableSpace);
        if (!layout.UsesOverflow)
            return SqliteTableLeafCell.Create(rowId, record, _io.UsableSpace);

        var firstOverflowPage = SqliteOverflowChainWriter.Write(_io, record[layout.LocalPayloadLength..]);
        return SqliteTableLeafCell.Create(
            rowId,
            checked((ulong)record.Length),
            record[..layout.LocalPayloadLength],
            firstOverflowPage,
            _io.UsableSpace);
    }

    /// <summary>
    /// Splits one leaf's cells across as few pages as they need.
    /// </summary>
    /// <remarks>
    /// SQLite distinguishes the two ways a leaf overflows. An append at the
    /// right edge takes the quick-balance path, which leaves the full page alone
    /// and starts a fresh page, so a monotonically increasing key sequence packs
    /// pages completely. Any other overflow distributes the cells evenly, which
    /// leaves each resulting page roughly half empty so the following insertions
    /// into that neighbourhood do not split again. Packing greedily in the second
    /// case would make every subsequent middle insertion split.
    /// </remarks>
    private List<List<SqliteTableLeafCell>> PartitionLeafCells(
        uint pageNumber,
        List<SqliteTableLeafCell> cells,
        bool appendedAtEnd)
    {
        var capacity = LeafCapacity(pageNumber);
        var costs = new int[cells.Count];
        var total = 0;
        for (var index = 0; index < cells.Count; index++)
        {
            costs[index] = cells[index].EncodedLength + sizeof(ushort);
            if (costs[index] > capacity)
            {
                throw new SqliteBtreeMaintenanceRequiredException(
                    $"A SQLite table-leaf cell of {cells[index].EncodedLength} bytes does not fit in page {pageNumber}.");
            }

            total += costs[index];
        }

        if (total <= capacity)
            return [cells];

        var minimumPageCount = MinimumPageCount(costs, capacity);
        if (appendedAtEnd)
            return Distribute(cells, costs, capacity);

        // Aim for an even distribution, but never spend more pages than the
        // cells strictly need.
        var even = Distribute(cells, costs, (total + minimumPageCount - 1) / minimumPageCount);
        return even.Count <= minimumPageCount ? even : Distribute(cells, costs, capacity);
    }

    private static List<List<SqliteTableLeafCell>> Distribute(
        List<SqliteTableLeafCell> cells,
        int[] costs,
        int budget)
    {
        var groups = new List<List<SqliteTableLeafCell>> { new List<SqliteTableLeafCell>() };
        var used = 0;
        for (var index = 0; index < cells.Count; index++)
        {
            if (used + costs[index] > budget && groups[^1].Count > 0)
            {
                groups.Add([]);
                used = 0;
            }

            groups[^1].Add(cells[index]);
            used += costs[index];
        }

        return groups;
    }

    /// <summary>
    /// The fewest pages that can hold <paramref name="costs"/> in order.
    /// </summary>
    private static int MinimumPageCount(int[] costs, int capacity)
    {
        var pageCount = 1;
        var used = 0;
        foreach (var cost in costs)
        {
            if (used + cost > capacity)
            {
                pageCount++;
                used = 0;
            }

            used += cost;
        }

        return pageCount;
    }

    private List<List<ChildLink>> PartitionInteriorChildren(uint pageNumber, List<ChildLink> links)
    {
        var capacity = InteriorCapacity(pageNumber);
        var groups = new List<List<ChildLink>> { new List<ChildLink>() };
        var used = 0;
        for (var index = 0; index < links.Count; index++)
        {
            // Every child except the group's last one needs a separator cell, so
            // charging one to each child is conservative by exactly one cell.
            var cost = SqliteTableInteriorCell.ChildPointerLength
                + SqliteVarint.GetLength(unchecked((ulong)links[index].MaximumRowId))
                + sizeof(ushort);
            if (used + cost > capacity && groups[^1].Count > 0)
            {
                groups.Add([]);
                used = 0;
            }

            groups[^1].Add(links[index]);
            used += cost;
        }

        // A one-child group cannot carry a separator for a following group, so
        // rebalance the tail rather than emitting a degenerate page.
        if (groups.Count > 1 && groups[^1].Count == 1 && groups[^2].Count > 1)
        {
            var moved = groups[^2][^1];
            groups[^2].RemoveAt(groups[^2].Count - 1);
            groups[^1].Insert(0, moved);
        }

        return groups;
    }

    private byte[] BuildLeafImage(uint pageNumber, List<SqliteTableLeafCell> cells)
    {
        var isFirstPage = IsFirstPage(pageNumber);
        var builder = new SqliteTableLeafPageBuilder(_io.PageSize, _io.UsableSpace, isFirstPage);
        foreach (var cell in cells)
            builder.Append(cell);

        var image = isFirstPage ? _io.ReadPage(pageNumber) : new byte[_io.PageSize];
        builder.WriteTo(image);
        return image;
    }

    private byte[] BuildInteriorImage(uint pageNumber, List<ChildLink> links)
    {
        if (links.Count == 0)
            throw new InvalidOperationException("A SQLite table-interior page requires at least one child.");

        var isFirstPage = IsFirstPage(pageNumber);
        var builder = new SqliteTableInteriorPageBuilder(
            _io.PageSize,
            _io.UsableSpace,
            links[^1].PageNumber,
            isFirstPage);
        for (var index = 0; index < links.Count - 1; index++)
            builder.Append(SqliteTableInteriorCell.Create(links[index].PageNumber, links[index].MaximumRowId));

        var image = isFirstPage ? _io.ReadPage(pageNumber) : new byte[_io.PageSize];
        builder.WriteTo(image);
        return image;
    }

    private void WriteSinglePage(uint pageNumber, ReadOnlySpan<byte> image)
        => _io.WritePage(pageNumber, image);

    private int LeafCapacity(uint pageNumber)
        => _io.UsableSpace
           - (IsFirstPage(pageNumber) ? SqliteBtreePageHeader.FirstPageOffset : 0)
           - SqliteBtreePageHeader.LeafHeaderSize;

    private int InteriorCapacity(uint pageNumber)
        => _io.UsableSpace
           - (IsFirstPage(pageNumber) ? SqliteBtreePageHeader.FirstPageOffset : 0)
           - SqliteBtreePageHeader.InteriorHeaderSize;

    private static bool IsFirstPage(uint pageNumber) => pageNumber == 1;

    private readonly record struct PathEntry(uint PageNumber, int ChildIndex);

    private readonly record struct ChildLink(uint PageNumber, long MaximumRowId);
}
