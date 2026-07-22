using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

public class SqliteBtreeSplitStorageTests
{
    [Test]
    public void TableRootSplitRoutesEveryKeyAndSurvivesCheckpoint()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var pager = CreatePager(fileSystem, "table-root"))
        {
            SeedTableRoot(pager, 1, 3, 5, 7);

            var mutation = new SqliteBtreeSplitWriter(
                pager,
                new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
                .PrepareTableLeafSplit(leafPageNumber: 1, leftCellCount: 2);

            mutation.WriteImages.Select(image => image.PageNumber).Should().Equal(2, 3, 1);
            mutation.CommitTo(pager);

            pager.CommittedPageCount.Should().Be(3);
            SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).DatabaseSizeInPages.Should().Be(3);
            var root = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(1),
                SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).UsableSpace,
                isFirstPage: true);
            root.Cells.Select(cell => cell.Cell.LeftChildPage).Should().Equal(2);
            root.Cells.Select(cell => cell.Cell.RowId).Should().Equal(3);
            root.Header.RightMostChildPage.Should().Be(3);
            root.SearchChild(1).ChildPage.Should().Be(2);
            root.SearchChild(3).ChildPage.Should().Be(2);
            root.SearchChild(4).ChildPage.Should().Be(3);
            root.SearchChild(7).ChildPage.Should().Be(3);
            ReadTableRowIds(pager, 2).Should().Equal(1, 3);
            ReadTableRowIds(pager, 3).Should().Equal(5, 7);

            pager.CheckpointToMainStore().DatabaseSizeInPages.Should().Be(3);
        }

        using var store = SqlitePageStore.Open(fileSystem, "table-root.db", readOnly: true);
        store.PageCount.Should().Be(3);
        var rootAfterCheckpoint = SqliteTableInteriorPageView.Parse(
            store.ReadPage(1),
            store.Header.UsableSpace,
            isFirstPage: true);
        rootAfterCheckpoint.SearchChild(3).ChildPage.Should().Be(2);
        rootAfterCheckpoint.SearchChild(4).ChildPage.Should().Be(3);
    }

    [Test]
    public void TableParentSplitInstallsChildBeforeParentAndPreservesRoutes()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = CreatePager(fileSystem, "table-parent");
        SeedTableInteriorRoot(pager);

        var mutation = new SqliteBtreeSplitWriter(
            pager,
            new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
            .PrepareTableLeafSplit(leafPageNumber: 2, leftCellCount: 1, parentPageNumber: 1);

        mutation.WriteImages.Select(image => image.PageNumber).Should().Equal(4, 2, 1);
        mutation.CommitTo(pager);

        pager.CommittedPageCount.Should().Be(4);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(1),
            SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).UsableSpace,
            isFirstPage: true);
        root.Cells.Select(cell => (cell.Cell.LeftChildPage, cell.Cell.RowId))
            .Should()
            .Equal((2U, 1L), (4U, 3L));
        root.Header.RightMostChildPage.Should().Be(3);
        root.SearchChild(1).ChildPage.Should().Be(2);
        root.SearchChild(2).ChildPage.Should().Be(4);
        root.SearchChild(3).ChildPage.Should().Be(4);
        root.SearchChild(4).ChildPage.Should().Be(3);
        ReadTableRowIds(pager, 2).Should().Equal(1);
        ReadTableRowIds(pager, 4).Should().Equal(3);
        ReadTableRowIds(pager, 3).Should().Equal(5, 7);
    }

    [Test]
    public void IndexRootThenParentSplitRoutesEveryCompleteRecord()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = CreatePager(fileSystem, "index");
        var records = new[]
        {
            Record(SqlValue.Integer(1)),
            Record(SqlValue.Integer(3)),
            Record(SqlValue.Integer(5)),
            Record(SqlValue.Integer(7)),
        };
        SeedIndexRoot(pager, records);

        var rootSplit = new SqliteBtreeSplitWriter(
            pager,
            new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
            .PrepareIndexLeafSplit(leafPageNumber: 2, leftCellCount: 2);
        rootSplit.WriteImages.Select(image => image.PageNumber).Should().Equal(3, 4, 2);
        rootSplit.CommitTo(pager);

        var parentSplit = new SqliteBtreeSplitWriter(
            pager,
            new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
            .PrepareIndexLeafSplit(leafPageNumber: 3, leftCellCount: 1, parentPageNumber: 2);
        parentSplit.WriteImages.Select(image => image.PageNumber).Should().Equal(5, 3, 2);
        parentSplit.CommitTo(pager);

        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var root = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(2),
            header.UsableSpace,
            header.TextEncoding);
        root.Cells.Select(cell => cell.Cell.LeftChildPage).Should().Equal(3, 5);
        root.GetRecord(0).Should().Equal(records[0]);
        root.GetRecord(1).Should().Equal(records[1]);
        root.Header.RightMostChildPage.Should().Be(4);
        root.SearchChild(records[0]).ChildPage.Should().Be(3);
        root.SearchChild(Record(SqlValue.Integer(2))).ChildPage.Should().Be(5);
        root.SearchChild(records[1]).ChildPage.Should().Be(5);
        root.SearchChild(Record(SqlValue.Integer(4))).ChildPage.Should().Be(4);
        ReadIndexRecords(pager, 3, header).Should().ContainSingle().Which.Should().Equal(records[0]);
        ReadIndexRecords(pager, 5, header).Should().ContainSingle().Which.Should().Equal(records[1]);
        var rightLeafRecords = ReadIndexRecords(pager, 4, header);
        rightLeafRecords.Should().HaveCount(2);
        rightLeafRecords[0].Should().Equal(records[2]);
        rightLeafRecords[1].Should().Equal(records[3]);
    }

    [Test]
    public void EveryInterruptedRootSplitFrameRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= 3; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            using (var pager = CreatePager(fileSystem, $"failure-{failedFrame}"))
            {
                SeedTableRoot(pager, 1, 3, 5, 7);
                var mutation = new SqliteBtreeSplitWriter(
                    pager,
                    new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
                    .PrepareTableLeafSplit(leafPageNumber: 1, leftCellCount: 2);
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);

                Assert.Throws<IOException>(() => mutation.CommitTo(pager));
                pager.State.Should().Be(SqlitePagerState.Faulted);
            }

            using var recovered = SqlitePager.Open(
                fileSystem,
                $"failure-{failedFrame}.db",
                $"failure-{failedFrame}.db-wal");
            recovered.CommittedPageCount.Should().Be(1);
            recovered.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
            var root = SqliteTableLeafPageView.Parse(
                recovered.ReadCommittedPage(1),
                SqliteDatabaseHeader.Parse(recovered.ReadCommittedPage(1)).UsableSpace,
                isFirstPage: true);
            root.Cells.Select(cell => cell.Cell.RowId).Should().Equal(1, 3, 5, 7);
        }
    }

    [Test]
    public void EveryInterruptedParentPropagationFrameRecoversThePriorRouting()
    {
        for (var failedFrame = 1; failedFrame <= 3; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            using (var pager = CreatePager(fileSystem, $"parent-failure-{failedFrame}"))
            {
                SeedTableInteriorRoot(pager);
                var mutation = new SqliteBtreeSplitWriter(
                    pager,
                    new SqliteAppendOnlyPageAllocator(pager.CommittedPageCount))
                    .PrepareTableLeafSplit(leafPageNumber: 2, leftCellCount: 1, parentPageNumber: 1);
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);

                Assert.Throws<IOException>(() => mutation.CommitTo(pager));
                pager.State.Should().Be(SqlitePagerState.Faulted);
            }

            using var recovered = SqlitePager.Open(
                fileSystem,
                $"parent-failure-{failedFrame}.db",
                $"parent-failure-{failedFrame}.db-wal");
            recovered.CommittedPageCount.Should().Be(3);
            recovered.RecoveryInfo.LastCommittedFrameNumber.Should().Be(3);
            var header = SqliteDatabaseHeader.Parse(recovered.ReadCommittedPage(1));
            var root = SqliteTableInteriorPageView.Parse(
                recovered.ReadCommittedPage(1),
                header.UsableSpace,
                isFirstPage: true);
            root.Cells.Select(cell => (cell.Cell.LeftChildPage, cell.Cell.RowId))
                .Should()
                .Equal((2U, 3L));
            root.Header.RightMostChildPage.Should().Be(3);
            root.SearchChild(3).ChildPage.Should().Be(2);
            root.SearchChild(4).ChildPage.Should().Be(3);
            ReadTableRowIds(recovered, 2).Should().Equal(1, 3);
        }
    }

    private static SqlitePager CreatePager(IFileSystem fileSystem, string name)
        => SqlitePager.Create(
            fileSystem,
            $"{name}.db",
            $"{name}.db-wal",
            SqliteWalHeader.Create(
                SqlitePageSize.Minimum,
                salt1: 0x1020_3040,
                salt2: 0x5060_7080,
                checkpointSequence: 1),
            SqliteDatabaseHeader.CreateDefault() with { PageSize = SqlitePageSize.Minimum });

    private static void SeedTableRoot(SqlitePager pager, params long[] rowIds)
    {
        var page = pager.ReadCommittedPage(1);
        var header = SqliteDatabaseHeader.Parse(page);
        var builder = new SqliteTableLeafPageBuilder(pager.PageSize, header.UsableSpace, isFirstPage: true);
        foreach (var rowId in rowIds)
            builder.Append(SqliteTableLeafCell.Create(rowId, [(byte)rowId], header.UsableSpace));
        builder.WriteTo(page);
        CommitPages(pager, targetPageCount: 1, [new SqlitePageImage(1, page)]);
    }

    private static void SeedTableInteriorRoot(SqlitePager pager)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var leftLeaf = BuildTableLeaf(pager.PageSize, header.UsableSpace, 1, 3);
        var rightLeaf = BuildTableLeaf(pager.PageSize, header.UsableSpace, 5, 7);
        var root = pager.ReadCommittedPage(1);
        var rootBuilder = new SqliteTableInteriorPageBuilder(
            pager.PageSize,
            header.UsableSpace,
            rightMostChildPage: 3,
            isFirstPage: true);
        rootBuilder.Append(SqliteTableInteriorCell.Create(2, 3));
        rootBuilder.WriteTo(root);
        WritePageOneCount(root, 3);

        CommitPages(
            pager,
            targetPageCount: 3,
            [
                new SqlitePageImage(2, leftLeaf),
                new SqlitePageImage(3, rightLeaf),
                new SqlitePageImage(1, root),
            ]);
    }

    private static void SeedIndexRoot(SqlitePager pager, IReadOnlyList<byte[]> records)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var root = BuildIndexLeaf(pager.PageSize, header.UsableSpace, records);
        CommitPages(pager, targetPageCount: 2, [new SqlitePageImage(2, root)]);
    }

    private static byte[] BuildTableLeaf(int pageSize, int usableSpace, params long[] rowIds)
    {
        var builder = new SqliteTableLeafPageBuilder(pageSize, usableSpace);
        foreach (var rowId in rowIds)
            builder.Append(SqliteTableLeafCell.Create(rowId, [(byte)rowId], usableSpace));
        return builder.Build();
    }

    private static byte[] BuildIndexLeaf(int pageSize, int usableSpace, IReadOnlyList<byte[]> records)
    {
        var builder = new SqliteIndexLeafPageBuilder(pageSize, usableSpace);
        foreach (var record in records)
            builder.Append(SqliteIndexLeafCell.Create(record, usableSpace));
        return builder.Build();
    }

    private static void CommitPages(
        SqlitePager pager,
        uint targetPageCount,
        IReadOnlyList<SqlitePageImage> images)
    {
        using var transaction = pager.BeginTransaction(targetPageCount);
        foreach (var image in images)
            transaction.WritePage(image.PageNumber, image.Page.Span);
        transaction.Commit();
    }

    private static long[] ReadTableRowIds(SqlitePager pager, uint pageNumber)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        return SqliteTableLeafPageView.Parse(pager.ReadCommittedPage(pageNumber), header.UsableSpace)
            .Cells
            .Select(cell => cell.Cell.RowId)
            .ToArray();
    }

    private static byte[][] ReadIndexRecords(
        SqlitePager pager,
        uint pageNumber,
        SqliteDatabaseHeader header)
    {
        var view = SqliteIndexLeafPageView.Parse(
            pager.ReadCommittedPage(pageNumber),
            header.UsableSpace,
            header.TextEncoding);
        return Enumerable.Range(0, view.Cells.Count).Select(view.GetRecord).ToArray();
    }

    private static void WritePageOneCount(byte[] page, uint pageCount)
    {
        var header = SqliteDatabaseHeader.Parse(page);
        (header with
        {
            DatabaseSizeInPages = pageCount,
            VersionValidFor = header.ChangeCounter,
        }).WriteTo(page);
    }

    private static byte[] Record(params SqlValue[] values) => SqliteRecordCodec.Encode(values);
}
