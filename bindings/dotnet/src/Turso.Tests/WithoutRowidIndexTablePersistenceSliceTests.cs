using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Turso.Tests;

public sealed class WithoutRowidIndexTablePersistenceSliceTests
{
    private const int LargeRowCount = 512;

    [Test]
    public void BinaryPrimaryKeyPersistsAcrossMultipleIndexLeavesAndRealSqliteReadsIt()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE entry(note TEXT, code TEXT PRIMARY KEY, amount INTEGER) WITHOUT ROWID;");
                Execute(connection, BuildLargeInsert(1, LargeRowCount));
                Execute(connection, "UPDATE entry SET note = 'updated' WHERE code = 'key-00100';");
                Execute(connection, "DELETE FROM entry WHERE code = 'key-00300';");
                Execute(connection, "INSERT INTO entry VALUES ('replacement', 'key-00513', 513);");
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var rootPage = FindRootPage(pager, header, "entry");
                SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rootPage)).PageType
                    .Should()
                    .Be(SqliteBtreePageType.IndexInterior);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM entry;").AsInteger().Should().Be(LargeRowCount);
                Scalar(connection, "SELECT note FROM entry WHERE code = 'key-00100';").AsText().Should().Be("updated");
                Scalar(connection, "SELECT COUNT(*) FROM entry WHERE code = 'key-00300';").AsInteger().Should().Be(0);
                Scalar(connection, "SELECT note FROM entry WHERE code = 'key-00513';").AsText().Should().Be("replacement");
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

                using var query = sqlite.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM entry;";
                Convert.ToInt64(query.ExecuteScalar()).Should().Be(LargeRowCount);
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
    public void DuplicateWithoutRowidMutationRejectsBeforeWriting()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var database = EmbeddedDatabase.OpenFile("without-rowid-bounded.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE entry(code TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO entry VALUES ('saved', 'first');");

        var writesBeforeDuplicate = faults.GetOperationCount(FileSystemOperation.Write);
        faults.FailNext(FileSystemOperation.Write);
        var duplicate = () => Execute(connection, "INSERT INTO entry VALUES ('saved', 'duplicate');");
        duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeDuplicate);
        faults.ClearScheduled();

        Scalar(connection, "SELECT value FROM entry WHERE code = 'saved';").AsText().Should().Be("first");
    }

    [TestCase(
        "CREATE TABLE rejected(k TEXT COLLATE NOCASE PRIMARY KEY, value TEXT) WITHOUT ROWID;",
        "uses NOCASE collation")]
    [TestCase(
        "CREATE TABLE rejected(k TEXT COLLATE RTRIM PRIMARY KEY, value TEXT) WITHOUT ROWID;",
        "uses RTRIM collation")]
    [TestCase(
        "CREATE TABLE rejected(k TEXT COLLATE custom_collation PRIMARY KEY, value TEXT) WITHOUT ROWID;",
        "uses CUSTOM_COLLATION collation")]
    [TestCase(
        "CREATE TABLE rejected(k TEXT PRIMARY KEY DESC, value TEXT) WITHOUT ROWID;",
        "is descending")]
    [TestCase(
        "CREATE TABLE rejected(a TEXT, b TEXT, PRIMARY KEY(a, b)) WITHOUT ROWID;",
        "only one ascending BINARY primary-key column")]
    public void UnsupportedWithoutRowidKeyShapesRejectBeforeWriting(string sql, string message)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var database = EmbeddedDatabase.OpenFile("without-rowid-key-reject.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE retained(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");

        var writesBeforeReject = faults.GetOperationCount(FileSystemOperation.Write);
        faults.FailNext(FileSystemOperation.Write);
        var rejected = () => Execute(connection, sql);
        rejected.Should().Throw<EmbeddedSqlException>().WithMessage($"*{message}*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReject);
        faults.ClearScheduled();

        Scalar(connection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
    }

    [Test]
    public void WithoutRowidSecondaryIndexRejectsBeforeWriting()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var database = EmbeddedDatabase.OpenFile("without-rowid-secondary-index.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE entry(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO entry VALUES ('saved', 'key');");

        var writesBeforeReject = faults.GetOperationCount(FileSystemOperation.Write);
        faults.FailNext(FileSystemOperation.Write);
        var rejected = () => Execute(connection, "CREATE INDEX entry_value ON entry(value);");
        rejected.Should().Throw<EmbeddedSqlException>().WithMessage("*secondary indexes*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReject);
        faults.ClearScheduled();

        Scalar(connection, "SELECT value FROM entry WHERE code = 'key';").AsText().Should().Be("saved");
    }

    [Test]
    public void WithoutRowidRootLeafRoundTripsOverflowRecords()
    {
        var fileSystem = new InMemoryFileSystem();
        var payload = new string('x', 10_000);
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-overflow.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, $"INSERT INTO entry VALUES ('{payload}', 'key');");
        }

        using (var pager = SqlitePager.Open(
                   fileSystem,
                   "without-rowid-overflow.db",
                   "without-rowid-overflow.db-wal",
                   readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var root = SqliteIndexLeafPageView.Parse(
                pager.ReadCommittedPage(FindRootPage(pager, header, "entry")),
                header.UsableSpace,
                header.TextEncoding,
                overflowReader: new SqliteOverflowChainReader(pager, header));
            root.Cells.Should().ContainSingle();
            root.Cells[0].Cell.FirstOverflowPage.Should().NotBeNull();
            root.GetRecord(0).Should().Equal(SqliteRecordCodec.Encode([SqlValue.Text("key"), SqlValue.Text(payload)]));
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-overflow.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT value FROM entry WHERE code = 'key';").AsText().Should().Be(payload);
    }

    [Test]
    public void InterruptedWithoutRowidWalMutationRecoversOnlyThePriorCommit()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-wal-recovery.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, "INSERT INTO entry VALUES ('committed', 'one');");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 2);
            Assert.Throws<IOException>(() => Execute(connection, "INSERT INTO entry VALUES ('uncommitted', 'two');"));
        }

        faults.ClearScheduled();
        using var recovered = EmbeddedDatabase.OpenFile("without-rowid-wal-recovery.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        Scalar(recoveredConnection, "SELECT value FROM entry WHERE code = 'one';").AsText().Should().Be("committed");
        Scalar(recoveredConnection, "SELECT COUNT(*) FROM entry WHERE code = 'two';").AsInteger().Should().Be(0);
    }

    [Test]
    public void EncryptedAndReadOnlyReopenRetainTheWithoutRowidIndexTable()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = TursoEncryptionOptions.FromHex(
            TursoEncryptionCipher.Aes256Gcm,
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var encryptedFileSystem = new TursoEncryptionFileSystem(fileSystem, encryption);

        using (var database = EmbeddedDatabase.OpenFile("without-rowid-encrypted.db", encryptedFileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, "INSERT INTO entry VALUES ('persisted', 'key');");
        }

        using (var readOnly = EmbeddedDatabase.OpenFile(
                   "without-rowid-encrypted.db",
                   encryptedFileSystem,
                   readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Scalar(connection, "SELECT value FROM entry WHERE code = 'key';").AsText().Should().Be("persisted");
            var write = () => Execute(connection, "INSERT INTO entry VALUES ('blocked', 'other');");
            write.Should().Throw<EmbeddedSqlException>()
                .WithMessage("attempt to write a readonly database");
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-encrypted.db", encryptedFileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT value FROM entry WHERE code = 'key';").AsText().Should().Be("persisted");
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM entry WHERE code = 'other';").AsInteger().Should().Be(0);
    }

    [Test]
    public void CorruptWithoutRowidRootIsRejectedOnReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        SqliteDatabaseHeader header;
        uint rootPage;
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-corrupt.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, "INSERT INTO entry VALUES ('saved', 'key');");
        }

        using (var store = SqlitePageStore.Open(fileSystem, "without-rowid-corrupt.db"))
        {
            header = store.Header;
            using var pager = SqlitePager.Open(
                fileSystem,
                "without-rowid-corrupt.db",
                "without-rowid-corrupt.db-wal",
                readOnly: true);
            rootPage = FindRootPage(pager, header, "entry");
            var page = store.ReadPage(rootPage);
            page[0] = (byte)SqliteBtreePageType.TableLeaf;
            store.WritePage(rootPage, page);
            store.Flush();
        }

        var reopen = () => EmbeddedDatabase.OpenFile("without-rowid-corrupt.db", fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*WITHOUT ROWID table*");
    }

    private static uint FindRootPage(SqlitePager pager, SqliteDatabaseHeader header, string name)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == name)[3]
            .AsInteger());
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static string BuildLargeInsert(int firstIndex, int count)
        => $"INSERT INTO entry VALUES {string.Join(", ", Enumerable.Range(firstIndex, count)
            .Select(index => $"('note-{index:D5}-{new string('x', 128)}', 'key-{index:D5}', {index})"))};";

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "without-rowid-index-table-persistence-slice-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
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
