using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>The durable journal modes implemented by the managed pager.</summary>
public enum SqliteJournalMode
{
    Delete,
    Wal,
}

/// <summary>
/// Writes and recovers SQLite-compatible DELETE-mode rollback journals.
/// Page records contain the exact on-disk page image so encrypted databases
/// can be restored without exposing plaintext in the journal.
/// </summary>
internal static class SqliteRollbackJournal
{
    private const int HeaderSize = 28;
    private const int SectorSize = 512;
    private static ReadOnlySpan<byte> Magic => [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];

    internal static bool IsHot(IFileSystem fileSystem, string journalPath)
    {
        if (!fileSystem.FileExists(journalPath))
            return false;

        using var journal = fileSystem.OpenFile(journalPath, FileOpenMode.OpenExisting, readOnly: true);
        if (journal.Length < Magic.Length)
            return false;

        Span<byte> magic = stackalloc byte[Magic.Length];
        ReadExact(journal, 0, magic, "SQLite rollback journal magic");
        return magic.SequenceEqual(Magic);
    }

    internal static void RecoverIfPresent(
        IFileSystem fileSystem,
        string databasePath,
        string journalPath,
        bool readOnly)
    {
        if (!fileSystem.FileExists(journalPath))
            return;

        if (!IsHot(fileSystem, journalPath))
        {
            if (!readOnly)
                fileSystem.DeleteFile(journalPath);
            return;
        }

        if (readOnly)
        {
            throw new InvalidDataException(
                "Cannot safely open the SQLite database read-only because it has a hot rollback journal. "
                + "Open it writable to recover the journal.");
        }

        using var journal = fileSystem.OpenFile(journalPath, FileOpenMode.OpenExisting, readOnly: true);
        var header = ReadHeader(journal);
        var recordSize = checked((long)header.PageSize + 8);
        var requiredLength = checked((long)SectorSize + (long)header.RecordCount * recordSize);
        if (journal.Length != requiredLength)
        {
            throw new InvalidDataException(
                "Managed recovery supports only the single-segment rollback journals it creates. "
                + "Use SQLite to recover this external rollback journal before reopening it with the managed provider.");
        }

        var page = new byte[header.PageSize];
        Span<byte> pageNumberBytes = stackalloc byte[4];
        Span<byte> checksumBytes = stackalloc byte[4];
        var recordOffset = (long)SectorSize;
        var restoredPages = new HashSet<uint>();
        var pageNumbers = new uint[checked((int)header.RecordCount)];
        for (var index = 0; index < pageNumbers.Length; index++)
        {
            ReadExact(journal, recordOffset, pageNumberBytes, "SQLite rollback journal page number");
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(pageNumberBytes);
            if (pageNumber == 0 || pageNumber > header.InitialDatabasePageCount)
                throw new InvalidDataException($"SQLite rollback journal contains invalid page number {pageNumber}.");
            if (!restoredPages.Add(pageNumber))
                throw new InvalidDataException($"SQLite rollback journal contains duplicate page {pageNumber}.");

            ReadExact(journal, recordOffset + 4, page, $"SQLite rollback journal page {pageNumber}");
            ReadExact(
                journal,
                recordOffset + 4 + header.PageSize,
                checksumBytes,
                $"SQLite rollback journal checksum for page {pageNumber}");
            var expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(checksumBytes);
            var actualChecksum = ComputeChecksum(page, header.ChecksumNonce);
            if (actualChecksum != expectedChecksum)
            {
                throw new InvalidDataException(
                    $"SQLite rollback journal checksum for page {pageNumber} is invalid.");
            }

            pageNumbers[index] = pageNumber;
            recordOffset += recordSize;
        }

        using var database = fileSystem.OpenFile(databasePath, FileOpenMode.OpenExisting);
        recordOffset = SectorSize;
        foreach (var pageNumber in pageNumbers)
        {
            ReadExact(journal, recordOffset + 4, page, $"SQLite rollback journal page {pageNumber}");
            database.Write(checked((long)(pageNumber - 1) * header.PageSize), page);
            recordOffset += recordSize;
        }

        database.SetLength(checked((long)header.InitialDatabasePageCount * header.PageSize));
        database.FlushToDisk();
        journal.Dispose();
        Invalidate(journalPath, fileSystem);
    }

    internal static void Commit(
        IFileSystem fileSystem,
        string journalPath,
        SqlitePageStore pageStore,
        IReadOnlyCollection<uint> pageNumbers,
        Action applyDatabaseChanges)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(journalPath);
        ArgumentNullException.ThrowIfNull(pageStore);
        ArgumentNullException.ThrowIfNull(pageNumbers);
        ArgumentNullException.ThrowIfNull(applyDatabaseChanges);

        var originalPageCount = pageStore.PageCount;
        var pages = pageNumbers
            .Where(pageNumber => pageNumber >= 1 && pageNumber <= originalPageCount)
            .Distinct()
            .OrderBy(pageNumber => pageNumber)
            .ToArray();
        var checksumNonce = unchecked((uint)Random.Shared.NextInt64());

        if (fileSystem.FileExists(journalPath))
            RecoverIfPresent(fileSystem, pageStore.Path, journalPath, readOnly: false);

        var journalCreated = false;
        try
        {
            using (var journal = fileSystem.OpenFile(journalPath, FileOpenMode.CreateNew))
            {
                journalCreated = true;
                var zeroHeader = new byte[SectorSize];
                WriteHeader(
                    zeroHeader,
                    pages.Length,
                    checksumNonce,
                    originalPageCount,
                    pageStore.PageSize,
                    includeMagic: false);
                journal.Write(0, zeroHeader);

                var recordOffset = (long)SectorSize;
                Span<byte> pageNumberBytes = stackalloc byte[4];
                Span<byte> checksumBytes = stackalloc byte[4];
                foreach (var pageNumber in pages)
                {
                    var rawPage = pageStore.ReadRawPage(pageNumber);
                    BinaryPrimitives.WriteUInt32BigEndian(pageNumberBytes, pageNumber);
                    journal.Write(recordOffset, pageNumberBytes);
                    journal.Write(recordOffset + 4, rawPage);
                    BinaryPrimitives.WriteUInt32BigEndian(
                        checksumBytes,
                        ComputeChecksum(rawPage, checksumNonce));
                    journal.Write(recordOffset + 4 + pageStore.PageSize, checksumBytes);
                    recordOffset += pageStore.PageSize + 8L;
                }

                journal.SetLength(recordOffset);
                journal.FlushToDisk();

                Span<byte> durableHeader = stackalloc byte[HeaderSize];
                WriteHeader(
                    durableHeader,
                    pages.Length,
                    checksumNonce,
                    originalPageCount,
                    pageStore.PageSize,
                    includeMagic: true);
                journal.Write(0, durableHeader);
                journal.FlushToDisk();
            }

            applyDatabaseChanges();
            Invalidate(journalPath, fileSystem);
        }
        catch
        {
            if (!journalCreated)
                TryDelete(fileSystem, journalPath);
            throw;
        }
    }

    private static JournalHeader ReadHeader(IFile journal)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(journal, 0, header, "SQLite rollback journal header");
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("SQLite rollback journal magic is invalid.");

        var recordCount = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        var checksumNonce = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        var initialDatabasePageCount = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        var sectorSize = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        var pageSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[24..]));
        if (sectorSize != SectorSize)
        {
            throw new InvalidDataException(
                $"Managed recovery does not support external SQLite rollback-journal sector size {sectorSize}. "
                + "Use SQLite to recover the journal before reopening it with the managed provider.");
        }
        if (recordCount == uint.MaxValue)
        {
            throw new InvalidDataException(
                "Managed recovery does not support external SQLite rollback journals with sentinel record counts. "
                + "Use SQLite to recover the journal before reopening it with the managed provider.");
        }
        _ = SqlitePageSize.Encode(pageSize);
        if (initialDatabasePageCount == 0)
            throw new InvalidDataException("SQLite rollback journal declares an empty original database.");

        return new JournalHeader(recordCount, checksumNonce, initialDatabasePageCount, pageSize);
    }

    private static void WriteHeader(
        Span<byte> destination,
        int recordCount,
        uint checksumNonce,
        uint initialDatabasePageCount,
        int pageSize,
        bool includeMagic)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException($"Rollback journal header requires {HeaderSize} bytes.", nameof(destination));

        destination[..HeaderSize].Clear();
        if (includeMagic)
            Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], checked((uint)recordCount));
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], checksumNonce);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], initialDatabasePageCount);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], SectorSize);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], checked((uint)pageSize));
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> page, uint nonce)
    {
        var checksum = nonce;
        for (var index = page.Length - 200; index >= 0; index -= 200)
            checksum = unchecked(checksum + page[index]);
        return checksum;
    }

    private static void Invalidate(string journalPath, IFileSystem fileSystem)
    {
        using (var journal = fileSystem.OpenFile(journalPath, FileOpenMode.OpenExisting))
        {
            journal.Write(0, new byte[Magic.Length]);
            journal.FlushToDisk();
        }

        TryDelete(fileSystem, journalPath);
    }

    private static void TryDelete(IFileSystem fileSystem, string path)
    {
        try
        {
            fileSystem.DeleteFile(path);
        }
        catch
        {
            // A zeroed journal is not hot. A later writable open retries cleanup.
        }
    }

    private static void ReadExact(IFile file, long offset, Span<byte> destination, string description)
    {
        var read = file.Read(offset, destination);
        if (read != destination.Length)
            throw new InvalidDataException($"{description} is truncated.");
    }

    private readonly record struct JournalHeader(
        uint RecordCount,
        uint ChecksumNonce,
        uint InitialDatabasePageCount,
        int PageSize);
}
