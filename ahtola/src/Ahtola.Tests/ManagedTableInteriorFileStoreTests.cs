using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class ManagedTableInteriorFileStoreTests
{
    [Test]
    public void PersistsOneLevelTableInteriorAcrossReopenAndRealSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
                Execute(connection, BuildInsert(1, 120));
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                pager.RecoveryInfo.LastCommittedFrameNumber.Should().BeGreaterThan(0);
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var schema = SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(1),
                    header.UsableSpace,
                    isFirstPage: true);
                var rootPage = checked((uint)schema.Cells
                    .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                    .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
                    .AsInteger());

                var root = SqliteTableInteriorPageView.Parse(
                    pager.ReadCommittedPage(rootPage),
                    header.UsableSpace);
                root.Cells.Should().NotBeEmpty();
                var childPages = root.Cells
                    .Select(cell => cell.Cell.LeftChildPage)
                    .Append(root.Header.RightMostChildPage)
                    .ToArray();
                childPages.Should().OnlyHaveUniqueItems();

                var rowIds = new List<long>();
                for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
                {
                    var leaf = SqliteTableLeafPageView.Parse(
                        pager.ReadCommittedPage(childPages[childIndex]),
                        header.UsableSpace);
                    leaf.Cells.Should().NotBeEmpty();
                    if (childIndex < root.Cells.Count)
                    {
                        leaf.Cells[^1].Cell.RowId.Should().Be(root.Cells[childIndex].Cell.RowId);
                    }

                    rowIds.AddRange(leaf.Cells.Select(cell => cell.Cell.RowId));
                }

                rowIds.Should().Equal(Enumerable.Range(1, 120).Select(value => (long)value));
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                var rows = Query(connection, "SELECT id, value FROM t ORDER BY id;");
                rows.Select(row => row[0].AsInteger())
                    .Should()
                    .Equal(Enumerable.Range(1, 120).Select(value => (long)value));
                rows[^1][1].AsText().Should().Contain("row-120");
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
                Convert.ToInt64(count.ExecuteScalar()).Should().Be(120);
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
    public void InterruptedTableInteriorWriteRecoversThePriorCommittedLeaf()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile("interior-recovery.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'committed');");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, BuildInsert(2, 120)));
        }

        using var recovered = EmbeddedDatabase.OpenFile("interior-recovery.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        var rows = Query(recoveredConnection, "SELECT id, value FROM t ORDER BY id;");
        rows.Should().ContainSingle();
        rows[0][0].AsInteger().Should().Be(1);
        rows[0][1].AsText().Should().Be("committed");
    }

    [Test]
    public void ReopenRejectsInteriorWhoseSeparatorDoesNotMatchItsLeaf()
    {
        var fileSystem = new InMemoryFileSystem();
        SqliteDatabaseHeader header;
        uint rootPage;
        using (var database = EmbeddedDatabase.OpenFile("interior-corrupt.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildInsert(1, 120));
        }

        using (var store = SqlitePageStore.Open(fileSystem, "interior-corrupt.db"))
        {
            header = store.Header;
            var schema = SqliteTableLeafPageView.Parse(
                store.ReadPage(1),
                header.UsableSpace,
                isFirstPage: true);
            rootPage = checked((uint)schema.Cells
                .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
                .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
                .AsInteger());

            var root = SqliteTableInteriorPageView.Parse(
                store.ReadPage(rootPage),
                header.UsableSpace);
            root.Cells.Should().NotBeEmpty();

            var rootImage = store.ReadPage(rootPage);
            rootImage[root.CellPointers[0] + sizeof(uint)] = 0;
            store.WritePage(rootPage, rootImage);
            store.Flush();
        }

        fileSystem.DeleteFile("interior-corrupt.db-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   "interior-corrupt.db-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 1, salt2: 2)))
        {
        }

        var reopen = () => EmbeddedDatabase.OpenFile("interior-corrupt.db", fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*separator*");
    }

    private static string BuildInsert(int firstId, int lastId)
    {
        var rows = Enumerable.Range(firstId, lastId - firstId + 1)
            .Select(id => $"({id}, 'row-{id:D3}-{new string('x', 96)}')");
        return $"INSERT INTO t VALUES {string.Join(", ", rows)};";
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static List<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < row.Length; ordinal++)
                row[ordinal] = statement.GetValue(ordinal);
            rows.Add(row);
        }

        return rows;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-table-interior-file-store-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"table-{Guid.NewGuid():N}.db");
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
}
