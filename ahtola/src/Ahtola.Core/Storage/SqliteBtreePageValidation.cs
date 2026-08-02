using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

internal static class SqliteBtreePageValidation
{
    public static void ValidateFreeblocks(
        ReadOnlySpan<byte> page,
        SqliteBtreePageHeader header,
        int usableSpace,
        int minimumCellStorageLength)
    {
        var offset = header.FirstFreeblockOffset;
        var previousEnd = 0;
        while (offset != 0)
        {
            if (offset < header.CellContentAreaOffset || offset > usableSpace - sizeof(uint))
            {
                throw new InvalidDataException(
                    "SQLite B-tree freeblock starts outside the usable cell-content area.");
            }

            if (offset < previousEnd)
                throw new InvalidDataException("SQLite B-tree freeblocks overlap or are not ordered.");

            var next = BinaryPrimitives.ReadUInt16BigEndian(page[offset..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(page[(offset + sizeof(ushort))..]);
            if (size < minimumCellStorageLength)
                throw new InvalidDataException("SQLite B-tree freeblock is smaller than its header.");

            var end = checked(offset + size);
            if (end > usableSpace)
                throw new InvalidDataException("SQLite B-tree freeblock extends into reserved page space.");
            if (next != 0 && next <= end + (minimumCellStorageLength - 1))
                throw new InvalidDataException("SQLite B-tree freeblock chain overlaps or is not ordered.");

            previousEnd = end;
            offset = next;
        }
    }

    public static void ValidateCellRanges(List<(int Start, int End)> ranges, string cellDescription)
    {
        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Start < ranges[index - 1].End)
                throw new InvalidDataException($"SQLite {cellDescription} cells overlap.");
        }
    }

    public static void ValidateCellsDoNotOverlapFreeblocks(
        ReadOnlySpan<byte> page,
        SqliteBtreePageHeader header,
        IReadOnlyList<(int Start, int End)> cellRanges,
        string cellDescription)
    {
        var freeblockOffset = header.FirstFreeblockOffset;
        while (freeblockOffset != 0)
        {
            var size = BinaryPrimitives.ReadUInt16BigEndian(
                page[(freeblockOffset + sizeof(ushort))..]);
            var freeblockEnd = freeblockOffset + size;
            foreach (var (start, end) in cellRanges)
            {
                if (start < freeblockEnd && freeblockOffset < end)
                    throw new InvalidDataException($"SQLite {cellDescription} cell overlaps a freeblock.");
            }

            freeblockOffset = BinaryPrimitives.ReadUInt16BigEndian(page[freeblockOffset..]);
        }
    }

    public static void RequireNonZeroAndDistinctChild(
        uint childPage,
        ISet<uint> childPages,
        string childDescription)
    {
        if (childPage == 0)
            throw new InvalidDataException($"SQLite {childDescription} child page is zero.");
        if (!childPages.Add(childPage))
            throw new InvalidDataException($"SQLite {childDescription} repeats a child page.");
    }
}
