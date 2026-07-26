# Managed WAL interoperability contract

Status: **Stage 0 — process-exclusive ownership.** Managed physical databases are
*not* concurrently interoperable with ordinary SQLite clients or with other
managed processes. This document is the normative description of what the managed
pager does today, why each guard exists, and the staged work required before any
form of SQLite-compatible multi-process WAL access can be claimed.

Nothing here describes behavior that is unimplemented, and nothing here authorizes
relaxing a guard ahead of the stage that replaces it.

Source of truth:

- `bindings/dotnet/src/Turso.Core/Storage/SqliteManagedFileOwnership.cs`
- `bindings/dotnet/src/Turso.Core/Storage/SqliteWalSharedMemoryLocks.cs`
- `bindings/dotnet/src/Turso.Core/Storage/SqlitePagerLockManager.cs`
- `bindings/dotnet/src/Turso.Core/Storage/SqlitePager.cs`

## 1. The current contract

### 1.1 Main-file client ownership

`SqliteManagedFileOwnership`, brokered per canonical path by
`SqliteManagedFileOwnershipRegistry`.

| Property | Current behavior |
| --- | --- |
| Locked range | `[0x4000_0000, 0x4000_0200)` — 512 bytes covering SQLite's `PENDING_BYTE`, `RESERVED_BYTE`, and the full 510-byte `SHARED` range |
| Lock kind | Exclusive/write. Windows `FileStream.Lock` (`LockFile`); Linux `fcntl(F_OFD_SETLK, F_WRLCK)` through `LinuxOpenFileDescriptionLocks` |
| Platform gate | Windows, or 64-bit Linux with a 32-byte `struct flock`. Every other platform throws `PlatformNotSupportedException` at open |
| Applies to | `PhysicalFileSystem` only, after unwrapping `TursoEncryptionFileSystem`. In-memory and custom file systems receive no cross-process boundary at all |
| Lifetime | Acquired by the first `SqlitePager.Create`/`Open` for a path, reference-counted across every managed pager in the process, released only when the last one is disposed |
| Registry key | `Path.GetFullPath(...)`, upper-invariant on Windows |
| Contention | Retried every 10 ms until the configured busy timeout, then `SqlitePagerClientOwnershipException` (an `InvalidOperationException` exposing `DatabasePath` and `Timeout`) |
| Create collision | `createNew: true` for a path already owned in this process throws `IOException` |
| Read-only opens | Windows may hold ownership through a `FileAccess.Read` handle. Linux OFD write locks require a writable descriptor, so read-only ownership fails where the file cannot be opened `ReadWrite` |

Consequences callers may rely on:

- While a managed process owns a database, an ordinary SQLite client cannot take
  `SHARED`, `RESERVED`, or `PENDING`, so it fails busy instead of reading a
  database whose committed state lives in a WAL that SQLite cannot locate.
- The reverse also holds: an ordinary SQLite reader holding `SHARED` blocks the
  managed open, which fails with `SqlitePagerClientOwnershipException`.
- Linux deliberately uses open-file-description locks. Ordinary POSIX record locks
  are released for the entire process when *any* descriptor for the file is
  closed, so a plain `F_SETLK` lock could be dropped silently by unrelated code.
- The ownership lock is a boundary against *other* processes. It is not a
  substitute for SQLite's locking inside the owning process, so a native SQLite
  client must not be co-hosted with the managed engine on the same database.

### 1.2 `-shm` is a byte-lock carrier, not a WAL-index

`SqliteWalSharedMemoryLocks` opens `<database>-shm` and uses it **only** to place
byte-range locks. The file is never mapped, never read, and never written, so it
stays zero bytes long for the entire life of a managed database.

Managed roles are placed inside SQLite's reserved lock area, which begins at shm
offset 120 (`SQLITE_SHM_BASE`, the `WalCkptInfo.aLock[8]` field):

| shm byte | SQLite role | Managed use |
| ---: | --- | --- |
| 120 | `WAL_WRITE_LOCK` | Writer takes exclusive `[120, 1)` |
| 121 | `WAL_CKPT_LOCK` | Never taken on its own |
| 122 | `WAL_RECOVER_LOCK` | Writable open and WAL tail recovery take exclusive `[122, 1)` |
| 123–127 | `WAL_READ_LOCK(0..4)` | Reader takes the first free byte |
| 120–127 | — | Checkpoint and `SqlitePager.Create` take exclusive `[120, 8)` |

Additional current behavior:

- One reader byte is held per coordinator (one per database path per process) and
  reference-counted across every managed reader in that process.
- The carrier handle is retained while any range is held and closed once the last
  range is released, because closing a descriptor would drop process-owned POSIX
  record locks.
- Reader carriers open with `FileMode.Open`. A missing `-shm` therefore fails with
  `InvalidOperationException` instead of being created, so a read-only open never
  mutates storage. Writer and checkpoint carriers use `FileMode.OpenOrCreate`.
- Byte-range locking is enabled on Windows and Linux only; anything else throws
  `PlatformNotSupportedException` rather than falling back to process-local locks.
- Contention is polled every 10 ms until the pager busy timeout expires and is
  then raised as `SqlitePagerBusyException` carrying the requested
  `SqlitePagerLockOperation`.

### 1.3 Why this is not SQLite-compatible

1. **No WAL-index exists.** SQLite locates WAL frames through the `-shm` mapping.
   Managed never publishes `mxFrame`, `nPage`, frame checksums, the page-number
   array, or the hash tables, so a concurrent SQLite client has no way to observe
   managed commits, and managed has no way to observe SQLite's.
2. **Read-mark locks are exclusive, not shared.** Windows `LockFile` is
   exclusive-only, and on Linux `FileStream.Lock` requests `F_RDLCK` only while the
   carrier handle happens to be read-only, so the lock mode is not stable. SQLite
   readers take *shared* locks on a read mark and expect many readers per mark;
   managed effectively supports at most five reader-holding coordinators.
3. **Read marks are never written.** `aReadMark[]` stays zero, so managed readers
   publish no snapshot for any checkpointer — managed or SQLite — to respect.
   Managed reader isolation comes entirely from a process-local page-overlay copy
   taken under the process-local lock manager.
4. **Checkpoint exclusion is coarser than SQLite's.** Managed checkpointing demands
   all eight lock bytes, so any externally held read-mark byte blocks it, while
   SQLite's checkpointer takes `WAL_CKPT_LOCK` plus the marks it needs and can run
   concurrently with a writer. Symmetrically, managed never takes byte 121 alone,
   so an external holder of SQLite's checkpoint byte does not exclude a managed
   writer.
5. **Backfill accounting is absent.** `nBackfill` and `nBackfillAttempted` are
   never maintained; installed-frame accounting lives only in
   `SqliteCheckpointResult` and process-local pager state.
6. **The primary arbiter is process-local.** `SqlitePagerLockManager` serializes
   readers, the single writer, and checkpoints inside the process; the shm bytes
   are a secondary boundary layered underneath it.

### 1.4 Busy semantics today

- Every role converts external contention into `SqlitePagerBusyException` whose
  `Operation` is the requested role and whose `Timeout` is the configured pager
  busy timeout. Nested busy failures are wrapped rather than replaced.
- Recovery-lock contention is reported as `SqlitePagerLockOperation.Writer`,
  because recovery is only attempted on behalf of a writable open or a writer.
- Retry is a flat 10 ms poll. There is no exponential backoff and no equivalent of
  `sqlite3_busy_handler`.
- Ownership contention is intentionally a *different* exception type
  (`SqlitePagerClientOwnershipException`) so callers can distinguish "another
  client owns this database" from "this role is momentarily busy".

### 1.5 Cache invalidation today

- `SqlitePagerLockManager.Generation` is a monotonic counter bumped by writer and
  checkpoint leases through `PublishStorageChange`. It is purely process-local.
- Because that counter cannot observe other processes, physical pagers
  (`UsesFileBackedWalLocks`) skip the generation fast path and re-validate on every
  `SynchronizeCommittedView`.
- Validation on synchronize covers a hot rollback journal, a changed page size, a
  changed read/write file-format version, a changed or missing WAL incarnation
  (`ValidateWalIncarnation` compares WAL salts), and — for non-file-backed lock
  managers — any uncommitted or invalid WAL tail.
- `ValidateWalHasNotChanged` re-verifies the WAL immediately before checkpoint
  installation.
- Any of these failures transitions the pager to `SqlitePagerState.Faulted`. The
  pager never silently re-reads a database that changed underneath it.

### 1.6 Recovery and handoff today

- **Writable open** holds the write byte and the recovery byte, recovers a hot
  rollback journal, scans the WAL, truncates it to the last committed frame, and
  requires the post-repair scan to match the authenticated pre-repair scan exactly.
- **Read-only open** takes only a reader byte, never repairs anything, refuses to
  create a missing `-shm`, and refuses to establish a snapshot that would require
  WAL repair.
- **Managed → SQLite handoff is one-way per session.** Commit, checkpoint, then
  dispose *every* managed pager and connection for that path; ownership is released
  on the last dispose. Only then may SQLite open the database.
- **SQLite → managed handoff.** SQLite may delete or replace `-wal` and `-shm`, so
  reopen writable with the managed provider first and let managed WAL or
  rollback-journal recovery complete before resuming normal use.

## 2. Required staged transition to SQLite-compatible multi-process WAL

Each stage is a prerequisite for the next. No stage may ship with the ownership
lock relaxed until Stage 6.

### Stage 1 — WAL-index format and shared mapping

Implement the `-shm` layout SQLite defines:

- Two `WalIndexHdr` copies, 48 bytes each, at offsets 0 and 48: `iVersion`
  (`3007000`), padding, `iChange`, `isInit`, `bigEndCksum`, `szPage` (64 KiB
  encoded as `1`), `mxFrame`, `nPage`, `aFrameCksum[2]`, `aSalt[2]`, `aCksum[2]`.
- `WalCkptInfo` at offset 96: `nBackfill`, `aReadMark[5]`, `aLock[8]` (bytes
  120–127), `nBackfillAttempted`, one reserved word. Header region total: 136
  bytes.
- 32 KiB wal-index pages: 4096 `u32` frame slots (4062 on the first page, which
  also carries the 136-byte header) followed by 8192 `u16` hash slots.

This requires a real shared-memory capability on `IFileSystem`
(`mmap`/`MapViewOfFile`), because byte-range locks alone cannot publish state.

*Exit criteria:* managed reads and validates an index written by ordinary SQLite,
including both header copies and their checksums, and its frame lookups agree with
its own independent WAL scan across a corpus of SQLite-produced databases.

### Stage 2 — read marks and the reader protocol

Implement SQLite's `walTryBeginRead`: use `WAL_READ_LOCK(0)` for a database-only
snapshot, otherwise select the largest `aReadMark[i]` not exceeding `mxFrame`, or
claim an unused mark under an exclusive lock and downgrade it to shared. Reader
snapshots must be pinned to `mxFrame` at read-lock time instead of copying a
process-local overlay.

This requires *shared* byte-range locks on both platforms. `FileStream.Lock`
cannot express a shared lock on Windows, so a `LockFileEx` interop layer — and
Linux OFD read locks — must be added first.

*Exit criteria:* many managed readers share one mark, and managed and SQLite
readers coexist on the same mark.

### Stage 3 — writer and checkpointer protocol

- The writer takes only `WAL_WRITE_LOCK`, verifies the index header is unchanged,
  appends frames, and then publishes `mxFrame`, `nPage`, and checksums by writing
  both header copies with the required barrier between them.
- The checkpointer takes `WAL_CKPT_LOCK`, derives `mxSafeFrame` from
  `aReadMark[]`, backfills, and maintains `nBackfill`/`nBackfillAttempted`. Only a
  checkpointer that obtains exclusive locks on every read mark may reset the WAL.
- Managed must stop demanding the whole `[120, 8)` range for checkpoints and for
  `SqlitePager.Create`.

*Exit criteria:* an ordinary SQLite writer and a managed reader (and the reverse)
interleave correctly under a differential stress harness, and `PRAGMA
wal_checkpoint` issued from either side agrees with the other.

### Stage 4 — busy semantics

Map `SQLITE_BUSY`, `SQLITE_BUSY_SNAPSHOT`, and `SQLITE_BUSY_RECOVERY` onto managed
exceptions; adopt SQLite's retry/backoff schedule instead of a flat 10 ms poll;
preserve `SqlitePagerBusyException.Operation` so existing callers keep working; and
add a distinct snapshot-invalidated result for readers whose mark was reset.

### Stage 5 — recovery, handoff, and shared cache invalidation

- Recovery runs under `WAL_RECOVER_LOCK` plus exclusive read marks, rebuilds the
  index from the WAL, and bumps `iChange`.
- Cache invalidation moves from the process-local
  `SqlitePagerLockManager.Generation` to the shared `WalIndexHdr` (`iChange`,
  `mxFrame`, salts). The current physical-pager guards — `ValidateMainFileFormat`,
  `ValidateWalIncarnation`, and the uncommitted-tail check — become ordinary
  snapshot comparisons instead of "dispose and reopen" errors.
- Handle `-shm` unlink by the last connection out, exclusive locking mode, and the
  heap-memory WAL-index fallback.

### Stage 6 — retire process-exclusive ownership

Only after Stages 1–5 land: replace the 512-byte main-file ownership lock with
SQLite's `PENDING`/`RESERVED`/`SHARED` protocol, including DELETE-mode rollback
journal locking, and delete `SqliteManagedFileOwnership`. Until then the ownership
lock must remain and must keep failing closed.

## 3. Invariants that hold in every stage

1. Never claim interoperability that is not implemented. Ownership and lock
   acquisition fail closed rather than proceeding optimistically.
2. Read-only opens never mutate storage, including companion files.
3. No silent downgrade to process-local locking on an unsupported platform.
4. A validation failure faults the pager instead of re-reading a database that
   changed underneath it.
5. Each stage ships with differential tests against ordinary SQLite before the next
   stage begins.

## 4. Characterization coverage

`bindings/dotnet/src/Turso.Tests/SqliteWalInteroperabilityContractTests.cs` pins
the Stage 0 boundary:

| Test | Contract clause |
| --- | --- |
| `ManagedWalActivityNeverMaterializesASqliteWalIndex` | §1.2 — `-shm` stays zero bytes across commits and checkpoints |
| `ManagedWriterClaimsSqliteWalWriteLockByte` | §1.2 — the writer occupies byte 120 |
| `ManagedWritableOpenClaimsSqliteWalRecoveryLockByte` | §1.2, §1.6 — a writable open occupies byte 122 |
| `ManagedReaderClaimsTheFirstFreeSqliteReadMarkLockByte` | §1.2 — readers walk bytes 123–127 (Windows only; see below) |
| `ManagedReaderIsBusyWhenEverySqliteReadMarkLockByteIsHeld` | §1.3, §1.4 — five reader slots, then busy |
| `ManagedCheckpointDemandsTheEntireSqliteWalLockArea` | §1.3 — checkpoint exclusion is coarser than SQLite's |
| `ManagedRolesNeverClaimSqliteCheckpointLockByteAlone` | §1.3 — byte 121 is unused by managed roles |
| `ManagedRolesStayInsideSqliteReservedSharedMemoryLockArea` | §1.2 — no locks outside bytes 120–127 |
| `ManagedReadOnlyOpenRefusesToCreateAMissingSharedMemoryLockCarrier` | §1.2, §3 — read-only opens never create `-shm` |

Related existing coverage:

- `SqlitePagerPortableLockCoordinatorTests` — cross-process ownership, ordinary
  SQLite peers, and recovery before handoff.
- `SqlitePagerLockingStorageTests` — lock-manager role interleaving and
  cross-process busy behavior.
- `SqlitePagerWalConcurrencyRecoverySliceTests` — ownership retry and recovery
  failure surfacing.
- `ManagedJournalPageMigrationTests` — WAL incarnation change detection.

Tests that need an external holder of a `-shm` byte range start a worker process
(`CrossProcessSharedMemoryLockWorkerHoldsRequestedRanges`) instead of opening a
second handle in the test process, because POSIX record locks are process-scoped
and would not contend with the managed coordinator on Linux. The single test that
must *probe* which read-mark byte the managed reader claimed still needs a
handle-scoped lock inside the process and therefore runs on Windows only.
