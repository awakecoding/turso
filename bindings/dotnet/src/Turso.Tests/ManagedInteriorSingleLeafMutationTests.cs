using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

[NonParallelizable]
public sealed class ManagedInteriorSingleLeafMutationTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int PayloadLength = 80;
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void OneLevelLeafUpdateAndLeftMaximumDeleteAreBoundedAndReopen()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "interior-single-leaf-mutation.db";
        CreateMinimumPageDatabase(fileSystem, path);

        long deletedId;
        long updatedId;
        long replacementSeparator;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var before = SeedUntilOneLevelInteriorRoot(connection, fileSystem, path);
            var leftLeaf = ReadLeaf(fileSystem, path, before.ChildPages[0]);
            leftLeaf.Cells.Count.Should().BeGreaterThan(1);
            deletedId = before.Separators[0];
            deletedId.Should().Be(leftLeaf.Cells[^1].Cell.RowId);
            replacementSeparator = leftLeaf.Cells[^2].Cell.RowId;
            updatedId = leftLeaf.Cells[0].Cell.RowId;

            var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"DELETE FROM target WHERE id = {deletedId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBeforeDelete).Should().Be(6);

            var afterDelete = ReadTopology(fileSystem, path);
            afterDelete.RootPage.Should().Be(before.RootPage);
            afterDelete.ChildPages.Should().Equal(before.ChildPages);
            afterDelete.Separators[0].Should().Be(replacementSeparator);
            Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);

            var rootBeforeUpdate = ReadPage(fileSystem, path, afterDelete.RootPage);
            var writesBeforeUpdate = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"UPDATE target SET payload = 'updated-{updatedId:D3}-{new string('u', PayloadLength)}' WHERE id = {updatedId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBeforeUpdate).Should().Be(4);
            ReadPage(fileSystem, path, afterDelete.RootPage).Should().Equal(rootBeforeUpdate);
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);
            Text(connection, $"SELECT payload FROM target WHERE id = {updatedId};")
                .Should()
                .Be($"updated-{updatedId:D3}-{new string('u', PayloadLength)}");
        }
    }

    [Test]
    public void LeftMaximumDeleteReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            CreateMinimumPageDatabase(PhysicalFileSystem.Instance, path);
            long deletedId;
            long expectedSeparator;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                var before = SeedUntilOneLevelInteriorRoot(
                    connection,
                    PhysicalFileSystem.Instance,
                    path);
                var leftLeaf = ReadLeaf(PhysicalFileSystem.Instance, path, before.ChildPages[0]);
                deletedId = before.Separators[0];
                expectedSeparator = leftLeaf.Cells[^2].Cell.RowId;
                Execute(connection, $"DELETE FROM target WHERE id = {deletedId};");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
                Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);

            VerifyWithSqlite(path, expectedSeparator);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InterruptedLeftMaximumDeleteRecoversPriorLeafAndParentSeparator()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "interior-single-leaf-delete-recovery.db";
        CreateMinimumPageDatabase(fileSystem, path);
        long deletedId;
        int expectedCount;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var before = SeedUntilOneLevelInteriorRoot(connection, fileSystem, path);
            deletedId = before.Separators[0];
            expectedCount = checked((int)Integer(connection, "SELECT COUNT(*) FROM target;"));

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 2);
            Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM target WHERE id = {deletedId};"));
        }

        using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = recovered.Connect())
        {
            Integer(connection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
            Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(1);
        }

        var recoveredTopology = ReadTopology(fileSystem, path);
        recoveredTopology.Separators[0].Should().Be(deletedId);
    }

    [Test]
    public void EncryptedOneLevelLeftMaximumDeleteReopensReadOnly()
    {
        using var encryption = TursoEncryptionOptions.FromHex(
            TursoEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new TursoEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-interior-single-leaf-delete.db";
        CreateMinimumPageDatabase(fileSystem, path);
        long deletedId;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var before = SeedUntilOneLevelInteriorRoot(connection, fileSystem, path);
            deletedId = before.Separators[0];
            Execute(connection, $"DELETE FROM target WHERE id = {deletedId};");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        Integer(reopenedConnection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);
    }

    private static Topology SeedUntilOneLevelInteriorRoot(
        EmbeddedConnection connection,
        IFileSystem fileSystem,
        string path)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, payload TEXT);");
        Execute(connection, BuildInsert(1, 120));
        var topology = ReadTopology(fileSystem, path);
        if (topology.RootType != SqliteBtreePageType.TableInterior
            || topology.Separators.Count == 0)
        {
            throw new InvalidOperationException("Unable to create a one-level table-interior root.");
        }

        return topology;
    }

    private static void CreateMinimumPageDatabase(IFileSystem fileSystem, string path)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = PageSize };
        using var pager = SqlitePager.Create(
            fileSystem,
            path,
            path + "-wal",
            SqliteWalHeader.Create(PageSize, salt1: 0x1020_3040, salt2: 0x5060_7080),
            header);
    }

    private static Topology ReadTopology(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindRootPage(pager, header);
        var root = SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rootPage));
        if (root.PageType != SqliteBtreePageType.TableInterior)
            return new Topology(rootPage, root.PageType, [], []);

        var interior = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        return new Topology(
            rootPage,
            root.PageType,
            interior.Cells.Select(cell => cell.Cell.RowId).ToArray(),
            interior.Cells.Select(cell => cell.Cell.LeftChildPage)
                .Append(interior.Header.RightMostChildPage)
                .ToArray());
    }

    private static SqliteTableLeafPageView ReadLeaf(IFileSystem fileSystem, string path, uint pageNumber)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        return SqliteTableLeafPageView.Parse(pager.ReadCommittedPage(pageNumber), header.UsableSpace);
    }

    private static byte[] ReadPage(IFileSystem fileSystem, string path, uint pageNumber)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        return pager.ReadCommittedPage(pageNumber);
    }

    private static uint FindRootPage(SqlitePager pager, SqliteDatabaseHeader header)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == "target")[3]
            .AsInteger());
    }

    private static void VerifyWithSqlite(string path, long expectedSeparator)
    {
        var verificationPath = CreateDatabasePath("integrity");
        try
        {
            File.Copy(path, verificationPath, overwrite: true);

            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using var root = sqlite.CreateCommand();
            root.CommandText = $"SELECT max(rowid) FROM target WHERE rowid < {expectedSeparator + 1};";
            Convert.ToInt64(root.ExecuteScalar()).Should().Be(expectedSeparator);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static string InsertStatement(int id)
        => $"INSERT INTO target VALUES ({id}, 'payload-{id:D3}-{new string('x', PayloadLength)}');";

    private static string BuildInsert(int firstId, int count)
        => $"INSERT INTO target VALUES {string.Join(", ", Enumerable.Range(firstId, count).Select(
            id => $"({id}, 'payload-{id:D3}-{new string('x', PayloadLength)}')"))};";

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Integer(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string Text(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-interior-single-leaf-mutation-tests");
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

    private sealed record Topology(
        uint RootPage,
        SqliteBtreePageType RootType,
        IReadOnlyList<long> Separators,
        IReadOnlyList<uint> ChildPages);
}
