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

    /// <summary>Serializes this header to a new exact-length native-endian buffer.</summary>
    public byte[] ToArray()
    {
        var destination = new byte[Size];
        WriteTo(destination);
        return destination;
    }

    /// <summary>
    /// Serializes this header to an exact-length native-endian destination.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        RequireExactLength(destination.Length, Size, "SQLite WAL-index header destination");
        if (MaximumFrame != 0 && DatabasePageCount == 0)
        {
            throw new InvalidOperationException(
                "SQLite WAL-index header cannot publish frames without a committed database page count.");
        }

        var encodedPageSize = PageSize == SqlitePageSize.Maximum
            ? (ushort)1
            : checked((ushort)PageSize);
        if (DecodePageSize(encodedPageSize) != PageSize)
            throw new InvalidOperationException("SQLite WAL-index header has an invalid page size.");

        WriteUInt32(destination, NativeByteOrder, CurrentFormatVersion);
        WriteUInt32(destination[4..], NativeByteOrder, value: 0);
        WriteUInt32(destination[8..], NativeByteOrder, ChangeCounter);
        destination[12] = 1;
        destination[13] = WalChecksumByteOrder switch
        {
            SqliteWalChecksumByteOrder.LittleEndian => 0,
            SqliteWalChecksumByteOrder.BigEndian => 1,
            _ => throw new InvalidOperationException("SQLite WAL-index header has an unsupported checksum byte order."),
        };
        WriteUInt16(
            destination[14..],
            NativeByteOrder,
            encodedPageSize);
        WriteUInt32(destination[16..], NativeByteOrder, MaximumFrame);
        WriteUInt32(destination[20..], NativeByteOrder, DatabasePageCount);
        WriteUInt32(destination[24..], NativeByteOrder, FrameChecksum1);
        WriteUInt32(destination[28..], NativeByteOrder, FrameChecksum2);
        BinaryPrimitives.WriteUInt32BigEndian(destination[32..], Salt1);
        BinaryPrimitives.WriteUInt32BigEndian(destination[36..], Salt2);

        var checksum = SqliteWalChecksum.Calculate(
            destination[..40],
            ToWalChecksumByteOrder(NativeByteOrder));
        if (checksum != (Checksum1, Checksum2))
            throw new InvalidOperationException("SQLite WAL-index header has stale checksum fields.");

        WriteUInt32(destination[40..], NativeByteOrder, Checksum1);
        WriteUInt32(destination[44..], NativeByteOrder, Checksum2);
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

    private static void WriteUInt32(
        Span<byte> destination,
        SqliteWalIndexByteOrder byteOrder,
        uint value)
    {
        switch (byteOrder)
        {
            case SqliteWalIndexByteOrder.LittleEndian:
                BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
                return;
            case SqliteWalIndexByteOrder.BigEndian:
                BinaryPrimitives.WriteUInt32BigEndian(destination, value);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(byteOrder),
                    byteOrder,
                    "Unsupported SQLite WAL-index byte order.");
        }
    }

    private static void WriteUInt16(
        Span<byte> destination,
        SqliteWalIndexByteOrder byteOrder,
        ushort value)
    {
        switch (byteOrder)
        {
            case SqliteWalIndexByteOrder.LittleEndian:
                BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
                return;
            case SqliteWalIndexByteOrder.BigEndian:
                BinaryPrimitives.WriteUInt16BigEndian(destination, value);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(byteOrder),
                    byteOrder,
                    "Unsupported SQLite WAL-index byte order.");
        }
    }

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

    /// <summary>Number of SQLite WAL read-mark slots.</summary>
    public const int ReadMarkCount = 5;

    /// <summary>Value SQLite uses for an unclaimed read-mark slot.</summary>
    public const uint ReadMarkNotUsed = uint.MaxValue;

    /// <summary>Offset of SQLite's eight lock bytes within the complete header region.</summary>
    public const int LockOffset = 120;

    /// <summary>Returns the read-mark value for a SQLite reader slot.</summary>
    public uint GetReadMark(int readMarkIndex)
        => readMarkIndex switch
        {
            0 => ReadMark0,
            1 => ReadMark1,
            2 => ReadMark2,
            3 => ReadMark3,
            4 => ReadMark4,
            _ => throw new ArgumentOutOfRangeException(
                nameof(readMarkIndex),
                readMarkIndex,
                $"SQLite WAL read-mark index must be between zero and {ReadMarkCount - 1}."),
        };

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

/// <summary>
/// Reads and publishes SQLite WAL-index headers and resolves page numbers through
/// the transient native-endian hash tables.
/// </summary>
/// <remarks>
/// This is deliberately detached from <see cref="SqlitePager"/>. Callers must
/// provide any SQLite role lock required for their operation; the instance lock
/// only serializes operations issued through this instance. A valid result is
/// authenticated against the WAL file and never authorizes pager behavior.
/// </remarks>
public sealed class SqliteWalIndexSharedMemory
{
    private const int StableHeaderReadAttempts = 8;
    private const uint HashMultiplier = 383;

    private readonly object _gate = new();
    private readonly ISqliteWalSharedMemoryMapping _mapping;

    /// <summary>Creates an accessor over an already mapped SQLite <c>-shm</c> region.</summary>
    public SqliteWalIndexSharedMemory(ISqliteWalSharedMemoryMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        _mapping = mapping;
    }

    /// <summary>
    /// Reads a stable dual-header snapshot and validates it against the WAL.
    /// </summary>
    public SqliteWalIndexHeaderRegion ReadValidatedHeader(SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(wal);
        lock (_gate)
        {
            var region = ReadStableHeaderRegion();
            ValidateHeaderAgainstWal(region.Header, wal);
            return region;
        }
    }

    /// <summary>
    /// Publishes a validated WAL-index header using SQLite's second-copy,
    /// barrier, first-copy ordering.
    /// </summary>
    public void PublishHeader(SqliteWalIndexHeader header, SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(wal);

        lock (_gate)
        {
            if (_mapping.IsReadOnly)
                throw new InvalidOperationException("Cannot publish a SQLite WAL-index header through a read-only mapping.");

            EnsureMappedBlocks(SqliteWalIndexLayout.GetRequiredBlockCount(header.MaximumFrame));
            ValidateHeaderAgainstWal(header, wal);

            var bytes = header.ToArray();
            _mapping.Write(SqliteWalIndexHeader.Size, bytes);
            _mapping.MemoryBarrier();
            _mapping.Write(position: 0, bytes);
        }
    }

    /// <summary>
    /// Publishes one nonzero WAL read mark while its caller holds that mark's
    /// exclusive SQLite byte-range lock.
    /// </summary>
    /// <remarks>
    /// Read mark zero is a placeholder for database-only readers and must never
    /// be written. This method deliberately does not acquire a role lock: the
    /// caller owns the cross-process protocol and must downgrade to a shared
    /// lock before exposing the selected boundary to a reader.
    /// </remarks>
    public void PublishReadMark(int readMarkIndex, uint maximumFrame)
    {
        if (readMarkIndex <= 0 || readMarkIndex >= SqliteWalIndexCheckpointInfo.ReadMarkCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readMarkIndex),
                readMarkIndex,
                $"SQLite WAL writable read-mark indexes must be between one and {SqliteWalIndexCheckpointInfo.ReadMarkCount - 1}.");
        }
        if (maximumFrame == 0 || maximumFrame == SqliteWalIndexCheckpointInfo.ReadMarkNotUsed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFrame),
                maximumFrame,
                "SQLite WAL read marks must name a nonzero committed frame.");
        }

        lock (_gate)
        {
            if (_mapping.IsReadOnly)
                throw new InvalidOperationException("Cannot publish a SQLite WAL read mark through a read-only mapping.");

            EnsureMappedBlocks(blockCount: 1);
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            WriteUInt32(bytes, maximumFrame);
            _mapping.Write(
                SqliteWalIndexHeader.Size * 2L + sizeof(uint) + readMarkIndex * sizeof(uint),
                bytes);
            _mapping.MemoryBarrier();
        }
    }

    /// <summary>
    /// Resolves the newest frame for <paramref name="pageNumber"/> within the
    /// currently validated committed WAL boundary, or returns <see langword="null"/>.
    /// </summary>
    public uint? FindFrame(SqliteWalFile wal, uint pageNumber)
    {
        ArgumentNullException.ThrowIfNull(wal);
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite database page numbers start at one.");

        lock (_gate)
        {
            for (var attempt = 0; attempt < StableHeaderReadAttempts; attempt++)
            {
                var region = ReadStableHeaderRegion();
                ValidateHeaderAgainstWal(region.Header, wal);
                var frameNumber = FindFrame(region.Header, pageNumber);
                if (frameNumber is { } frame)
                    ValidateMatchedFrame(wal, region.Header, frame, pageNumber);

                var confirmation = ReadStableHeaderRegion();
                if (region.Header == confirmation.Header)
                    return frameNumber;
            }
        }

        throw new InvalidDataException(
            "SQLite WAL-index header changed while resolving a page number; refusing a stale lookup.");
    }

    private SqliteWalIndexHeaderRegion ReadStableHeaderRegion()
    {
        EnsureMappedBlocks(blockCount: 1);
        InvalidDataException? failure = null;
        for (var attempt = 0; attempt < StableHeaderReadAttempts; attempt++)
        {
            try
            {
                Span<byte> source = stackalloc byte[SqliteWalIndexLayout.HeaderRegionSize];
                _mapping.Read(position: 0, source[..SqliteWalIndexHeader.Size]);
                _mapping.MemoryBarrier();
                _mapping.Read(
                    SqliteWalIndexHeader.Size,
                    source.Slice(SqliteWalIndexHeader.Size, SqliteWalIndexHeader.Size));
                _mapping.Read(
                    SqliteWalIndexHeader.Size * 2,
                    source[(SqliteWalIndexHeader.Size * 2)..]);

                return SqliteWalIndexHeaderRegion.Parse(source);
            }
            catch (InvalidDataException exception)
            {
                failure = exception;
            }
        }

        throw new InvalidDataException(
            $"SQLite WAL-index header remained malformed or torn after {StableHeaderReadAttempts} stable-read attempts.",
            failure);
    }

    private uint? FindFrame(SqliteWalIndexHeader header, uint pageNumber)
    {
        if (header.MaximumFrame == 0)
            return null;

        var blockIndex = SqliteWalIndexLayout.GetBlockIndex(header.MaximumFrame);
        EnsureMappedBlocks(checked(blockIndex + 1));
        for (; blockIndex >= 0; blockIndex--)
        {
            var frameZero = GetBlockFrameZero(blockIndex);
            var frameCapacity = GetBlockFrameCapacity(blockIndex);
            var hashSlot = (int)(unchecked(pageNumber * HashMultiplier)
                                 & (SqliteWalIndexLayout.HashSlotCount - 1));
            uint? result = null;

            for (var probe = 0; probe < SqliteWalIndexLayout.HashSlotCount; probe++)
            {
                var hashValue = ReadUInt16(
                    SqliteWalIndexLayout.GetHashSlotOffset(blockIndex, hashSlot));
                if (hashValue == 0)
                    break;
                if (hashValue > frameCapacity)
                {
                    throw new InvalidDataException(
                        $"SQLite WAL-index hash slot {hashSlot} in block {blockIndex} refers to page-number slot {hashValue}, outside the block.");
                }

                var frameNumber = checked(frameZero + hashValue);
                if (frameNumber <= header.MaximumFrame)
                {
                    var indexedPageNumber = ReadUInt32(
                        SqliteWalIndexLayout.GetPageNumberOffset(frameNumber));
                    if (indexedPageNumber == 0)
                    {
                        throw new InvalidDataException(
                            $"SQLite WAL-index page-number slot for frame {frameNumber} is zero within the committed boundary.");
                    }
                    if (indexedPageNumber == pageNumber
                        && (result is null || frameNumber > result.Value))
                    {
                        result = frameNumber;
                    }
                }

                hashSlot = (hashSlot + 1) & (SqliteWalIndexLayout.HashSlotCount - 1);
            }

            if (result is { })
                return result;
        }

        return null;
    }

    private void EnsureMappedBlocks(int blockCount)
    {
        if (blockCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "SQLite WAL-index block count must be positive.");

        var requiredLength = checked((long)blockCount * SqliteWalIndexLayout.BlockSize);
        if (_mapping.Length < requiredLength)
        {
            throw new InvalidDataException(
                $"SQLite WAL-index mapping is {_mapping.Length} bytes but requires at least {requiredLength} bytes.");
        }
    }

    private static void ValidateHeaderAgainstWal(SqliteWalIndexHeader header, SqliteWalFile wal)
    {
        var walHeader = wal.Header;
        if (header.PageSize != walHeader.PageSize)
            throw new InvalidDataException("SQLite WAL-index page size does not match the WAL header.");
        if (header.WalChecksumByteOrder != walHeader.ChecksumByteOrder)
            throw new InvalidDataException("SQLite WAL-index checksum byte order does not match the WAL header.");
        if (header.Salt1 != walHeader.Salt1 || header.Salt2 != walHeader.Salt2)
            throw new InvalidDataException("SQLite WAL-index salts do not match the WAL header.");

        var recovery = wal.ScanRecovery();
        if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile)
        {
            throw new InvalidDataException(
                $"SQLite WAL contains a {recovery.StopReason} tail; refusing to trust its WAL-index.");
        }
        if (recovery.LastCommittedFrameNumber != header.MaximumFrame)
        {
            throw new InvalidDataException(
                "SQLite WAL-index committed-frame boundary does not match the independently validated WAL.");
        }

        if (header.MaximumFrame == 0)
            return;

        if (recovery.LastCommittedDatabaseSizeInPages != header.DatabasePageCount)
        {
            throw new InvalidDataException(
                "SQLite WAL-index database page count does not match the independently validated WAL.");
        }

        var committedFrame = wal.ReadFrame(header.MaximumFrame);
        if (!committedFrame.Header.IsCommit)
            throw new InvalidDataException("SQLite WAL-index maximum frame is not a WAL commit frame.");
        if (committedFrame.Header.DatabaseSizeInPages != header.DatabasePageCount)
        {
            throw new InvalidDataException(
                "SQLite WAL-index database page count does not match its maximum WAL frame.");
        }
        if (committedFrame.Header.Checksum1 != header.FrameChecksum1
            || committedFrame.Header.Checksum2 != header.FrameChecksum2)
        {
            throw new InvalidDataException(
                "SQLite WAL-index frame checksum does not match its maximum WAL frame.");
        }
    }

    private static void ValidateMatchedFrame(
        SqliteWalFile wal,
        SqliteWalIndexHeader header,
        uint frameNumber,
        uint pageNumber)
    {
        var frame = wal.ReadFrame(frameNumber);
        if (frame.Header.PageNumber != pageNumber)
        {
            throw new InvalidDataException(
                $"SQLite WAL-index frame {frameNumber} maps page {pageNumber} but the WAL frame stores page {frame.Header.PageNumber}.");
        }
        if (frame.Header.Salt1 != header.Salt1 || frame.Header.Salt2 != header.Salt2)
            throw new InvalidDataException("SQLite WAL-index lookup frame salts do not match its header.");
    }

    private uint ReadUInt32(long position)
    {
        Span<byte> source = stackalloc byte[sizeof(uint)];
        _mapping.Read(position, source);
        return SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source)
            : BinaryPrimitives.ReadUInt32BigEndian(source);
    }

    private ushort ReadUInt16(long position)
    {
        Span<byte> source = stackalloc byte[sizeof(ushort)];
        _mapping.Read(position, source);
        return SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(source)
            : BinaryPrimitives.ReadUInt16BigEndian(source);
    }

    private static void WriteUInt32(Span<byte> destination, uint value)
    {
        if (SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
    }

    private static uint GetBlockFrameZero(int blockIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        if (blockIndex == 0)
            return 0;

        return checked(
            (uint)SqliteWalIndexLayout.FirstBlockFrameCapacity
            + checked((uint)(blockIndex - 1) * SqliteWalIndexLayout.SubsequentBlockFrameCapacity));
    }

    private static ushort GetBlockFrameCapacity(int blockIndex)
        => checked((ushort)(blockIndex == 0
            ? SqliteWalIndexLayout.FirstBlockFrameCapacity
            : SqliteWalIndexLayout.SubsequentBlockFrameCapacity));
}
