using AwesomeAssertions;
using Turso.Core.Storage;

namespace Turso.Tests;

public sealed class SqlitePagerBoundedAutomaticRecoveryConcurrencyTests
{
    [Test]
    public void CompetingPagerReportsBoundedRecoveryBusyThenCheckpointsRecoveredCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        var coordinator = new RecoveryGateCoordinator();
        var locks = new SqlitePagerLockManager(coordinator);
        const string databasePath = "automatic-recovery-busy.db";
        const string walPath = databasePath + "-wal";
        var firstPage = CreatePage(SqlitePageSize.Default, 0xA1);
        var replacementPage = CreatePage(SqlitePageSize.Default, 0xB2);

        using var writerPager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(writerPager, firstPage);
        using var recoveringPager = SqlitePager.Open(
            fileSystem,
            databasePath,
            walPath,
            lockManager: locks);

        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0xC3));
            wal.Flush();
        }

        var timeout = TimeSpan.FromMilliseconds(250);
        coordinator.BlockRecovery = true;
        var busy = Assert.Throws<SqlitePagerBusyException>(
            () => recoveringPager.BeginTransaction(targetDatabaseSizeInPages: 2, busyTimeout: timeout));

        busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
        busy.Timeout.Should().Be(timeout);
        coordinator.LastRecoveryTimeout.Should().NotBeNull();
        coordinator.LastRecoveryTimeout!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        coordinator.LastRecoveryTimeout!.Value.Should().BeLessThanOrEqualTo(timeout);
        recoveringPager.State.Should().Be(SqlitePagerState.Ready);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
        recoveringPager.ReadCommittedPage(2).Should().Equal(firstPage);

        coordinator.BlockRecovery = false;
        CommitPageTwo(recoveringPager, replacementPage);
        recoveringPager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeTrue();
        reopened.ReadCommittedPage(2).Should().Equal(replacementPage);
    }

    [Test]
    public void AutomaticRecoveryFlushFaultFaultsOnlyOnePagerAndLeavesPeerCheckpointable()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var locks = new SqlitePagerLockManager(new RecoveryGateCoordinator());
        const string databasePath = "automatic-recovery-fault.db";
        const string walPath = databasePath + "-wal";
        var firstPage = CreatePage(SqlitePageSize.Default, 0xD4);
        var replacementPage = CreatePage(SqlitePageSize.Default, 0xE5);

        using var faultedPager = SqlitePager.Create(
            fileSystem,
            databasePath,
            walPath,
            CreateWalHeader(),
            lockManager: locks);
        CommitPageTwo(faultedPager, firstPage);
        using var peerPager = SqlitePager.Open(
            fileSystem,
            databasePath,
            walPath,
            lockManager: locks);
        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, CreatePage(SqlitePageSize.Default, 0xF6));
            wal.Flush();
        }

        faults.FailNext(FileSystemOperation.FlushToDisk);

        Assert.Throws<IOException>(() => faultedPager.BeginTransaction(targetDatabaseSizeInPages: 2));
        faultedPager.State.Should().Be(SqlitePagerState.Faulted);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        CommitPageTwo(peerPager, replacementPage);
        peerPager.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath, lockManager: locks);
        reopened.RecoveryInfo.IsDurablyCheckpointedMainStore.Should().BeTrue();
        reopened.ReadCommittedPage(2).Should().Equal(replacementPage);
    }

    private static void CommitPageTwo(SqlitePager pager, byte[] pageTwo)
    {
        var pageOne = pager.ReadCommittedPage(1);
        var header = SqliteDatabaseHeader.Parse(pageOne);
        (header with { DatabaseSizeInPages = 2 }).WriteTo(pageOne);

        using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
        transaction.WritePage(2, pageTwo);
        transaction.WritePage(1, pageOne);
        transaction.Commit();
    }

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1020_3040,
            salt2: 0x5060_7080,
            checkpointSequence: 13);

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private sealed class RecoveryGateCoordinator : ISqlitePagerLockCoordinator
    {
        internal bool BlockRecovery { get; set; }

        internal TimeSpan? LastRecoveryTimeout { get; private set; }

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout) => new Lease();

        public IDisposable AcquireRecovery(TimeSpan timeout)
        {
            LastRecoveryTimeout = timeout;
            if (BlockRecovery)
                throw new SqlitePagerBusyException(SqlitePagerLockOperation.Writer, timeout);

            return new Lease();
        }

        private sealed class Lease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
