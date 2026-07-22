using System.Buffers.Binary;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

[NonParallelizable]
public sealed class ManagedBoundedTwoInteriorTablePersistencePressureTests
{
    private const int RowCount = 700;
    private const int PayloadLength = 2_048;

    [Test]
    public void TwoInteriorLevelTablePersistsReopensAndPassesRealSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            CreatePressureTable(path, PhysicalFileSystem.Instance);

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                AssertTwoInteriorLevelTable(pager);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection).Should().Be(RowCount);
                QueryText(connection, RowCount).Should().Be(new string('x', PayloadLength));
            }

            var verificationPath = path + ".verify.db";
            File.Copy(path, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();

                using var integrity = sqlite.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var count = sqlite.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM t;";
                Convert.ToInt64(count.ExecuteScalar()).Should().Be(RowCount);
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(verificationPath);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InterruptedTwoInteriorLevelRewriteRecoversPriorCommittedTable()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile("two-interior-wal-failure.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, BuildPressureInsert()));
        }

        using var recovered = EmbeddedDatabase.OpenFile("two-interior-wal-failure.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        QueryCount(recoveredConnection).Should().Be(0);
    }

    [Test]
    public void EncryptedTwoInteriorLevelTableReopensWithEveryRow()
    {
        var innerFileSystem = new InMemoryFileSystem();
        using var encryption = TursoEncryptionOptions.FromHex(
            TursoEncryptionCipher.Aes256Gcm,
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var fileSystem = new TursoEncryptionFileSystem(innerFileSystem, encryption);

        CreatePressureTable("two-interior-encrypted.db", fileSystem);

        using (var pager = SqlitePager.Open(
                   fileSystem,
                   "two-interior-encrypted.db",
                   "two-interior-encrypted.db-wal",
                   readOnly: true))
        {
            AssertTwoInteriorLevelTable(pager);
        }

        using var reopened = EmbeddedDatabase.OpenFile("two-interior-encrypted.db", fileSystem);
        using var connection = reopened.Connect();
        QueryCount(connection).Should().Be(RowCount);
        QueryText(connection, RowCount).Should().Be(new string('x', PayloadLength));
    }

    [Test]
    public void ReopenRejectsCorruptSecondInteriorLevelSeparator()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "two-interior-corrupt.db";
        SqliteDatabaseHeader header;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildPressureInsert());
        }

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            header = store.Header;
            var rootPage = FindTableRootPage(store.ReadPage(1), header);
            var root = SqliteTableInteriorPageView.Parse(
                store.ReadPage(rootPage),
                header.UsableSpace);
            root.Cells.Should().NotBeEmpty();

            var secondInteriorPage = root.Cells[0].Cell.LeftChildPage;
            var secondInterior = SqliteTableInteriorPageView.Parse(
                store.ReadPage(secondInteriorPage),
                header.UsableSpace);
            secondInterior.Cells.Should().NotBeEmpty();

            var pageImage = store.ReadPage(secondInteriorPage);
            pageImage[secondInterior.CellPointers[0] + sizeof(uint)] = 0;
            store.WritePage(secondInteriorPage, pageImage);
            store.Flush();
        }

        ReplaceWalWithEmptyFile(fileSystem, path, header, salt1: 41, salt2: 43);

        var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*separator*");
    }

    [Test]
    public void FullRewritePersistsTableWithAtLeastThreeInteriorLevels()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "three-interior-full-rewrite.db";
        const int highDepthRowCount = 1_200;
        const int highDepthPayloadLength = 969;
        const long firstRowId = 9_000_000_000_000_000_000L;
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = 512 };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 47, salt2: 53),
                   header))
        {
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildPressureInsert(
                highDepthRowCount,
                highDepthPayloadLength,
                firstRowId));
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var persistedHeader = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), persistedHeader);
            ReadTableHeight(pager, persistedHeader, rootPage, new HashSet<uint>())
                .Should()
                .BeGreaterThanOrEqualTo(4);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(highDepthRowCount);
        QueryText(reopenedConnection, firstRowId + highDepthRowCount - 1).Should()
            .Be(new string('x', highDepthPayloadLength));
    }

    private static void CreatePressureTable(string path, IFileSystem fileSystem)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, BuildPressureInsert());
    }

    private static string BuildPressureInsert(
        int rowCount = RowCount,
        int payloadLength = PayloadLength,
        long firstRowId = 1)
    {
        var payload = new string('x', payloadLength);
        var rows = Enumerable.Range(0, rowCount)
            .Select(offset => $"({firstRowId + offset}, '{payload}')");
        return $"INSERT INTO t VALUES {string.Join(", ", rows)};";
    }

    private static void AssertTwoInteriorLevelTable(SqlitePager pager)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        root.Cells.Should().NotBeEmpty();

        var rowIds = new List<long>();
        foreach (var interiorPage in root.Cells
                     .Select(cell => cell.Cell.LeftChildPage)
                     .Append(root.Header.RightMostChildPage))
        {
            var interior = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(interiorPage),
                header.UsableSpace);
            interior.Cells.Should().NotBeEmpty();
            foreach (var leafPage in interior.Cells
                         .Select(cell => cell.Cell.LeftChildPage)
                         .Append(interior.Header.RightMostChildPage))
            {
                var leaf = SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(leafPage),
                    header.UsableSpace);
                leaf.Cells.Should().NotBeEmpty();
                rowIds.AddRange(leaf.Cells.Select(cell => cell.Cell.RowId));
            }
        }

        rowIds.Should().Equal(Enumerable.Range(1, RowCount).Select(value => (long)value));
    }

    private static uint FindTableRootPage(ReadOnlySpan<byte> schemaPage, SqliteDatabaseHeader header)
    {
        var schema = SqliteTableLeafPageView.Parse(
            schemaPage,
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
            .AsInteger());
    }

    private static int ReadTableHeight(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        ISet<uint> seenPages)
    {
        seenPages.Add(pageNumber).Should().BeTrue();
        var page = pager.ReadCommittedPage(pageNumber);
        var pageHeader = SqliteBtreePageHeader.Parse(page);
        if (pageHeader.PageType == SqliteBtreePageType.TableLeaf)
        {
            var leaf = SqliteTableLeafPageView.Parse(page, header.UsableSpace);
            leaf.Cells.Should().NotBeEmpty();
            return 1;
        }

        pageHeader.PageType.Should().Be(SqliteBtreePageType.TableInterior);
        var interior = SqliteTableInteriorPageView.Parse(page, header.UsableSpace);
        interior.Cells.Should().NotBeEmpty();
        var childHeights = interior.Cells
            .Select(cell => ReadTableHeight(pager, header, cell.Cell.LeftChildPage, seenPages))
            .Append(ReadTableHeight(pager, header, interior.Header.RightMostChildPage, seenPages))
            .ToArray();
        childHeights.Should().OnlyContain(height => height == childHeights[0]);
        return childHeights[0] + 1;
    }

    private static long QueryCount(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("SELECT COUNT(*) FROM t;");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string QueryText(EmbeddedConnection connection, long id)
    {
        using var statement = connection.Prepare($"SELECT value FROM t WHERE id = {id};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-bounded-two-interior-table-persistence-pressure-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private static void ReplaceWalWithEmptyFile(
        IFileSystem fileSystem,
        string path,
        SqliteDatabaseHeader header,
        uint salt1,
        uint salt2)
    {
        fileSystem.DeleteFile(path + "-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1, salt2)))
        {
        }
    }
}
