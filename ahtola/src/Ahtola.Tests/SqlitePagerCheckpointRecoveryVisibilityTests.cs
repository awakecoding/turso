using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqlitePagerCheckpointRecoveryVisibilityTests
{
    [Test]
    public void RetainedWalReportsItsPhysicalCommitAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        var page = CreatePage(SqlitePageSize.Default, 0x21);

        using (var pager = CreatePager(fileSystem, "retained"))
        {
            CommitPageTwo(pager, page);
            pager.CheckpointToMainStore().RetainedCommittedFrameCount.Should().Be(1);
            pager.RecoveryInfo.LastValidFrameNumber.Should().Be(1);
            pager.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
            pager.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeFalse();
        }

        using var reopened = SqlitePager.Open(fileSystem, "retained.db", "retained.db-wal");
        reopened.ReadCommittedPage(2).Should().Equal(page);
        reopened.RecoveryInfo.LastValidFrameNumber.Should().Be(1);
        reopened.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
        reopened.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeFalse();
    }

    [Test]
    public void ResetWalPublishesCheckpointedCommitAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var pager = CreatePager(fileSystem, "reset"))
        {
            CommitPageOne(pager);

            pager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);
            using var wal = SqliteWalFile.Open(fileSystem, "reset.db-wal", readOnly: true);
            wal.ScanRecovery().LastValidFrameNumber.Should().Be(0);
            wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(0);
            AssertCheckpointedMainStore(pager.RecoveryInfo);
        }

        using var reopened = SqlitePager.Open(fileSystem, "reset.db", "reset.db-wal", readOnly: true);
        AssertCheckpointedMainStore(reopened.RecoveryInfo);
    }

    [Test]
    public void ResetCheckpointFailureRetainsPhysicalWalWithoutCheckpointMarker()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);

        using (var pager = CreatePager(fileSystem, "failure"))
        {
            CommitPageOne(pager);
            faults.FailNext(FileSystemOperation.FlushToDisk);

            Assert.Throws<IOException>(() => pager.CheckpointToMainStoreAndResetWal());
            pager.State.Should().Be(SqlitePagerState.Faulted);
            using var wal = SqliteWalFile.Open(fileSystem, "failure.db-wal", readOnly: true);
            wal.ScanRecovery().LastValidFrameNumber.Should().Be(1);
            wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
        }

        using var reopened = SqlitePager.Open(fileSystem, "failure.db", "failure.db-wal");
        reopened.RecoveryInfo.LastValidFrameNumber.Should().Be(1);
        reopened.RecoveryInfo.LastCommittedFrameNumber.Should().Be(1);
    }

    [Test]
    public void EncryptedResetWalPublishesCheckpointedCommitAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = new AhtolaEncryptionOptions(
            AhtolaEncryptionCipher.Aes256Gcm,
            Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"));

        using (var pager = SqlitePager.Create(
                   fileSystem,
                   "encrypted-reset.db",
                   "encrypted-reset.db-wal",
                   CreateWalHeader(),
                   encryption: encryption))
        {
            CommitPageOne(pager);
            pager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);
        }

        using var reopened = SqlitePager.Open(
            fileSystem,
            "encrypted-reset.db",
            "encrypted-reset.db-wal",
            readOnly: true,
            encryption: encryption);
        AssertCheckpointedMainStore(reopened.RecoveryInfo);
    }

    [Test]
    public void CheckpointMarkerFailsClosedWhenTheMainStoreIsNotAuthoritative()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var pager = CreatePager(fileSystem, "non-authoritative"))
        {
            CommitPageOne(pager);
            pager.CheckpointToMainStoreAndResetWal();
        }

        using (var store = SqlitePageStore.Open(fileSystem, "non-authoritative.db"))
        {
            var pageOne = store.ReadPage(1);
            var header = SqliteDatabaseHeader.Parse(pageOne);
            (header with { VersionValidFor = header.ChangeCounter - 1 }).WriteTo(pageOne);
            store.WritePage(1, pageOne);
            store.Flush();
        }

        Assert.Throws<InvalidDataException>(() =>
            SqlitePager.Open(fileSystem, "non-authoritative.db", "non-authoritative.db-wal", readOnly: true));
    }

    [Test]
    public void CheckpointMarkerFailsClosedWhenItsWalHeaderIsTampered()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var pager = CreatePager(fileSystem, "tampered"))
        {
            CommitPageOne(pager);
            pager.CheckpointToMainStoreAndResetWal();
        }

        using (var wal = fileSystem.OpenFile("tampered.db-wal", FileOpenMode.OpenExisting))
            wal.Write(12, [0]);

        Assert.Throws<InvalidDataException>(() =>
            SqlitePager.Open(fileSystem, "tampered.db", "tampered.db-wal", readOnly: true));
    }

    private static SqlitePager CreatePager(InMemoryFileSystem fileSystem, string name)
        => SqlitePager.Create(fileSystem, $"{name}.db", $"{name}.db-wal", CreateWalHeader());

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1020_3040,
            salt2: 0x5060_7080,
            checkpointSequence: 7);

    private static void CommitPageOne(SqlitePager pager)
    {
        var pageOne = pager.ReadCommittedPage(1);
        using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 1);
        transaction.WritePage(1, pageOne);
        transaction.Commit();
    }

    private static void CommitPageTwo(SqlitePager pager, byte[] page)
    {
        using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
        transaction.WritePage(2, page);
        transaction.Commit();
    }

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static void AssertCheckpointedMainStore(SqliteWalRecoveryInfo recovery)
    {
        recovery.LastValidFrameNumber.Should().Be(0);
        recovery.LastCommittedFrameNumber.Should().Be(1);
        recovery.LastCommittedByteLength.Should().Be(SqliteWalHeader.Size);
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
        recovery.IsDurablyCheckpointedMainStore.Should().BeTrue();
    }
}
