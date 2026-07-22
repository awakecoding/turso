using System.Diagnostics;
using AwesomeAssertions;
using Turso.Core.Storage;

namespace Turso.Tests;

public sealed class SqlitePagerPortableLockCoordinatorTests
{
    [Test]
    public void CoordinatorContentionReportsConfiguredTimeoutAndReleasesForTheNextOwner()
    {
        var coordinator = new ExclusiveCoordinator();
        var first = new SqlitePagerLockManager(coordinator);
        var second = new SqlitePagerLockManager(coordinator);
        var timeout = TimeSpan.FromMilliseconds(123);

        using (first.EnterWriter())
        {
            var busy = Assert.Throws<SqlitePagerBusyException>(() => second.EnterWriter(timeout));

            busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
            busy.Timeout.Should().Be(timeout);
            coordinator.LastRejectedTimeout.Should().NotBeNull();
            Assert.That(
                coordinator.LastRejectedTimeout!.Value,
                Is.GreaterThan(TimeSpan.Zero).And.LessThanOrEqualTo(timeout));
            second.State.Should().Be(SqlitePagerLockState.Unlocked);
        }

        using (second.EnterWriter())
        {
            second.State.Should().Be(SqlitePagerLockState.Writer);
            coordinator.ReleaseCount.Should().Be(1);
        }

        coordinator.ReleaseCount.Should().Be(2);
    }

    [Test]
    public void CoordinatorCancellationDoesNotRetainLocalWriterOwnership()
    {
        var coordinator = new FailOnceCoordinator(new OperationCanceledException("Lock acquisition cancelled."));
        var locks = new SqlitePagerLockManager(coordinator);

        Assert.Throws<OperationCanceledException>(() => locks.EnterWriter(TimeSpan.FromSeconds(1)));

        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
        using var writer = locks.EnterWriter();
        writer.IsActive.Should().BeTrue();
        coordinator.AcquisitionCount.Should().Be(2);
    }

    [Test]
    public void CoordinatorFailureDoesNotRetainLocalWriterOwnership()
    {
        var coordinator = new FailOnceCoordinator(new IOException("Injected coordinator failure."));
        var locks = new SqlitePagerLockManager(coordinator);

        Assert.Throws<IOException>(() => locks.EnterWriter(TimeSpan.FromSeconds(1)));

        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
        using var writer = locks.EnterWriter();
        writer.IsActive.Should().BeTrue();
        coordinator.AcquisitionCount.Should().Be(2);
    }

    [Test]
    [NonParallelizable]
    public void PhysicalPagerCoordinatesManagedWritersAcrossProcessesOnSupportedPlatforms()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            Assert.Ignore("Physical managed WAL lock coordination requires Windows or Linux byte-range locks.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using (var pager = SqlitePager.Create(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       databasePath + "-wal",
                       CreateWalHeader()))
            using (var writer = pager.BeginTransaction(targetDatabaseSizeInPages: 1))
            {
                RunWriterWorker(databasePath, "busy");
            }

            RunWriterWorker(databasePath, "available");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessPortableWriterWorkerObservesSharedMemoryLock()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_PORTABLE_WAL_LOCK_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var expectedResult = Environment.GetEnvironmentVariable("TURSO_PORTABLE_WAL_LOCK_WORKER_EXPECTED_RESULT")
            ?? throw new InvalidOperationException("The portable WAL lock worker is missing its expected result.");
        switch (expectedResult)
        {
            case "busy":
                var busy = Assert.Throws<SqlitePagerBusyException>(() => SqlitePager.Open(
                    PhysicalFileSystem.Instance,
                    databasePath,
                    databasePath + "-wal",
                    busyTimeout: TimeSpan.Zero));
                busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
                break;
            case "available":
                using (var pager = SqlitePager.Open(
                           PhysicalFileSystem.Instance,
                           databasePath,
                           databasePath + "-wal",
                           busyTimeout: TimeSpan.Zero))
                using (var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 1))
                {
                    transaction.Rollback();
                }

                break;
            default:
                throw new InvalidOperationException("The portable WAL lock worker received an unknown expected result.");
        }
    }

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1122_3344,
            salt2: 0x5566_7788,
            checkpointSequence: 9);

    private static void RunWriterWorker(string databasePath, string expectedResult)
    {
        var testDirectory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = testDirectory.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(
            Path.Combine(testDirectory.FullName, "Turso.Tests.dll"));
        startInfo.ArgumentList.Add(
            "--TestCaseFilter:FullyQualifiedName=Turso.Tests.SqlitePagerPortableLockCoordinatorTests.CrossProcessPortableWriterWorkerObservesSharedMemoryLock");
        startInfo.Environment["TURSO_PORTABLE_WAL_LOCK_WORKER_DATABASE_PATH"] = databasePath;
        startInfo.Environment["TURSO_PORTABLE_WAL_LOCK_WORKER_EXPECTED_RESULT"] = expectedResult;

        using var worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the portable WAL lock worker.");
        if (!worker.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            worker.Kill(entireProcessTree: true);
            Assert.Fail("The portable WAL lock worker did not exit within 30 seconds.");
        }

        var output = worker.StandardOutput.ReadToEnd() + worker.StandardError.ReadToEnd();
        worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{output}");
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sqlite-pager-portable-locking",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class ExclusiveCoordinator : ISqlitePagerLockCoordinator
    {
        private readonly object _gate = new();
        private bool _held;

        internal TimeSpan? LastRejectedTimeout { get; private set; }

        internal int ReleaseCount { get; private set; }

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout)
        {
            lock (_gate)
            {
                if (_held)
                {
                    LastRejectedTimeout = timeout;
                    throw new SqlitePagerBusyException(operation, timeout);
                }

                _held = true;
                return new Lease(this);
            }
        }

        public IDisposable AcquireRecovery(TimeSpan timeout)
            => Acquire(SqlitePagerLockOperation.Writer, timeout);

        private void Release()
        {
            lock (_gate)
            {
                if (!_held)
                    throw new InvalidOperationException("The test coordinator released an unowned lock.");

                _held = false;
                ReleaseCount++;
            }
        }

        private sealed class Lease : IDisposable
        {
            private ExclusiveCoordinator? _owner;

            internal Lease(ExclusiveCoordinator owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release();
            }
        }
    }

    private sealed class FailOnceCoordinator : ISqlitePagerLockCoordinator
    {
        private Exception? _failure;

        internal FailOnceCoordinator(Exception failure) => _failure = failure;

        internal int AcquisitionCount { get; private set; }

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout)
        {
            AcquisitionCount++;
            var failure = Interlocked.Exchange(ref _failure, null);
            if (failure is not null)
                throw failure;

            return new NoOpLease();
        }

        public IDisposable AcquireRecovery(TimeSpan timeout)
            => Acquire(SqlitePagerLockOperation.Writer, timeout);

        private sealed class NoOpLease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
