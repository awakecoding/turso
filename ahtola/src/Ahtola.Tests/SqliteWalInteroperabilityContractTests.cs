using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Characterizes the WAL interoperability contract documented in
/// <c>docs/wal-interoperability-contract.md</c>. These tests pin the observable
/// Stage 0 boundary between the managed pager and ordinary SQLite / Turso
/// clients: the <c>-shm</c> file is a byte-lock carrier only, managed roles
/// occupy exactly SQLite's reserved lock-byte range, and managed lock demands
/// are deliberately stricter than the shared Turso/SQLite WAL protocol until
/// later stages land.
/// </summary>
/// <remarks>
/// External contention is produced from a worker process because POSIX record
/// locks are process-scoped: a second handle inside this process would not
/// contend with the managed coordinator on Linux.
/// </remarks>
public class SqliteWalInteroperabilityContractTests
{
    private const long SharedMemoryLockAreaOffset = 120;
    private const long WriteLockOffset = 120;
    private const long CheckpointLockOffset = 121;
    private const long RecoveryLockOffset = 122;
    private const long FirstReadMarkLockOffset = 123;
    private const int ReadMarkLockCount = 5;
    private const long SharedMemoryLockAreaLength = 8;

    [Test]
    [NonParallelizable]
    public void ManagedWalActivityNeverMaterializesASqliteWalIndex()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            var walPath = databasePath + "-wal";
            var sharedMemoryPath = databasePath + "-shm";
            using var pager = CreatePhysicalPager(databasePath);

            CommitPageTwo(pager, CreatePage(pager.PageSize, 0x41));

            new FileInfo(walPath).Length.Should().BeGreaterThan(
                SqliteWalHeader.Size,
                "the managed pager must have appended WAL frames for this characterization to be meaningful");
            File.Exists(sharedMemoryPath).Should().BeTrue();
            new FileInfo(sharedMemoryPath).Length.Should().Be(
                0,
                "the managed pager uses -shm purely as a byte-lock carrier and never writes a WAL-index");

            pager.CheckpointToMainStoreAndResetWal();

            new FileInfo(sharedMemoryPath).Length.Should().Be(
                0,
                "checkpointing publishes no WAL-index header, read marks, or backfill counter");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedWriterClaimsSqliteWalWriteLockByte()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var pager = CreatePhysicalPager(databasePath);

            using (HoldSharedMemoryRanges(workDirectory, databasePath, $"{WriteLockOffset}:1"))
            {
                var busy = Assert.Throws<SqlitePagerBusyException>(
                    () => pager.BeginTransaction(targetDatabaseSizeInPages: 2, TimeSpan.Zero));
                busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
            }

            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 1, TimeSpan.Zero);
            transaction.Rollback();
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedWritableOpenClaimsSqliteWalRecoveryLockByte()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            var walPath = databasePath + "-wal";
            using (var pager = CreatePhysicalPager(databasePath))
            {
                CommitPageTwo(pager, CreatePage(pager.PageSize, 0x42));
            }

            using (HoldSharedMemoryRanges(workDirectory, databasePath, $"{RecoveryLockOffset}:1"))
            {
                var busy = Assert.Throws<SqlitePagerBusyException>(() => SqlitePager.Open(
                    PhysicalFileSystem.Instance,
                    databasePath,
                    walPath,
                    busyTimeout: TimeSpan.Zero));
                busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
            }

            using var reopened = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                walPath,
                busyTimeout: TimeSpan.Zero);
            reopened.State.Should().Be(SqlitePagerState.Ready);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedReaderIsBusyWhenEverySqliteReadMarkLockByteIsHeld()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var pager = CreatePhysicalPager(databasePath);

            using (HoldSharedMemoryRanges(
                       workDirectory,
                       databasePath,
                       $"{FirstReadMarkLockOffset}:{ReadMarkLockCount}"))
            {
                var busy = Assert.Throws<SqlitePagerBusyException>(
                    () => pager.BeginReadTransaction(TimeSpan.Zero));
                busy!.Operation.Should().Be(SqlitePagerLockOperation.Reader);
            }

            using var reader = pager.BeginReadTransaction(TimeSpan.Zero);
            reader.ReadPage(1).Should().NotBeNull();
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedCheckpointDemandsTheEntireSqliteWalLockArea()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var pager = CreatePhysicalPager(databasePath);
            CommitPageTwo(pager, CreatePage(pager.PageSize, 0x43));

            var lastReadMarkOffset = FirstReadMarkLockOffset + ReadMarkLockCount - 1;
            using (HoldSharedMemoryRanges(workDirectory, databasePath, $"{lastReadMarkOffset}:1"))
            {
                var busy = Assert.Throws<SqlitePagerBusyException>(
                    () => pager.CheckpointToMainStore(TimeSpan.Zero));
                busy!.Operation.Should().Be(SqlitePagerLockOperation.Checkpoint);
            }

            pager.CheckpointToMainStore(TimeSpan.Zero).InstalledPageCount.Should().BeGreaterThan(0);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedRolesNeverClaimSqliteCheckpointLockByteAlone()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var pager = CreatePhysicalPager(databasePath);

            using var holder = HoldSharedMemoryRanges(
                workDirectory,
                databasePath,
                $"{CheckpointLockOffset}:1");
            CommitPageTwo(pager, CreatePage(pager.PageSize, 0x44));
            using (var reader = pager.BeginReadTransaction(TimeSpan.Zero))
            {
                reader.ReadPage(2).Should().NotBeNull();
            }

            var busy = Assert.Throws<SqlitePagerBusyException>(
                () => pager.CheckpointToMainStore(TimeSpan.Zero));
            busy!.Operation.Should().Be(SqlitePagerLockOperation.Checkpoint);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedRolesStayInsideSqliteReservedSharedMemoryLockArea()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var pager = CreatePhysicalPager(databasePath);

            var beforeLockArea = SharedMemoryLockAreaOffset - 1;
            var afterLockArea = SharedMemoryLockAreaOffset + SharedMemoryLockAreaLength;
            using var holder = HoldSharedMemoryRanges(
                workDirectory,
                databasePath,
                $"{beforeLockArea}:1,{afterLockArea}:1");
            CommitPageTwo(pager, CreatePage(pager.PageSize, 0x45));
            using (var reader = pager.BeginReadTransaction(TimeSpan.Zero))
            {
                reader.ReadPage(2).Should().NotBeNull();
            }

            pager.CheckpointToMainStore(TimeSpan.Zero).InstalledPageCount.Should().BeGreaterThan(0);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedReaderClaimsTheFirstFreeSqliteReadMarkLockByte()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore(
                "Probing which read-mark byte the managed reader claimed requires handle-scoped "
                + "byte-range locks inside this process, which only Windows provides.");
        }

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var pager = CreatePhysicalPager(databasePath);
            using var probe = OpenSharedMemoryLockCarrier(databasePath);

            using var holder = HoldSharedMemoryRanges(
                workDirectory,
                databasePath,
                $"{FirstReadMarkLockOffset}:1");
            using var reader = pager.BeginReadTransaction(TimeSpan.Zero);

            Assert.Throws<IOException>(() => LockRange(probe, FirstReadMarkLockOffset + 1, 1));
            LockRange(probe, FirstReadMarkLockOffset + 2, 1);
            UnlockRange(probe, FirstReadMarkLockOffset + 2, 1);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedReadOnlyOpenRefusesToCreateAMissingSharedMemoryLockCarrier()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            var walPath = databasePath + "-wal";
            var sharedMemoryPath = databasePath + "-shm";
            using (var pager = CreatePhysicalPager(databasePath))
            {
                CommitPageTwo(pager, CreatePage(pager.PageSize, 0x46));
            }

            File.Delete(sharedMemoryPath);

            var failure = Assert.Throws<InvalidOperationException>(() => SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                walPath,
                readOnly: true,
                busyTimeout: TimeSpan.Zero));
            failure!.Message.Should().Contain("WAL lock file is missing");
            File.Exists(sharedMemoryPath).Should().BeFalse(
                "a read-only managed open must never mutate storage to obtain its lock carrier");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Holds the requested <c>-shm</c> byte ranges on behalf of a parent test.
    /// This runs as its own process so the locks contend with the managed
    /// coordinator on every supported platform.
    /// </summary>
    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessSharedMemoryLockWorkerHoldsRequestedRanges()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_SHM_LOCK_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var ranges = Environment.GetEnvironmentVariable("TURSO_SHM_LOCK_WORKER_RANGES")
            ?? throw new InvalidOperationException("The shared-memory lock worker is missing its ranges.");
        var readyPath = Environment.GetEnvironmentVariable("TURSO_SHM_LOCK_WORKER_READY_PATH")
            ?? throw new InvalidOperationException("The shared-memory lock worker is missing its ready path.");
        var releasePath = Environment.GetEnvironmentVariable("TURSO_SHM_LOCK_WORKER_RELEASE_PATH")
            ?? throw new InvalidOperationException("The shared-memory lock worker is missing its release path.");

        var parsedRanges = ParseRanges(ranges);
        using var stream = OpenSharedMemoryLockCarrier(databasePath);
        var lockedCount = 0;
        try
        {
            foreach (var (offset, length) in parsedRanges)
            {
                LockRange(stream, offset, length);
                lockedCount++;
            }

            File.WriteAllText(readyPath, string.Empty);
            var stopwatch = Stopwatch.StartNew();
            while (!File.Exists(releasePath))
            {
                if (stopwatch.Elapsed >= TimeSpan.FromSeconds(60))
                    Assert.Fail("The shared-memory lock worker was not released within 60 seconds.");
                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }
        }
        finally
        {
            for (var index = lockedCount - 1; index >= 0; index--)
            {
                var (offset, length) = parsedRanges[index];
                UnlockRange(stream, offset, length);
            }
        }
    }

    private static (long Offset, long Length)[] ParseRanges(string ranges)
        => ranges
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(range =>
            {
                var parts = range.Split(':');
                if (parts.Length != 2)
                    throw new InvalidOperationException($"Malformed shared-memory lock range '{range}'.");
                return (long.Parse(parts[0]), long.Parse(parts[1]));
            })
            .ToArray();

    private static ExternalSharedMemoryLockHolder HoldSharedMemoryRanges(
        string workDirectory,
        string databasePath,
        string ranges)
        => new(workDirectory, databasePath, ranges);

    private sealed class ExternalSharedMemoryLockHolder : IDisposable
    {
        private readonly Process _worker;
        private readonly string _releasePath;
        private readonly StringBuilder _output = new();

        internal ExternalSharedMemoryLockHolder(string workDirectory, string databasePath, string ranges)
        {
            var token = Guid.NewGuid().ToString("N");
            var readyPath = Path.Combine(workDirectory, $"shm-lock-ready-{token}");
            _releasePath = Path.Combine(workDirectory, $"shm-lock-release-{token}");

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
            startInfo.ArgumentList.Add(Path.Combine(testDirectory.FullName, "Ahtola.Tests.dll"));
            startInfo.ArgumentList.Add(
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqliteWalInteroperabilityContractTests."
                + nameof(CrossProcessSharedMemoryLockWorkerHoldsRequestedRanges));
            startInfo.Environment["TURSO_SHM_LOCK_WORKER_DATABASE_PATH"] = databasePath;
            startInfo.Environment["TURSO_SHM_LOCK_WORKER_RANGES"] = ranges;
            startInfo.Environment["TURSO_SHM_LOCK_WORKER_READY_PATH"] = readyPath;
            startInfo.Environment["TURSO_SHM_LOCK_WORKER_RELEASE_PATH"] = _releasePath;

            _worker = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start the shared-memory lock worker.");

            // The worker must never block on a full pipe buffer before it writes
            // the ready file, so its output is drained continuously.
            _worker.OutputDataReceived += AppendOutput;
            _worker.ErrorDataReceived += AppendOutput;
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();

            var stopwatch = Stopwatch.StartNew();
            while (!File.Exists(readyPath))
            {
                if (_worker.HasExited)
                {
                    _worker.WaitForExit();
                    var output = DrainOutput();
                    _worker.Dispose();
                    Assert.Fail(
                        $"The shared-memory lock worker exited before locking its ranges:{Environment.NewLine}{output}");
                }
                if (stopwatch.Elapsed >= TimeSpan.FromSeconds(60))
                {
                    _worker.Kill(entireProcessTree: true);
                    var output = DrainOutput();
                    _worker.Dispose();
                    Assert.Fail(
                        "The shared-memory lock worker did not lock its ranges within 60 seconds:"
                        + Environment.NewLine
                        + output);
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }
        }

        private void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
                return;

            lock (_output)
            {
                _output.AppendLine(args.Data);
            }
        }

        private string DrainOutput()
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }

        public void Dispose()
        {
            try
            {
                File.WriteAllText(_releasePath, string.Empty);
                if (!_worker.WaitForExit(TimeSpan.FromSeconds(60)))
                {
                    _worker.Kill(entireProcessTree: true);
                    Assert.Fail(
                        "The shared-memory lock worker did not exit within 60 seconds:"
                        + Environment.NewLine
                        + DrainOutput());
                }

                // The timed overload does not wait for the redirected streams to
                // finish, so flush them before reading the collected output.
                _worker.WaitForExit();
                _worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{DrainOutput()}");
            }
            finally
            {
                _worker.Dispose();
            }
        }
    }

    private static void LockRange(FileStream stream, long offset, long length)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            stream.Lock(offset, length);
            return;
        }

        throw new PlatformNotSupportedException(
            "Managed SQLite WAL locking requires byte-range locks that are supported only on Windows and Linux.");
    }

    private static void UnlockRange(FileStream stream, long offset, long length)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            stream.Unlock(offset, length);
            return;
        }

        throw new PlatformNotSupportedException(
            "Managed SQLite WAL locking requires byte-range locks that are supported only on Windows and Linux.");
    }

    private static FileStream OpenSharedMemoryLockCarrier(string databasePath)
        => new(
            databasePath + "-shm",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);

    private static SqlitePager CreatePhysicalPager(string databasePath)
        => SqlitePager.Create(
            PhysicalFileSystem.Instance,
            databasePath,
            databasePath + "-wal",
            SqliteWalHeader.Create(
                SqlitePageSize.Default,
                salt1: 0x1122_3344,
                salt2: 0x5566_7788,
                checkpointSequence: 9));

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

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sqlite-wal-interoperability-contract",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
