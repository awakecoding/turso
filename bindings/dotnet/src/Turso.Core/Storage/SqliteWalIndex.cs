using System.Buffers.Binary;

namespace Turso.Core.Storage;

/// <summary>The native byte order used by SQLite's transient WAL-index.</summary>
public enum SqliteWalIndexByteOrder
{
    LittleEndian,
    BigEndian,
}

/// <summary>
/// Optional shared-memory capability required before a filesystem can participate
/// in SQLite-compatible WAL-index coordination.
/// </summary>
/// <remarks>
/// This capability is deliberately separate from <see cref="IFileSystem"/> until
/// a production implementation provides mapped, cross-process-visible memory on
/// every supported physical platform. No managed pager currently consumes it.
/// </remarks>
public interface ISqliteWalSharedMemoryFileSystem
{
    /// <summary>Opens a mapped SQLite WAL shared-memory region.</summary>
    ISqliteWalSharedMemoryMapping OpenSharedMemory(
        string path,
        FileOpenMode mode,
        bool readOnly = false);
}

/// <summary>
/// A cross-process-visible mapping of SQLite's transient WAL shared-memory file.
/// </summary>
/// <remarks>
/// A mapping alone does not establish a coherent WAL snapshot. Future pager code
/// must acquire the SQLite role locks and use <see cref="MemoryBarrier"/> between
/// duplicate header publication before trusting or changing this memory.
/// </remarks>
public interface ISqliteWalSharedMemoryMapping : IDisposable
{
    /// <summary>The current mapped length in bytes.</summary>
    long Length { get; }

    /// <summary>Whether this mapping rejects writes.</summary>
    bool IsReadOnly { get; }

    /// <summary>Copies bytes from the mapping at an absolute offset.</summary>
    void Read(long position, Span<byte> destination);

    /// <summary>Copies bytes to the mapping at an absolute offset.</summary>
    void Write(long position, ReadOnlySpan<byte> source);

    /// <summary>
    /// Publishes prior shared-memory writes before a dependent reader or writer
    /// observes the next WAL-index state.
    /// </summary>
    void MemoryBarrier();
}

/// <summary>Defines the fixed layout of SQLite's 32 KiB WAL-index blocks.</summary>
public static class SqliteWalIndexLayout
{
    /// <summary>Size of one SQLite WAL-index block in bytes.</summary>
    public const int BlockSize = 32 * 1024;

    /// <summary>Bytes occupied by both headers and checkpoint information.</summary>
    public const int HeaderRegionSize = 136;

    /// <summary>Frames indexed by the first block after its header region.</summary>
    public const int FirstBlockFrameCapacity = 4_062;

    /// <summary>Frames indexed by every block after the first.</summary>
    public const int SubsequentBlockFrameCapacity = 4_096;

    /// <summary>Hash slots in every WAL-index block.</summary>
    public const int HashSlotCount = 8_192;

    /// <summary>Returns the zero-based WAL-index block containing a frame.</summary>
    public static int GetBlockIndex(uint frameNumber)
    {
        if (frameNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(frameNumber), "SQLite WAL frame numbers start at one.");

        if (frameNumber <= FirstBlockFrameCapacity)
            return 0;

        return checked((int)((frameNumber - FirstBlockFrameCapacity - 1) / SubsequentBlockFrameCapacity) + 1);
    }

    /// <summary>Returns the byte offset of a frame's page-number slot.</summary>
    public static long GetPageNumberOffset(uint frameNumber)
    {
        var blockIndex = GetBlockIndex(frameNumber);
        var slotIndex = blockIndex == 0
            ? frameNumber - 1
            : (frameNumber - FirstBlockFrameCapacity - 1) % SubsequentBlockFrameCapacity;
        var blockOffset = checked((long)blockIndex * BlockSize);
        var pageNumberOffset = blockIndex == 0
            ? HeaderRegionSize
            : 0;

        return checked(blockOffset + pageNumberOffset + slotIndex * sizeof(uint));
    }

    /// <summary>Returns the byte offset of a block's zero-based hash slot.</summary>
    public static long GetHashSlotOffset(int blockIndex, int hashSlotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        if ((uint)hashSlotIndex >= HashSlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hashSlotIndex),
                $"SQLite WAL-index hash slot must be between zero and {HashSlotCount - 1}.");
        }

        var pageNumberBytes = blockIndex == 0
            ? FirstBlockFrameCapacity * sizeof(uint)
            : SubsequentBlockFrameCapacity * sizeof(uint);
        var blockOffset = checked((long)blockIndex * BlockSize);
        var pageNumberOffset = blockIndex == 0
            ? HeaderRegionSize
            : 0;

        return checked(blockOffset + pageNumberOffset + pageNumberBytes + hashSlotIndex * sizeof(ushort));
    }

    /// <summary>Returns the number of allocated blocks needed to index a frame.</summary>
    public static int GetRequiredBlockCount(uint maximumFrame)
    {
        if (maximumFrame <= FirstBlockFrameCapacity)
            return 1;

        var remainingFrames = maximumFrame - FirstBlockFrameCapacity;
        return checked((int)((remainingFrames + SubsequentBlockFrameCapacity - 1) / SubsequentBlockFrameCapacity) + 1);
    }
}

/// <summary>
/// The validated 48-byte SQLite WAL-index header stored twice at the start of
/// the transient <c>-shm</c> region.
/// </summary>
public sealed record SqliteWalIndexHeader
{
    /// <summary>Size in bytes of one WAL-index header copy.</summary>
    public const int Size = 48;

    /// <summary>The only WAL-index version understood by current SQLite.</summary>
    public const uint CurrentFormatVersion = 3_007_000;

    private SqliteWalIndexHeader(
        uint changeCounter,
        SqliteWalChecksumByteOrder walChecksumByteOrder,
        int pageSize,
        uint maximumFrame,
        uint databasePageCount,
        uint frameChecksum1,
        uint frameChecksum2,
        uint salt1,
        uint salt2,
        uint checksum1,
        uint checksum2)
    {
        ChangeCounter = changeCounter;
        WalChecksumByteOrder = walChecksumByteOrder;
        PageSize = pageSize;
        MaximumFrame = maximumFrame;
        DatabasePageCount = databasePageCount;
        FrameChecksum1 = frameChecksum1;
        FrameChecksum2 = frameChecksum2;
        Salt1 = salt1;
        Salt2 = salt2;
        Checksum1 = checksum1;
        Checksum2 = checksum2;
    }

    /// <summary>The native byte order of the current SQLite WAL-index host.</summary>
    public static SqliteWalIndexByteOrder NativeByteOrder { get; }
        = BitConverter.IsLittleEndian
            ? SqliteWalIndexByteOrder.LittleEndian
            : SqliteWalIndexByteOrder.BigEndian;

    /// <summary>The WAL-index format version.</summary>
    public static uint FormatVersion => CurrentFormatVersion;

    /// <summary>The transaction-change counter published by the writer.</summary>
    public uint ChangeCounter { get; }

    /// <summary>The byte order used by the associated WAL rolling checksums.</summary>
    public SqliteWalChecksumByteOrder WalChecksumByteOrder { get; }

    /// <summary>The database page size represented by this WAL-index.</summary>
    public int PageSize { get; }

    /// <summary>The last valid, committed frame published by the writer.</summary>
    public uint MaximumFrame { get; }

    /// <summary>The committed database size in pages.</summary>
    public uint DatabasePageCount { get; }

    /// <summary>The first checksum word of <see cref="MaximumFrame"/>.</summary>
    public uint FrameChecksum1 { get; }

    /// <summary>The second checksum word of <see cref="MaximumFrame"/>.</summary>
    public uint FrameChecksum2 { get; }

    /// <summary>The first WAL salt copied verbatim from the WAL header.</summary>
    public uint Salt1 { get; }

    /// <summary>The second WAL salt copied verbatim from the WAL header.</summary>
    public uint Salt2 { get; }

    /// <summary>The first checksum word over the preceding header bytes.</summary>
    public uint Checksum1 { get; }

    /// <summary>The second checksum word over the preceding header bytes.</summary>
    public uint Checksum2 { get; }

    /// <summary>
    /// Parses a header written by a SQLite process on this host architecture.
    /// </summary>
    public static SqliteWalIndexHeader Parse(ReadOnlySpan<byte> source)
        => Parse(source, NativeByteOrder);

    /// <summary>
    /// Parses one exact WAL-index header using its host-native byte order.
    /// </summary>
    /// <remarks>
    /// This overload exists for format validation. A live SQLite WAL-index must
    /// only be consumed on a host with the matching native byte order.
    /// </remarks>
    public static SqliteWalIndexHeader Parse(
        ReadOnlySpan<byte> source,
        SqliteWalIndexByteOrder nativeByteOrder)
    {
        RequireExactLength(source.Length, Size, "SQLite WAL-index header");
        ValidateByteOrder(nativeByteOrder);

        var version = ReadUInt32(source, nativeByteOrder);
        if (version != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported SQLite WAL-index format version {version}; expected {CurrentFormatVersion}.");
        }
        if (ReadUInt32(source[4..], nativeByteOrder) != 0)
            throw new InvalidDataException("SQLite WAL-index header padding must be zero.");
        if (source[12] != 1)
            throw new InvalidDataException("SQLite WAL-index header is not initialized.");

        var walChecksumByteOrder = source[13] switch
        {
            0 => SqliteWalChecksumByteOrder.LittleEndian,
            1 => SqliteWalChecksumByteOrder.BigEndian,
            var value => throw new InvalidDataException(
                $"SQLite WAL-index header has invalid big-endian checksum flag {value}."),
        };
        var pageSize = DecodePageSize(ReadUInt16(source[14..], nativeByteOrder));
        var checksum = SqliteWalChecksum.Calculate(
            source[..40],
            ToWalChecksumByteOrder(nativeByteOrder));
        var checksum1 = ReadUInt32(source[40..], nativeByteOrder);
        var checksum2 = ReadUInt32(source[44..], nativeByteOrder);
        if (checksum != (checksum1, checksum2))
            throw new InvalidDataException("SQLite WAL-index header checksum does not match its contents.");

        var maximumFrame = ReadUInt32(source[16..], nativeByteOrder);
        var databasePageCount = ReadUInt32(source[20..], nativeByteOrder);
        if (maximumFrame != 0 && databasePageCount == 0)
        {
            throw new InvalidDataException(
                "SQLite WAL-index header publishes frames without a committed database page count.");
        }

        return new SqliteWalIndexHeader(
            ReadUInt32(source[8..], nativeByteOrder),
            walChecksumByteOrder,
            pageSize,
            maximumFrame,
            databasePageCount,
            ReadUInt32(source[24..], nativeByteOrder),
            ReadUInt32(source[28..], nativeByteOrder),
            BinaryPrimitives.ReadUInt32BigEndian(source[32..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[36..]),
            checksum1,
            checksum2);
    }

    private static int DecodePageSize(ushort encodedPageSize)
    {
        if (encodedPageSize == 1)
            return SqlitePageSize.Maximum;
        if (encodedPageSize < SqlitePageSize.Minimum
            || encodedPageSize > 32 * 1024
            || (encodedPageSize & (encodedPageSize - 1)) != 0)
        {
            throw new InvalidDataException(
                "SQLite WAL-index page size must be 1 for 65536 bytes or a power of two from 512 through 32768 bytes.");
        }

        return encodedPageSize;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, SqliteWalIndexByteOrder byteOrder)
        => byteOrder switch
        {
            SqliteWalIndexByteOrder.LittleEndian => BinaryPrimitives.ReadUInt32LittleEndian(source),
            SqliteWalIndexByteOrder.BigEndian => BinaryPrimitives.ReadUInt32BigEndian(source),
            _ => throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "Unsupported SQLite WAL-index byte order."),
        };

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, SqliteWalIndexByteOrder byteOrder)
        => byteOrder switch
        {
            SqliteWalIndexByteOrder.LittleEndian => BinaryPrimitives.ReadUInt16LittleEndian(source),
            SqliteWalIndexByteOrder.BigEndian => BinaryPrimitives.ReadUInt16BigEndian(source),
            _ => throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "Unsupported SQLite WAL-index byte order."),
        };

    private static SqliteWalChecksumByteOrder ToWalChecksumByteOrder(SqliteWalIndexByteOrder byteOrder)
        => byteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? SqliteWalChecksumByteOrder.LittleEndian
            : SqliteWalChecksumByteOrder.BigEndian;

    private static void ValidateByteOrder(SqliteWalIndexByteOrder byteOrder)
    {
        if (byteOrder is not SqliteWalIndexByteOrder.LittleEndian
            and not SqliteWalIndexByteOrder.BigEndian)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteOrder),
                byteOrder,
                "Unsupported SQLite WAL-index byte order.");
        }
    }

    private static void RequireExactLength(int actualLength, int expectedLength, string structure)
    {
        if (actualLength != expectedLength)
        {
            throw new InvalidDataException(
                $"{structure} must be exactly {expectedLength} bytes; found {actualLength} bytes.");
        }
    }
}

/// <summary>The checkpoint fields following SQLite's duplicated WAL-index headers.</summary>
public sealed record SqliteWalIndexCheckpointInfo(
    uint BackfilledFrameCount,
    uint ReadMark0,
    uint ReadMark1,
    uint ReadMark2,
    uint ReadMark3,
    uint ReadMark4,
    uint BackfillAttemptedFrameCount)
{
    /// <summary>Size in bytes of the checkpoint information and lock area.</summary>
    public const int Size = 40;

    /// <summary>Offset of SQLite's eight lock bytes within the complete header region.</summary>
    public const int LockOffset = 120;

    internal static SqliteWalIndexCheckpointInfo Parse(
        ReadOnlySpan<byte> source,
        uint maximumFrame,
        SqliteWalIndexByteOrder nativeByteOrder)
    {
        if (source.Length != Size)
        {
            throw new InvalidDataException(
                $"SQLite WAL-index checkpoint information must be exactly {Size} bytes; found {source.Length} bytes.");
        }

        var backfilledFrameCount = ReadUInt32(source, nativeByteOrder);
        var backfillAttemptedFrameCount = ReadUInt32(source[32..], nativeByteOrder);
        if (backfilledFrameCount > maximumFrame || backfillAttemptedFrameCount > maximumFrame)
        {
            throw new InvalidDataException(
                "SQLite WAL-index checkpoint information refers to frames beyond the committed WAL boundary.");
        }

        return new SqliteWalIndexCheckpointInfo(
            backfilledFrameCount,
            ReadUInt32(source[4..], nativeByteOrder),
            ReadUInt32(source[8..], nativeByteOrder),
            ReadUInt32(source[12..], nativeByteOrder),
            ReadUInt32(source[16..], nativeByteOrder),
            ReadUInt32(source[20..], nativeByteOrder),
            backfillAttemptedFrameCount);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, SqliteWalIndexByteOrder byteOrder)
        => byteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source)
            : BinaryPrimitives.ReadUInt32BigEndian(source);
}

/// <summary>
/// A validated, internally consistent snapshot of SQLite's 136-byte WAL-index
/// header region.
/// </summary>
/// <remarks>
/// The parser requires both header copies to be valid and identical. A mismatch
/// may be a writer's in-progress second-copy/first-copy publication, a stale
/// mapping, or corruption. Without a mapped retry protocol and shared-memory
/// barrier, accepting either copy could expose an uncommitted frame.
/// </remarks>
public sealed record SqliteWalIndexHeaderRegion(
    SqliteWalIndexHeader Header,
    SqliteWalIndexCheckpointInfo CheckpointInfo)
{
    /// <summary>Parses the header region from a SQLite WAL-index snapshot.</summary>
    public static SqliteWalIndexHeaderRegion Parse(ReadOnlySpan<byte> source)
        => Parse(source, SqliteWalIndexHeader.NativeByteOrder);

    /// <summary>Parses the header region using an explicit native byte order.</summary>
    public static SqliteWalIndexHeaderRegion Parse(
        ReadOnlySpan<byte> source,
        SqliteWalIndexByteOrder nativeByteOrder)
    {
        if (source.Length < SqliteWalIndexLayout.HeaderRegionSize)
        {
            throw new InvalidDataException(
                $"SQLite WAL-index header region must contain at least {SqliteWalIndexLayout.HeaderRegionSize} bytes; found {source.Length} bytes.");
        }

        var firstHeader = SqliteWalIndexHeader.Parse(source[..SqliteWalIndexHeader.Size], nativeByteOrder);
        var secondHeader = SqliteWalIndexHeader.Parse(
            source.Slice(SqliteWalIndexHeader.Size, SqliteWalIndexHeader.Size),
            nativeByteOrder);
        if (firstHeader != secondHeader)
        {
            throw new InvalidDataException(
                "SQLite WAL-index header copies differ; refusing an in-progress, stale, or corrupt publication.");
        }

        var checkpointInfo = SqliteWalIndexCheckpointInfo.Parse(
            source.Slice(SqliteWalIndexHeader.Size * 2, SqliteWalIndexCheckpointInfo.Size),
            firstHeader.MaximumFrame,
            nativeByteOrder);
        return new SqliteWalIndexHeaderRegion(firstHeader, checkpointInfo);
    }
}
