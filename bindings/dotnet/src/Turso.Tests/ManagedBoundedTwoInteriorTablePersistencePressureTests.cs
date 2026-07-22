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
    private const int BoundedMutationPageSize = SqlitePageSize.Minimum;
    private const int BoundedMutationRowCount = 700;
    private const int BoundedMutationPayloadLength = 80;
    private const string BoundedMutationEncryptionKey =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

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
    public void NestedLeafMaximumDeleteRewritesOnlyItsNonRootParentSeparatorAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("nested-leaf-delete-integrity");
        try
        {
            CreateBoundedNestedTable(path, PhysicalFileSystem.Instance);
            var target = FindNestedLeafTarget(PhysicalFileSystem.Instance, path);
            byte[] rootBefore;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                rootBefore = pager.ReadCommittedPage(target.RootPage);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

            AssertNestedLeafDeletion(
                PhysicalFileSystem.Instance,
                path,
                target,
                rootBefore,
                BoundedMutationRowCount - 1);

            VerifyNestedMutationWithSqlite(path, BoundedMutationRowCount - 1, target.DeletedRowId);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void NestedLeafMaximumDeleteUsesOnlyLeafParentAndPageOneWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "nested-leaf-delete-bounded.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedLeafTarget(fileSystem, path);
        byte[] rootBefore;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
            rootBefore = pager.ReadCommittedPage(target.RootPage);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBeforeDelete).Should().Be(6);
        }

        AssertNestedLeafDeletion(
            fileSystem,
            path,
            target,
            rootBefore,
            BoundedMutationRowCount - 1);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(BoundedMutationRowCount - 1);
    }

    [Test]
    public void NestedRightmostLeafMaximumDeletePropagatesItsParentBoundaryToTheRoot()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "nested-rightmost-leaf-delete.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedRightmostLeafTarget(fileSystem, path);
        byte[] parentBefore;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
            parentBefore = pager.ReadCommittedPage(target.ParentPage);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

        using var committed = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(committed.ReadCommittedPage(1));
        var root = SqliteTableInteriorPageView.Parse(
            committed.ReadCommittedPage(target.RootPage),
            header.UsableSpace);
        root.Cells[target.RootParentIndex].Cell.LeftChildPage.Should().Be(target.ParentPage);
        root.Cells[target.RootParentIndex].Cell.RowId.Should().Be(target.ReplacementSeparator);
        committed.ReadCommittedPage(target.ParentPage).Should().Equal(parentBefore);

        var leaf = SqliteTableLeafPageView.Parse(
            committed.ReadCommittedPage(target.LeafPage),
            header.UsableSpace);
        leaf.Search(target.DeletedRowId).IsExact.Should().BeFalse();
        leaf.Cells[^1].Cell.RowId.Should().Be(target.ReplacementSeparator);
    }

    [Test]
    public void InterruptedNestedLeafParentSeparatorFramesRecoverThePriorCommittedTree()
    {
        for (var failedWrite = 1; failedWrite <= 3; failedWrite++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"nested-leaf-delete-wal-{failedWrite}.db";
            CreateBoundedNestedTable(path, fileSystem);
            var target = FindNestedLeafTarget(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedWrite);
                Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                QueryCount(connection).Should().Be(BoundedMutationRowCount);
                QueryText(connection, target.DeletedRowId).Should().Be(new string('x', BoundedMutationPayloadLength));
            }

            using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var parent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(target.ParentPage),
                header.UsableSpace);
            parent.Cells[target.ParentCellIndex].Cell.RowId.Should().Be(target.DeletedRowId);
        }
    }

    [Test]
    public void EncryptedNestedLeafMaximumDeleteReopensReadOnly()
    {
        using var encryption = TursoEncryptionOptions.FromHex(
            TursoEncryptionCipher.Aes256Gcm,
            BoundedMutationEncryptionKey);
        var fileSystem = new TursoEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-nested-leaf-delete.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedLeafTarget(fileSystem, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(BoundedMutationRowCount - 1);
    }

    [Test]
    public void NestedLeafMaximumDeleteCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "nested-leaf-delete-read-only.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedLeafTarget(fileSystem, path);
        var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = database.Connect())
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};"));

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeDelete);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(BoundedMutationRowCount);
    }

    [Test]
    public void ReopenRejectsCorruptNestedLeafParentSeparatorBeforeMutation()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "nested-leaf-delete-corrupt.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedLeafTarget(fileSystem, path);
        SqliteDatabaseHeader header;

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            header = store.Header;
            var parent = SqliteTableInteriorPageView.Parse(
                store.ReadPage(target.ParentPage),
                header.UsableSpace);
            var corruptedParent = store.ReadPage(target.ParentPage);
            corruptedParent[parent.CellPointers[target.ParentCellIndex] + sizeof(uint)] = 0;
            store.WritePage(target.ParentPage, corruptedParent);
            store.Flush();
        }

        ReplaceWalWithEmptyFile(fileSystem, path, header, salt1: 73, salt2: 79);
        var writesBeforeReopen = faults.GetOperationCount(FileSystemOperation.Write);
        var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*separator*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReopen);
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

    [Test]
    public void DeletingFromAThirdInteriorLevelFallsBackToTheSafeFullRewrite()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "third-interior-delete-full-rewrite.db";
        const int rowCount = 1_200;
        const int payloadLength = 969;
        const long firstRowId = 9_000_000_000_000_000_000L;
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = BoundedMutationPageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 83, salt2: 89),
                   header))
        {
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildPressureInsert(rowCount, payloadLength, firstRowId));
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var persistedHeader = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), persistedHeader);
            ReadTableHeight(pager, persistedHeader, rootPage, new HashSet<uint>())
                .Should()
                .BeGreaterThanOrEqualTo(4);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"DELETE FROM t WHERE id = {firstRowId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBeforeDelete).Should().BeGreaterThan(6);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(rowCount - 1);
        QueryText(reopenedConnection, firstRowId + 1).Should().Be(new string('x', payloadLength));
    }

    private static void CreateBoundedNestedTable(string path, IFileSystem fileSystem)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = BoundedMutationPageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 0x1020_3040, salt2: 0x5060_7080),
                   header))
        {
        }

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, BuildPressureInsert(BoundedMutationRowCount, BoundedMutationPayloadLength));
    }

    private static NestedLeafTarget FindNestedLeafTarget(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        root.Cells.Should().NotBeEmpty();

        for (var parentIndex = 0; parentIndex < root.Cells.Count; parentIndex++)
        {
            var parentPage = root.Cells[parentIndex].Cell.LeftChildPage;
            var parent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(parentPage),
                header.UsableSpace);
            for (var leafIndex = 0; leafIndex < parent.Cells.Count; leafIndex++)
            {
                var leafPage = parent.Cells[leafIndex].Cell.LeftChildPage;
                var leaf = SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(leafPage),
                    header.UsableSpace);
                if (leaf.Cells.Count < 2)
                    continue;

                var deletedRowId = leaf.Cells[^1].Cell.RowId;
                deletedRowId.Should().Be(parent.Cells[leafIndex].Cell.RowId);
                return new NestedLeafTarget(
                    rootPage,
                    parentPage,
                    leafPage,
                    parentIndex,
                    leafIndex,
                    deletedRowId,
                    leaf.Cells[^2].Cell.RowId);
            }
        }

        throw new InvalidOperationException(
            "Unable to create a two-interior-level table with a non-rightmost multi-cell leaf.");
    }

    private static NestedLeafTarget FindNestedRightmostLeafTarget(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        root.Cells.Should().NotBeEmpty();

        for (var parentIndex = 0; parentIndex < root.Cells.Count; parentIndex++)
        {
            var parentPage = root.Cells[parentIndex].Cell.LeftChildPage;
            var parent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(parentPage),
                header.UsableSpace);
            var leafPage = parent.Header.RightMostChildPage;
            var leaf = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(leafPage),
                header.UsableSpace);
            if (leaf.Cells.Count < 2)
                continue;

            var deletedRowId = leaf.Cells[^1].Cell.RowId;
            deletedRowId.Should().Be(root.Cells[parentIndex].Cell.RowId);
            return new NestedLeafTarget(
                rootPage,
                parentPage,
                leafPage,
                parentIndex,
                parent.Cells.Count,
                deletedRowId,
                leaf.Cells[^2].Cell.RowId);
        }

        throw new InvalidOperationException(
            "Unable to create a two-interior-level table with a multi-cell right-most child leaf.");
    }

    private static void AssertNestedLeafDeletion(
        IFileSystem fileSystem,
        string path,
        NestedLeafTarget target,
        ReadOnlySpan<byte> rootBefore,
        int expectedRowCount)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.FirstFreelistTrunkPage.Should().Be(0);
        header.FreelistPageCount.Should().Be(0);
        SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage)
            .PageNumbers
            .Should()
            .BeEmpty();
        pager.ReadCommittedPage(target.RootPage).Should().Equal(rootBefore.ToArray());

        var parent = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(target.ParentPage),
            header.UsableSpace);
        parent.Cells[target.ParentCellIndex].Cell.LeftChildPage.Should().Be(target.LeafPage);
        parent.Cells[target.ParentCellIndex].Cell.RowId.Should().Be(target.ReplacementSeparator);

        var leaf = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(target.LeafPage),
            header.UsableSpace);
        leaf.Search(target.DeletedRowId).IsExact.Should().BeFalse();
        leaf.Cells[^1].Cell.RowId.Should().Be(target.ReplacementSeparator);
        ReadTableHeight(pager, header, target.RootPage, new HashSet<uint>()).Should().Be(3);

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = database.Connect();
        QueryCount(connection).Should().Be(expectedRowCount);
        QueryText(connection, target.ReplacementSeparator)
            .Should()
            .Be(new string('x', BoundedMutationPayloadLength));
    }

    private static void VerifyNestedMutationWithSqlite(
        string path,
        int expectedRowCount,
        long deletedRowId)
    {
        var verificationPath = CreateDatabasePath("nested-leaf-delete-verify");
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
            sqlite.Open();

            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using (var count = sqlite.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM t;";
                Convert.ToInt64(count.ExecuteScalar()).Should().Be(expectedRowCount);
            }

            using var deleted = sqlite.CreateCommand();
            deleted.CommandText = $"SELECT COUNT(*) FROM t WHERE id = {deletedRowId};";
            Convert.ToInt64(deleted.ExecuteScalar()).Should().Be(0);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
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

    private sealed record NestedLeafTarget(
        uint RootPage,
        uint ParentPage,
        uint LeafPage,
        int RootParentIndex,
        int ParentCellIndex,
        long DeletedRowId,
        long ReplacementSeparator);
}
