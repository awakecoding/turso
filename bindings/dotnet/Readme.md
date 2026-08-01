# Turso .NET

ADO.NET bindings for Turso local and remote databases.

The `Turso.Data.Sqlite` package includes both a SQLite-compatible `Turso.Data.Sqlite` facade and Turso-specific `System.Data.Common` types such as `TursoConnection`, `TursoCommand`, `TursoDataReader`, `TursoParameter`, `TursoTransaction`, and `TursoFactory`.

## Install

```bash
dotnet add package Turso.Data.Sqlite
```

Managed local and remote Hrana applications only need `Turso.Data.Sqlite`. Native
local and embedded replica modes require an optional matching-version companion.

All managed assemblies and dynamic companions target `net8.0`, `net9.0`, and
`net10.0`. .NET Framework hosts (including Windows PowerShell 5.1 / `net48`) are
unsupported: there are no `net48` assets and none are planned. File-backed
databases additionally require Windows or 64-bit Linux at runtime; 32-bit Linux
(`linux-x86`) is unsupported, as noted in the limitation list below.

| Package | Contract |
| --- | --- |
| `Turso.Data.Sqlite` | Managed-only primary package. Includes `TursoConnection` for managed local and remote Hrana access, plus the local-only `Turso.Data.Sqlite` compatibility facade. Contains no native runtime assets or `Turso.Raw` dependency. |
| `Turso.Data.Sqlite.Native` | Optional dynamic native local provider selected with `Local Provider=Native`; depends on `Turso.Raw`. |
| `Turso.Data.Sqlite.Sync` | Optional native embedded-replica provider for `TursoConnection`; enables explicit `Sync`/`SyncAsync`. |
| `Turso.Data.Sqlite.NativeAot.<rid>` | Optional RID-specific desktop static-link package for native local NativeAOT publishing. |
| `Turso.EntityFrameworkCore.Sqlite` | Local-only EF Core 9.x provider. |
| `Turso.Raw` | Runtime/interop dependency of `Turso.Data.Sqlite.Native` and binding dependency of the static NativeAOT packages. The primary and Sync packages do not reference it directly. |

The primary package uses the managed provider by default. No Rust toolchain or
native runtime asset is needed to restore, build, pack, or run managed local or
remote Hrana applications.

## Managed engine scope

The managed local engine is an independent C# SQL engine that reads and writes
SQLite's on-disk format. It is not a port of Turso's Rust core, and it is not a
drop-in replacement for `Microsoft.Data.Sqlite` or for SQLite itself. Choose it
when you need a fully managed, native-asset-free local database for small to
moderate workloads; choose `Local Provider=Native` when you need SQLite's own
engine characteristics.

What it shares with SQLite is the file format and the observable SQL semantics
it implements. What it does not share is the engine architecture:

| Area | Managed engine reality |
| --- | --- |
| Execution | A tree-walking evaluator with a partial bytecode compiler layered on top. The managed VDBE defines its own instruction set rather than SQLite's or Turso's; compiled predicates re-enter the evaluator for row-local expressions. Any statement stepped with a cancellation token, and many statement shapes listed below, run entirely in the evaluator. |
| Query planning | No cost model, join reordering, predicate pushdown, subquery flattening, covering-index detection, or `sqlite_stat*` statistics. Index selection scans the table's indexes in name order and takes the first usable match. `ANALYZE` is an explicit error rather than a no-op. |
| `EXPLAIN` | Emits managed instruction names, not SQLite opcodes, and errors for evaluator-owned statements instead of fabricating a program. `EXPLAIN QUERY PLAN` reports the managed execution boundary (`MANAGED COMPILED VDBE`, `MANAGED EVALUATOR FALLBACK`, or a real `SCAN`/`SEARCH ... USING INDEX` row) rather than SQLite optimizer internals. |
| Storage | Table rows are materialized in managed memory rather than paged incrementally from a B-tree, so a database must fit in the process heap. There is no page defragmentation, freelist reclamation on the bounded write path, interior-page split/merge balancing, `auto_vacuum`, or pointer-map support. Use `VACUUM` to compact. |
| Working set | Nothing spills to disk. Sorters, joins, `DISTINCT`, and CTE materialization are in-memory, so result-set size is bounded by available memory. |
| Result order | `GROUP BY` emits groups in first-encounter order rather than SQLite's sorted-by-key order. A grouped query without `ORDER BY` - including one whose window pass runs over those grouped rows, such as `row_number() OVER ()` - may therefore return a different order than SQLite. Add an `ORDER BY` when the order matters. |
| REAL to TEXT | Layout matches SQLite exactly - a whole-number real keeps a fractional digit (`0.0`, not `0`), the fixed-notation window is the same, and the exponential form is `1.0e+20`. The digits are the shortest that read back as the same double. SQLite derives its digits from `sqlite3FpDecode`, which is deliberately cheap rather than correctly rounded, so for about one double in six it emits a redundant seventeenth digit, and that digit is sometimes wrong: it prints `3.1079236656039855e-160` where the correctly rounded value is `3.1079236656039854e-160`. Managed output always round-trips and is never longer than SQLite's. |
| Async | Local managed async methods move blocking work to the thread pool rather than performing non-blocking I/O. Treat them as cancellation-aware wrappers, not as a scalability mechanism. |

### Write throughput

Managed writes are substantially slower than SQLite's, and the gap widens as a
table grows because each mutating statement works against a copy of the affected
catalog and each commit rewrites durable state. Measured on Windows against
`Microsoft.Data.Sqlite` 9.0.8 with two-column rows in a file-backed database
(release build, single machine, indicative only):

| Shape | 250 rows | 1,000 rows | 4,000 rows |
| --- | --- | --- | --- |
| Inserts inside one transaction | ~10x slower | ~29x slower | ~56x slower |

Autocommit inserts, where both engines are dominated by per-commit durability,
measured 2-3x slower at 100-400 rows. Batch writes into explicit transactions,
keep individual tables modest, and benchmark your own workload before adopting
the managed provider for write-heavy use.

### Transaction modes and locking

`BEGIN DEFERRED` (and bare `BEGIN`), `BEGIN IMMEDIATE` and `BEGIN EXCLUSIVE` all
change managed locking behavior, matching SQLite's timing:

- DEFERRED takes no lock at `BEGIN`, so a competing writer surfaces
  `SqliteException` with `SqliteErrorCode` 5 at the transaction's first write.
- IMMEDIATE takes the write lock at `BEGIN`, so the losing writer fails there,
  before doing any work. `SqliteConnection.BeginTransaction` uses this for
  non-deferred `Serializable`.
- EXCLUSIVE takes the write lock at `BEGIN` and additionally excludes other
  connections' reads, but only under a rollback journal. In WAL mode, which is
  the managed default for file databases, SQLite's EXCLUSIVE does not block
  readers and neither does this.

The default busy timeout is zero, matching SQLite's default `busy_timeout=0`.
Like `Microsoft.Data.Sqlite`, `CommandTimeout` maps onto the equivalent of
`sqlite3_busy_timeout`, so a contended `BEGIN IMMEDIATE` waits up to
`CommandTimeout` for the holder to release before reporting busy (and
`CommandTimeout=0` waits indefinitely). `PRAGMA busy_timeout` itself stays
unsupported. Locking is
process-local, which is sufficient because a managed physical database is already
owned exclusively by one process.

The same busy timeout also governs the snapshot-stale contract, the managed
equivalent of SQLite's `SQLITE_BUSY_SNAPSHOT`. When a sibling connection's
commit moved the durable catalog past a connection's snapshot, `BEGIN` waits
out any in-flight commit, re-reads the catalog, and proceeds; an autocommit
statement is re-executed against the reloaded catalog so the sibling's rows
survive. A transaction that already went stale before committing still fails
with `SQLITE_BUSY` ("database is locked") and must roll back — SQLite's
must-rollback semantics — and a zero busy timeout keeps every one of these
paths fail-fast instead of retrying.

### Hooks, authorizer, tracing, and the progress handler

`SqliteConnection` publishes `SetUpdateHook`, `SetCommitHook`, `SetRollbackHook`,
`SetAuthorizer`, `SetTraceHandler`, and `SetProgressHandler`. Their semantics were
derived from measurements of real SQLite and are re-checked against it at test
time by `ManagedHookSqliteDifferentialTests`, so the surprising parts match:
`INSERT OR REPLACE` does not report its implicit delete, `WITHOUT ROWID` tables
report nothing, `sqlite_sequence` maintenance is invisible, a vetoing commit hook
turns the commit into a rollback reported as `SQLITE_CONSTRAINT` (19), and the
rollback hook fires for an explicit `ROLLBACK` even when nothing changed.

Known divergences from SQLite:

| Surface | Managed behavior |
| --- | --- |
| Update hook, unfiltered `DELETE FROM t` | Reports one change per row. SQLite reports none because it replaces the statement with a truncate; suppressing the notifications to imitate that would leave a change-tracking consumer silently stale. |
| Commit hook | Not consulted for `VACUUM`, `ATTACH`/`DETACH`, `CREATE TABLE ... AS SELECT`, header pragma writes such as `PRAGMA user_version = n`, or incremental blob writes, because those bypass the statement commit path. |
| Reentrancy | Using the connection from inside a hook throws. SQLite leaves this undefined and in practice permits it; the managed engine cannot, because a reentrant read would observe the published catalog rather than the in-flight working copy and would therefore return stale rows. |
| Trace | Reports the prepared SQL text without expanding parameters, matching `sqlite3_trace_v2`'s `SQLITE_TRACE_STMT` rather than the legacy `sqlite3_trace`. The provider's own column-metadata probes are excluded, because native SQLite answers those through `sqlite3_column_decltype` without preparing a statement. |
| Progress handler | Counts managed row-execution steps rather than VDBE opcodes, so the interval is not comparable to SQLite's. The interrupt semantics are: a `true` return fails the statement with `SQLITE_INTERRUPT` (9). |
| Provider and cache scope | Callbacks require `Local Provider=Managed`, and are rejected on managed shared-memory databases for the same reason connection-local functions are: the catalog is shared across connections. |
| Double-quoted tokens in value context | Resolved strictly as column identifiers; an unresolved name throws `no such column` rather than falling back to a string literal. Stock SQLite with `SQLITE_DQS` (the default, including `e_sqlite3`) reinterprets the token as a string literal when no column matches. Use single-quoted literals for portable string comparisons. |

`sqlite3_profile` and `sqlite3_trace_v2`'s row and close events have no managed
equivalent and are not published, because the managed engine has no per-statement
wall-clock accounting that would make the reported numbers mean anything.

### Disconnected ADO.NET

`TursoDataAdapter` and `TursoCommandBuilder` support the classic `DataSet` model.
`Fill`, `FillSchema` and `Update` round trips persist inserts, updates and deletes,
and both `TursoConnection` and the `SqliteConnection` facade use the same adapter.
Round trips are covered by tests on managed local connections, and on native local
connections when the native companion is present.

`GetSchema` is also shared: both connection types answer from one implementation that
reads the catalog with ordinary SQL on the owning connection. A remote or replica
connection therefore describes the database it is attached to, and a statement the
target rejects surfaces that engine's own error instead of an empty table that would
read as "no objects exist". Remote behaviour is covered against a canned Hrana server;
it has not been validated against a live Turso Cloud instance.

`GetSchema` runs those catalog statements on the caller's behalf, so an installed
authorizer sees them and a trace handler reports them. That is deliberate: the
`Tables` collection returns each object's stored DDL, so a schema call that bypassed
a deny-by-default policy would disclose exactly what the policy was installed to hide.
`MetaDataCollections` and `ReservedWords` describe the provider rather than the
database and are answered without touching it. The reader's own column-metadata
probes remain exempt, as documented above, because they describe a result set the
caller has already been authorized to read.

`ReservedWords` reports SQLite's full keyword list, checked as a set against
`Microsoft.Data.Sqlite` at test time, because callers use it to decide which
identifiers need quoting and a partial list yields invalid SQL rather than a
cosmetic difference. `MetaDataCollections` uses the reference provider's column
shape; its row set is a superset, since that provider defines only these two
constant collections and leaves the four catalog collections undefined.

Deliberate limits:

- `TursoCommandBuilder` generates statements for a single-table `SELECT` only, and the
  select list must expose a key column. Joins, expressions, and multi-table selects
  need hand-written `InsertCommand`/`UpdateCommand`/`DeleteCommand`.
- `UpdateBatchSize` stays at 1; each changed row is a separate round trip.
- `MissingSchemaAction.AddWithKey` does not promote a rowid-alias `INTEGER PRIMARY KEY`
  to a `DataTable` primary key. This matches `Microsoft.Data.Sqlite`: SQLite publishes
  no uniqueness metadata for a rowid alias, so `System.Data` declines to infer the key.
  `TursoCommandBuilder` is unaffected because it reads `IsKey` from the schema table.
- `GetSchema` defines `MetaDataCollections`, `ReservedWords`, `Tables`, `Columns`,
  `Indexes` and `IndexColumns`. Any other collection name is an `ArgumentException`.
- An authorizer that returns `SqliteAuthorizerResult.Ignore` for an `UPDATE` makes
  `Update` report the row as saved even though the assignment was neutralized, so the
  `DataSet` accepts a change the database never took. `Deny` is reported correctly and
  leaves the row pending; only `Ignore` is silent. The engine reports the matched-row
  count, which is `1` for a neutralized update exactly as it is for one that rewrites
  the value it already held, and a plain `ExecuteNonQuery` reports the same `1`, so the
  adapter has nothing to distinguish. Use `Deny` if a rejected write must be visible.

### Not implemented

- Virtual-table modules and `CREATE VIRTUAL TABLE`, including FTS and R-Tree.
- Profile callbacks (`sqlite3_profile`), and the row/close events of
  `sqlite3_trace_v2`.
- Raw `sqlite3*` handle interop: `SqliteConnection.Handle` returns `null`.
  `ServerVersion` reports a managed placeholder, not a real SQLite version.
- Experimental MVCC and vector search.
- `BEGIN CONCURRENT` and `ANALYZE`. Each is rejected during parsing.
- Chained `ON CONFLICT` clauses on a single `INSERT`.
- Encryption beyond AES-128-GCM and AES-256-GCM. Databases written with Turso's
  AEGIS ciphers fail closed rather than being partially read.
- File-backed databases on macOS and on 32-bit Linux. Managed lock leases require
  Linux OFD locks (`F_OFD_SETLK`) or Windows `LockFileEx`, so opening any
  on-disk database elsewhere throws `PlatformNotSupportedException`. In-memory
  connections work on every platform. (macOS file-backed support is deferred,
  not ruled out — see Testing scope below.)

### Testing scope

The managed engine has its own regression suite and runs the repository's
`sqlite-sqltests` conformance corpus in full. Cases it does not yet satisfy are
listed in `src/Turso.Tests/Conformance/managed-sqltest-expected-failures.txt`,
so both coverage and known gaps are visible; it does not run the upstream SQLite
TCL suites. Fault injection is limited to the detached WAL coordinator
primitives, where process-isolated workers cover writer and checkpoint
interruption, torn and uncommitted tails, and carrier replacement.
The ordinary pager and commit path has no fsync-failure, disk-full, torn-write,
or power-loss injection, and there is no fuzzing or property-based testing, so
managed durability rests on targeted deterministic tests rather than randomized
validation. Managed CI runs the whole suite on Linux, Windows, and macOS against
`net8.0`, `net9.0`, and `net10.0`, and every leg fails if it executes or passes
fewer tests than a real run does, so a silently empty run cannot pass.

**macOS file-backed databases are not yet implemented.** The managed engine builds its
lock leases on Linux OFD locks (`F_OFD_SETLK`), which Darwin does not implement, so
`SqliteManagedFileOwnership` throws `PlatformNotSupportedException` for every physical
open. The 236 tests that open a physical file fail on macOS with that one message.
In-memory databases, the parser, the planner, and the conformance corpus are unaffected,
and the remaining tests (over 3,700) still pass there. The macOS legs run the full suite
and tolerate failures only when they carry that one documented message; any other macOS
failure fails the leg, so the platform is covered for regressions even though the gap is
open. Physical WAL coordination is likewise implemented for Windows and 64-bit Linux
only, so the process-isolated WAL harness must pass on those legs and is required to
stay discovered on macOS rather than being removed from the matrix. macOS file-backed
support is deferred until it can be implemented and validated on macOS hardware, since
the available lock primitive there has different (process-scoped) semantics that need
hands-on verification.

**Opening a WAL database races against other processes on Linux.**
`SqliteWalWriterCheckpointCoordinator.Open` rebuilds the WAL index before it
hands back a coordinator, and that rebuild takes the checkpoint, writer, and
recovery leases with `TimeSpan.Zero`, so it never retries. If another process
holds any of those leases at that instant the open fails outright with
`SqliteWalByteRangeLockBusyException`, rather than waiting the way SQLite's own
recovery path does. Five process-isolation tests hit this on the Linux CI
runners and none on Windows, which reflects scheduling luck rather than a
Windows-only code path, so the same race is latent on both. The Linux legs are
left failing instead of being papered over, because the fix is a deliberate
decision about how long `Open` may block and belongs with the WAL owner.

## Dynamic native compatibility

Applications that intentionally select `Local Provider=Native` can reference the matching-version `Turso.Data.Sqlite.Native` companion package:

```xml
<ItemGroup>
  <PackageReference Include="Turso.Data.Sqlite" Version="x.y.z" />
  <PackageReference Include="Turso.Data.Sqlite.Native" Version="x.y.z" />
</ItemGroup>
```

`Turso.Data.Sqlite.Native` activates the native provider and resolves its
`Turso.Raw` runtime companion. Desktop package consumers are restored and
executed in release gates on Windows, Linux, and macOS. Android
(`android-arm64`, `android-arm`, `android-x64`, and `android-x86`) and iOS
assets are architecture- and package-target validated, but the release gates do
not claim device or emulator execution. These optional companions are not
restored or packed by the managed release path. Remote Turso/libSQL connections
use the managed HTTP client and do not require a native package.

Calls on one native connection are serialized because its connection and
statement handles require exclusive access; use separate connections for
parallel execution. Managed callbacks cannot reenter the same native
connection and fail explicitly instead of deadlocking. Cancellation tokens,
`DbCommand.Cancel()`, reader disposal, and connection closure interrupt an
active native statement and wait for its handle to become idle before release.
`CommandTimeout` controls native busy-lock waits, not total query duration.

## Embedded replicas

Embedded replicas require the matching-version `Turso.Data.Sqlite.Sync` companion package:

```xml
<ItemGroup>
  <PackageReference Include="Turso.Data.Sqlite" Version="x.y.z" />
  <PackageReference Include="Turso.Data.Sqlite.Sync" Version="x.y.z" />
</ItemGroup>
```

Specify a remote `Data Source` and a local `Replica Path`. The companion bootstraps
the replica on first open, and `Sync()` or `SyncAsync(CancellationToken)` explicitly
pushes local changes then pulls and applies remote changes. `Sync Interval` is retained
for connection-string compatibility, but zero is the only supported value: the provider
never starts background synchronization. Applications that need a cadence must own the
scheduler, keep at most one outstanding sync per connection, await every call, observe
every failure, and choose their own retry and backoff policy. Closing or disposing a
connection cancels an in-flight explicit sync and waits for it to quiesce; reopening starts
a new explicit lifecycle. The companion resolves its native runtime assets on
Windows, Linux, macOS, Android (`android-arm64`, `android-arm`, `android-x64`, and
`android-x86`), and iOS as an XCFramework with an arm64 device slice and a
universal arm64+x64 simulator slice.

## Native platform matrix

| Distribution | Target frameworks | Runtime identifiers / slices | Release validation |
| --- | --- | --- | --- |
| Managed `Turso.Data.Sqlite` | `net8.0`, `net9.0`, `net10.0` | Runtime-independent | Pack, restore, run, publish, and NativeAOT managed-engine smoke without Rust |
| Dynamic native and Sync companions | `net8.0`, `net9.0`, `net10.0` | `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` | Package restore and ABI/runtime smoke on every TFM/RID combination |
| Dynamic mobile companions | Mobile workload TFMs compatible with the `net8.0`, `net9.0`, or `net10.0` package assets | Android arm64, arm, x64, and x86; iOS arm64 device and arm64+x64 simulator XCFramework slices | Binary architecture, direct system dependencies, XCFramework structure, and package target selection; no device/emulator runtime claim |
| Static native NativeAOT companions | Generic `net8.0`, `net9.0`, `net10.0` only | `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` | Restore, static publish, final executable architecture/dependencies, and runtime smoke on every TFM/RID combination |

The `Turso.Data.Sqlite.NativeAot.*` packages intentionally reject mobile and
OS-qualified TFMs. Android and iOS use the dynamic mobile package assets and
their workload-specific app build; they are not covered by the desktop static
NativeAOT package gates.

Advanced Sync configuration uses managed-only option types from `Turso.Data`; the
native companion is loaded only when the connection opens:

```C#
var replicaOptions = new TursoReplicaOptions(
    "replica.db",
    new Uri("https://example.turso.io"),
    Environment.GetEnvironmentVariable("TURSO_AUTH_TOKEN"))
{
    LongPollTimeout = TimeSpan.FromSeconds(15),
    PartialBootstrap = TursoPartialBootstrapOptions.Prefix(
        length: 64 * 1024,
        segmentSize: 256 * 1024,
        prefetch: true),
    RemoteEncryption = new TursoRemoteEncryptionOptions(
        Environment.GetEnvironmentVariable("TURSO_ENCRYPTION_KEY")!,
        TursoRemoteEncryptionCipher.Aes256Gcm),
    PushOperationsThreshold = 1000,
    PullBytesThreshold = 1024 * 1024,
    HttpPolicy = new TursoSyncHttpPolicy(requestTimeout: TimeSpan.FromMinutes(2)),
};

using var replica = TursoConnection.CreateReplica(replicaOptions);
await replica.OpenAsync(CancellationToken.None);
var progress = new Progress<TursoSyncProgress>(value =>
    Console.WriteLine(value.Stage));
var result = await replica.SyncAsync(
    new TursoSyncOptions(progress),
    CancellationToken.None);
Console.WriteLine($"{result.Outcome}: revision {result.Statistics.Revision}");
```

`LongPollTimeout` is sent to the server in millisecond precision. Prefix and query
partial-bootstrap strategies are mutually exclusive by construction; segment size
and prefetch apply only when a strategy is configured. Remote encryption requires
the base64 key and server cipher together so the provider can set the native
reserved-byte requirement. Push thresholds never split a transaction. Pull
thresholds chunk full or prefix bootstrap downloads, but are rejected with query
bootstrap because the server selects that page set. A custom `HttpMessageHandler`
is application-owned unless `disposeMessageHandler: true` transfers ownership to
the connection. Owned handlers survive `Close`/reopen cycles and are disposed with
the connection; an ownership-transferring HTTP policy cannot create a second
connection. HTTP timeouts cover both response headers and body reads and are separate
from long-poll duration.

The options-bearing synchronization overload returns `TursoSyncResult`, including
whether remote changes were applied and a native statistics snapshot. The
parameterless `Sync` and cancellation-token-only `SyncAsync` methods retain their
existing return types for binary compatibility. Progress reports the
`Pushing`, `Pulling`, optional `Applying`, and `Completed` phases. Progress callbacks
must not reenter or close the same replica; reentry fails explicitly. The same rule
applies to custom HTTP handlers and response bodies while application code is
executing under the serialized replica operation. Cancellation stops in-flight HTTP
or file I/O and does not report `Completed`.

Current boundaries are intentional: nonzero `Sync Interval` values fail before native
or network access; no background work is started and explicit sync failures are never
swallowed;
local at-rest encryption options cannot be used for replicas (remote encryption is
configured separately); logical MVCC pull is not enabled by the .NET provider; and
partial bootstrap requires initial bootstrap to be enabled. These settings fail
before native or network access rather than being silently ignored.

## NativeAOT static linking

NativeAOT apps can opt into statically linking the Turso native library so publish output does not include a sidecar `turso_sdk_kit` DLL, `.so`, or `.dylib`. Reference the RID-specific static package alongside `Turso.Data.Sqlite`:

```xml
<ItemGroup>
  <PackageReference Include="Turso.Data.Sqlite" Version="x.y.z" />
  <PackageReference Include="Turso.Data.Sqlite.NativeAot.win-x64" Version="x.y.z" PrivateAssets="all" />
</ItemGroup>
```

Then enable static linking:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <SelfContained>true</SelfContained>
  <TursoUseStaticNativeLibrary>true</TursoUseStaticNativeLibrary>
</PropertyGroup>
```

Publish with a supported runtime identifier, for example:

```bash
dotnet publish -c Release -r win-x64
```

Static native packages are published for generic `net8.0`, `net9.0`, and
`net10.0` applications on `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
`osx-x64`, and `osx-arm64`. Mobile and OS-qualified TFMs are explicitly
excluded from this package contract. See `samples/NativeAot` for a complete
executable sample.

The RID-specific package carries the matching `Turso.Data.Native` provider assembly required by `Local Provider=Native` and resolves `Turso.Raw` for its bindings; do not add the dynamic companion separately. The static build target removes the dynamic runtime sidecar before publishing.

Release gates inspect every native binary's architecture and direct native
dependencies before packaging. Windows DLLs published to NuGet must have valid
timestamped Authenticode signatures; unsigned Windows artifacts are accepted
only for pull requests and dry runs. Apple libraries may be unsigned or
ad-hoc-signed in the NuGet XCFramework because the consuming macOS/iOS app is
responsible for final bundle code signing; any signature already present must
verify successfully. Linux and Android packages have no code-signing
expectation.
Static `.lib` and `.a` archives are not code-signing units; applications that
consume a NativeAOT static package must sign the final executable or app bundle
according to their distribution channel.

## Maintainer packaging

`make restore`, `make build`, `make test`, and `make pack` use the isolated managed-only release path. They neither build Rust nor consume `rs_compiled` native assets. `Turso.slnx` likewise contains only the managed package projects; compatibility-package tests and benchmarks stay on their explicit native paths. `make test` validates the packed provider and EF Core facade by restoring, building, running, and publishing the source-free `ManagedPackageConsumer` sample. The consumer rejects restored native companion packages, and the validation rejects native companion assets in its publish output. `make validate-managed-project-closure` rejects native package, asset, P/Invoke, Rust-tool, and shared-solution references from the managed package and NativeAOT sample configurations. `scripts/Validate-ManagedPackageClosure.ps1` also opens every managed `.nupkg`, rejects native entries and native/PInvoke/Rust build configuration, and checks the restored closure and publish output; this cross-platform PowerShell gate is used by both package and NativeAOT validation. `make validate-managed-nativeaot` restores that same packed provider before publishing its smoke executable, so NativeAOT validates the shipping package rather than a project reference. The managed CI gates replace `cargo` and `rustc` with failing shims while this path runs. Use the explicitly opt-in `make test-native` for the full source test suite, including dynamic native-companion coverage. `make validate-managed-package` runs the same packaged-provider validation directly.

Native distribution is intentionally explicit: `make pack-native` creates the dynamic native companion packages after their runtime assets have been built. `Turso.Raw` carries its managed `Turso.Core` and `Turso.Data` closure for `net8.0`, `net9.0`, and `net10.0` alongside the runtime assets. Release validation restores only the packed artifacts and executes the dynamic Raw, Native, and Sync companions on `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. That smoke verifies the versioned native ABI, every imported symbol, enum widths, managed/native structure sizes and offsets, UTF-8 values and errors, and owned handle cleanup before companion publication. `make pack-nativeaot-static` creates the RID-specific static-linking companions. `make pack-release` deliberately combines those optional companion steps with the managed packages for a full distribution cut; it is not the primary managed release path.

### Managed SELECT bytecode

The managed engine lowers generic source-less and single-table SELECT projections to VDBE bytecode when they contain literals or safely foldable deterministic arithmetic, late-bound parameters, declared columns or rowid, nested `+`, `-`, `*`, `/`, and `%` arithmetic, and the built-in `abs`, `coalesce`, `hex`, `ifnull`, `instr`, `length`, `lower`, `typeof`, and `upper` scalar functions. Functions execute at their normal row position rather than during compilation, so volatile expressions are never repeated from a folded value and erroring projections do not run on empty input. Arithmetic bytecode applies the evaluator's SQLite numeric affinity first, including text/blob coercion, NULL propagation, modulo conversion, division-by-zero results, and error behavior. `EXPLAIN` exposes `LoadParameter`, `NumericAffinity`, `Arithmetic`, and `Function` instructions without baking parameter values into the plan.

The tree-walking evaluator remains the explicit fallback for expression families not yet represented by this lowering, including comparison/logical/concatenation expressions, `CASE` and `CAST`, volatile, collation-sensitive, or context-dependent functions, shadowing user-defined functions, complex scan predicates whose streaming error order would differ, computed `DISTINCT` or `ORDER BY` projections, parameterized compound terms, computed or otherwise error-capable `INTERSECT`/`EXCEPT` terms, and compounds whose custom collation callbacks cannot be invoked at evaluator-equivalent points.

Managed common table expressions accept SQLite's `AS MATERIALIZED` and `AS NOT MATERIALIZED` hints in SELECT, CTAS, and CTE-scoped DML. Unspecified and `MATERIALIZED` CTEs retain the one-shot materialization boundary. `NOT MATERIALIZED` is advisory: only one nonrecursive CTE consumed by a metadata-only `SELECT *` pass-through may elide the outer scan, so joins, compounds, windows, and VALUES inherit their existing route without repeating evaluation. Multiple references, nested multi-CTE scopes, DML, cancellation-capable plans, and every outer shape whose callback or error order is not proven equivalent remain materialized and evaluator-owned. Eligible linear recursive CTEs keep their existing guarded worktable route regardless of the hint.

Managed join SELECTs have two compiled paths. Direct two-table `INNER`/`CROSS`/`LEFT` shapes retain the nested-loop cursor program. A materializing `OpenJoinCursor` path handles safe N-way joins plus parser-supported `RIGHT`/`FULL`, `USING`/`NATURAL` coalescing, qualified rowids, null extension, computed projections, direct filters, built-in-collation ordering, `DISTINCT`, and bounds; it also supports built-in scalar and direct-key grouped aggregates. The join cursor completes recursive `ON` phases and post-join filtering before later projection/sort/distinct phases, and `ProjectRegisters` publishes a projected row only after every expression succeeds. Declared comparison affinity and collation are shared with the evaluator. Computed or callback-bearing `ON`/`WHERE`, callback-bearing `ORDER BY`/custom distinct collations, aggregate combinations whose result/error order is not represented, non-base sources, subqueries/windows, and every cancellation-capable execution remain explicit evaluator fallback. `EXPLAIN` reports `OpenJoinCursor`, `ProjectRegisters`, sorter/aggregate instructions, and `DistinctFilter` only for genuinely compiled shapes; EQP reports the same compiled/fallback boundary.

Managed SELECT, compound SELECT, window, and limited DML ordering all preserve explicit `NULLS FIRST` and `NULLS LAST`; omitting the clause uses SQLite's direction-dependent default.

### Managed DML bytecode

Managed `INSERT`, `UPDATE`, and `DELETE` reuse the same generic expression lowering for `RETURNING`: literals, late-bound parameters, affected-row columns or rowid, qualified stars/columns, nested numeric arithmetic with SQLite affinity, value-only `COLLATE`, and the allow-listed built-ins above compile. `UPDATE` and `DELETE` predicates may use the evaluator's row-local scalar expression subset, including nested arithmetic, logical/comparison operators, and scalar functions, because the VDBE filter invokes that evaluator at the original per-row position.

The compiled program first scans predicates and buffers all mutations, then evaluates buffered `RETURNING` rows in source and projection order, and commits only after projection succeeds. This retains predicate/assignment user-callback timing, keeps projection errors statement-atomic, and remains resumable across returned rows. Subqueries, aggregates/windows, `CASE`, `CAST`, concatenation/comparison projections, volatile or context-dependent functions, and shadowed user functions remain evaluator-owned. DML with a cancellation-capable token, foreign-key enforcement, open incremental blobs, conflict algorithms, source `INSERT`, CTE scope, or schema tables also falls back.

The managed SQL contract enables SQLite's optional single-table `UPDATE`/`DELETE` `ORDER BY ... LIMIT` grammar independently of the bundled native SQLite compile options. `LIMIT ... OFFSET ...` and `LIMIT offset, count` accept bound parameters, negative limits are unbounded, and negative offsets clamp to zero. `RETURNING`, when present, precedes `ORDER BY`; ordering chooses the affected subset but does not reorder mutation or `RETURNING` output. Limited DML stays evaluator-owned so selection expressions run before source-ordered buffered mutation and statement-atomic projection; `EXPLAIN QUERY PLAN` reports `MANAGED EVALUATOR FALLBACK` for that route. `UPDATE OR <algorithm>`, `UPDATE ... FROM`, and `UPDATE`/`DELETE` target aliases are supported but also evaluator-owned; combining `LIMIT` with `UPDATE ... FROM`, row-value assignments, `INDEXED BY`/`NOT INDEXED`, `ORDER BY` without `LIMIT`, and limited DML inside trigger bodies are rejected during parsing.

## Getting started

```C#
using Turso;

using var connection = new TursoConnection("Data Source=:memory:");
connection.Open();

connection.ExecuteNonQuery("CREATE TABLE t(a, b)");
var rowsAffected = connection.ExecuteNonQuery("INSERT INTO t(a, b) VALUES (1, 2), (3, 4)");
Console.WriteLine($"RowsAffected: {rowsAffected}");

using var command = connection.CreateCommand();
command.CommandText = "SELECT * FROM t";
using var reader = command.ExecuteReader();
while (reader.Read())
{
    var a = reader.GetInt32(0);
    var b = reader.GetInt32(1);
    Console.WriteLine($"Value1: {a}, Value2: {b}");
}
```

## ADO.NET usage

Code written against `DbConnection` can use `TursoConnection` directly:

```C#
using System.Data.Common;
using Turso;

await using DbConnection connection = new TursoConnection("Data Source=app.db");
connection.Open();

await using var command = connection.CreateCommand();
command.CommandText = "SELECT $value";
var parameter = command.CreateParameter();
parameter.ParameterName = "$value";
parameter.Value = 42;
command.Parameters.Add(parameter);

var value = command.ExecuteScalar();
```

Remote Turso/libSQL databases can use the same `TursoConnection` surface with a remote URL and auth token:

```C#
await using var connection = new TursoConnection(
    "Data Source=libsql://example-org.turso.io;Auth Token=eyJ...");
await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = "SELECT name FROM customers WHERE id = $id";
command.Parameters.Add(new TursoParameter("$id", 42));

var name = await command.ExecuteScalarAsync();
```

Remote mode uses the Hrana HTTP `/v2/pipeline` protocol. `libsql://` URLs default to HTTPS; `Tls=False` maps them to HTTP for local development. `ws://` and `wss://` URLs are accepted and mapped to the equivalent HTTP pipeline endpoint. `Auth Token` requires HTTPS unless the host is `localhost` or loopback.

Local and remote connections support ADO.NET `DbBatch`:

```C#
await using var batch = connection.CreateBatch();

var insert = batch.CreateBatchCommand();
insert.CommandText = "INSERT INTO customers(name) VALUES ($name)";
var name = insert.CreateParameter();
name.ParameterName = "$name";
name.Value = "Alice";
insert.Parameters.Add(name);
batch.BatchCommands.Add(insert);

var select = batch.CreateBatchCommand();
select.CommandText = "SELECT COUNT(*) FROM customers";
batch.BatchCommands.Add(select);

await using var reader = await batch.ExecuteReaderAsync();
```

Local batches execute each batch command in order on one connection and expose command boundaries through `DbDataReader.NextResult()`. Each command has its own parameters and affected-row count. Local batches do not create an implicit transaction: completed commands remain committed if a later command fails unless the batch is associated with an explicit transaction. Transaction association is refreshed between commands, so commands after an explicit `COMMIT` or full `ROLLBACK` run outside the completed transaction. Closing or disposing a reader drains the remaining commands; `Cancel()` or cancellation stops before the next command. Remote batches preserve the existing single Hrana batch request.

## Facade capability matrix

`TursoConnection.Capabilities` and `SqliteConnection.Capabilities` expose the same
executable contract used by provider feature gates. `CanCreateBatch` is sourced from
that contract, so generic ADO.NET callers and the provider cannot drift.

| Capability | `TursoConnection` managed local | `TursoConnection` native local | `TursoConnection` remote Hrana | `TursoConnection` embedded replica | `SqliteConnection` managed local | `SqliteConnection` native local |
| --- | --- | --- | --- | --- | --- | --- |
| `DbBatch` / `CanCreateBatch` | Yes, sequential | Yes, sequential | Yes, one Hrana batch | No | Yes, sequential | Yes, sequential |
| Async open, command, reader, transaction | Yes, worker-backed local I/O | Yes, worker-backed local I/O | Yes, HTTP I/O | Yes, replica/native I/O | Yes, worker-backed local I/O | Yes, worker-backed local I/O |
| Transactions | Yes | Yes | Yes | Yes | Yes | Yes |
| Savepoints | Yes | Yes | Yes | Yes | Yes | Yes |
| `BackupDatabase` | No facade API | No facade API | No | No | Yes, same-provider connections | Yes, same-provider connections |
| `SqliteBlob` fixed-length incremental I/O | No facade API | No facade API | No | No | Yes, managed handle | Yes, SQL-backed compatibility |
| Scalar UDFs / aggregates / collations | No facade API | No facade API | No | No | Yes | Yes |
| Loadable extensions | No facade API | No facade API | No | No | No | Yes, disabled by default |
| `ATTACH` / `DETACH` | Yes, with managed limits | Yes | No | No | Yes, with managed limits | Yes |
| Managed connection pooling | Eligible unencrypted files when `Pooling=True`; named shared memory requires `Pooling=False` | No | No | No | Eligible unencrypted files; named shared memory accepts the default keyword but is not pooled | No |
| Explicit `Sync` | No | No | No | Yes, with Sync companion | No | No |
| Encryption | AES-128/256-GCM managed format | Native SDK cipher set | Local encryption options rejected | Remote encryption options; local at-rest options rejected | AES-128/256-GCM managed format | `Encryption Cipher`/`Encryption Key` rejected |

`Turso.Data.Sqlite` is a local-only migration facade; remote URLs fail before they
can be interpreted as file paths. Use `TursoConnection` for Hrana and embedded
replicas. `TursoConnection` rejects `Pooling=True` before provider or network
access unless the target is an eligible unencrypted managed file; named shared
memory requires `Pooling=False`. The SQLite facade accepts its default
`Pooling=True` keyword for named shared-memory and native compatibility without
placing either kind of handle in the managed physical connection pool. Memory,
shared-memory, encrypted, callback-bearing, native, remote, and replica connections
are never pooled.

Remote Hrana and embedded replicas reject `ATTACH` and `DETACH` before network or
native execution; syncing or routing an attached database is not implied. Native
SQLite compatibility remains explicit: the SQLite facade keeps its native UDF,
aggregate, collation, extension, backup, blob, and attachment behavior, while
`Turso.Raw` and native handles are unchanged and no fake SQLite handle is exposed.

Provider factories are available through `TursoFactory.Instance`:

```C#
DbProviderFactory factory = TursoFactory.Instance;
using var connection = factory.CreateConnection();
connection!.ConnectionString = "Data Source=:memory:";
connection.Open();
```

## Migrating from Microsoft.Data.Sqlite

For common embedded SQLite usage, `Turso.Data.Sqlite` exposes a SQLite-compatible facade over the Turso engine:

```diff
- using Microsoft.Data.Sqlite;
+ using Turso.Data.Sqlite;

- using var connection = new SqliteConnection("Data Source=app.db");
+ using var connection = new SqliteConnection("Data Source=app.db");
```

The builders share keyword names, but execution support depends on the facade and
mode. Supported common connection string keywords include:

| Keyword | Notes |
| --- | --- |
| `Data Source` | Local database path or `:memory:`; remote URL for `TursoConnection`. Aliases include `DataSource` and `Filename`. |
| `Mode` | Local only. Managed local honors `Memory`, `ReadOnly`, `ReadWrite`, and `ReadWriteCreate` with explicit file-existence checks. |
| `Foreign Read Only` | Local managed only. Opens a database owned by another engine (for example `winget`'s `index.db`) without claiming ownership or requiring `-shm`; see [Managed foreign read-only opens](#managed-foreign-read-only-opens). |
| `Cache` | Local only. Managed `Cache=Shared` is supported with a named `Data Source` and `Mode=Memory`; file databases accept it as an ordinary private file connection (SQLite shared-cache semantics are not emulated); see below. |
| `Foreign Keys` | Applied by local `SqliteConnection` through `PRAGMA foreign_keys`; the managed engine supports composite keys, referential actions, and deferred constraints. Direct managed `TursoConnection` rejects the keyword. |
| `Local Provider` | `Managed` is the default for local databases. Set `Native` when `Turso.Data.Sqlite.Native` or a RID-specific `Turso.Data.Sqlite.NativeAot.*` companion is referenced. |
| `Recursive Triggers` | Tracked by local `SqliteConnection`; direct managed `TursoConnection` rejects the keyword. |
| `Default Timeout` | Default command timeout. Aliases include `Command Timeout`; for managed local databases it controls busy waits, not total query duration. |
| `Pooling` | Defaults to `True` on the SQLite-compatible facade. Managed physical pooling applies only to ordinary unencrypted file-backed databases. Named shared memory accepts the default keyword only through `SqliteConnection` and is not pooled; `TursoConnection` requires `Pooling=False`. |
| `Vfs` | Native `SqliteConnection` only. Managed local rejects native SQLite VFS names. |
| `Encryption Cipher` | Local only. Managed local supports AES-128-GCM and AES-256-GCM; native `TursoConnection` uses the SDK cipher set; native `SqliteConnection` rejects it. |
| `Encryption Key` | Hex-encoded local key used with `Encryption Cipher`; follows the same facade/provider boundaries. |
| `Auth Token` | Bearer token for remote Turso/libSQL URLs. Aliases include `AuthToken` and `Authentication Token`. |
| `Replica Path` | Embedded replica `TursoConnection` only. Requires `Turso.Data.Sqlite.Sync` and a remote Turso URL. |
| `Read Your Writes` | Remote `TursoConnection` only. Keeps the Hrana session baton across commands; `False` uses stateless requests. |
| `Sync Interval` | Retained for connection-string compatibility. Only `0` is accepted; call `TursoConnection.Sync()` or `SyncAsync(CancellationToken)` explicitly and await every operation. |
| `Tls` | Remote `TursoConnection` only. Optional `libsql://` development override; conflicts with explicit HTTP(S) schemes fail early. |

### Managed foreign read-only opens

`Foreign Read Only=True` opts the managed engine into reading a database it does
not own — typically a file created and still owned by an ordinary SQLite client,
such as `winget`'s `index.db`:

```csharp
using var connection = new SqliteConnection(
    $"Data Source={wingetIndexDb};Mode=ReadOnly;Foreign Read Only=True;Pooling=False");
connection.Open();
```

- The open never acquires the managed ownership lock, never requires or creates
  `-shm`, and never writes to the database or its companion files. A live SQLite
  owner does not block the foreign open, and the foreign open does not block the
  owner.
- WAL and rollback-journal databases both open cleanly, with or without live
  companion files. A hot rollback journal that cannot be read fails closed.
- Commits the owner makes between statements are picked up automatically: every
  autocommit statement re-checks the database and WAL file stamps and reloads
  when they changed. An explicit `BEGIN` transaction pins its snapshot until the
  transaction ends, matching SQLite read-transaction semantics.
- Constraints: `Local Provider=Managed` (the default), `Mode=ReadOnly`, a
  physical file data source, and `Pooling=False` are required; shared cache,
  encryption, and custom file systems are rejected.

See
[docs/managed-wal-interoperability-contract.md](docs/managed-wal-interoperability-contract.md#19-foreign-read-only-opens)
for the normative contract.

### Managed foreign-key semantics

The managed local engine supports composite child and parent keys, explicit or omitted
parent primary-key columns, UNIQUE parent keys, parent affinity and collation, generated
columns, and `WITHOUT ROWID` tables within the storage shapes supported by the managed
pager. `ON DELETE` and `ON UPDATE` implement `CASCADE`, `SET NULL`, `SET DEFAULT`,
`RESTRICT`, and `NO ACTION`, including bounded self-referential and multi-table cascades.
Foreign-key actions run after the parent mutation and before its AFTER row triggers. Child
row triggers caused by `CASCADE`, `SET NULL`, or `SET DEFAULT` finish before the parent
AFTER trigger. The whole statement, including actions and trigger effects, rolls back on
ABORT-class failures.

`DEFERRABLE INITIALLY DEFERRED` and `PRAGMA defer_foreign_keys` participate in managed
transactions and savepoints. A failed deferred `COMMIT` or outermost `RELEASE` leaves the
transaction open so the violation can be repaired. `PRAGMA foreign_keys` remains
connection-local and cannot change while a transaction is active. The managed engine
keeps the SQLite CLI default (OFF); managed `SqliteConnection` instances default it to
ON at open, matching the e_sqlite3 build Microsoft.Data.Sqlite ships
(SQLITE_DEFAULT_FOREIGN_KEYS=1). `Foreign Keys=False` in the connection string opts out.
`PRAGMA foreign_key_list` and `PRAGMA foreign_key_check` expose the retained schema and
violations. As in SQLite, named `MATCH` clauses are accepted and use MATCH SIMPLE
behavior.

Foreign keys are always resolved within the database that owns the child table. A
schema-qualified `REFERENCES` target is rejected, and an ATTACH transaction still may
mutate only one database because independent files cannot be committed atomically.
Managed schema rewriting still rejects `ALTER TABLE ADD COLUMN ... REFERENCES` and
foreign-key-dependent column renames.

### Managed row-trigger semantics

Managed local databases implement persistent SQLite row triggers plus connection-local
triggers in the `temp` schema. Trigger programs run in the database that owns their target,
remain statement-atomic with the outer DML, and persistent definitions are preserved by
reopen, managed backup, and page-size migration.

| Trigger surface | Managed local contract |
| --- | --- |
| Timing and targets | `BEFORE` and `AFTER` on tables and `INSTEAD OF` on views are supported. Omitting timing means `BEFORE`. Other timing/target combinations are rejected. |
| Row selection | SQLite row-trigger behavior is used; optional `FOR EACH ROW`, per-row `WHEN`, and `UPDATE OF` are supported. Unknown `UPDATE OF` names are accepted and never match. |
| Row images | Event-valid `OLD.column` and `NEW.column` references are supported in `WHEN`, body DML, SELECTs, and subqueries. Generated columns and `WITHOUT ROWID` primary-key columns are included. Rowid aliases are available only for rowid tables; automatic `NEW.rowid` in a BEFORE INSERT uses SQLite's undefined placeholder and must not be relied upon. |
| Body statements | Reduced `INSERT`, `UPDATE`, `DELETE`, and SELECT programs are supported. SELECT bodies may use the managed operator, join, compound, CTE, and window-function subsets. Body statements run in lexical order; SELECT rows are discarded. |
| RAISE | `RAISE(IGNORE)`, and literal-message `ROLLBACK`, `ABORT`, and `FAIL` are supported with SQLite rollback/prefix semantics. Dynamic message expressions from SQLite 3.47 and later are rejected. |
| DML interactions | Row triggers participate in `INSERT OR` conflict handling, REPLACE delete-trigger dispatch, inferred/column/expression/partial-index VALUES UPSERT targets, partial/expression-index maintenance, AUTOINCREMENT sequence tracking, top-level limited UPDATE/DELETE, and top-level RETURNING. RETURNING captures the directly changed row before AFTER-trigger changes and emits nothing if a later error aborts execution. |
| Foreign keys | Immediate/deferred checks and referential actions share the outer statement boundary. AFTER triggers may repair NO ACTION violations before statement completion. |
| Recursion | With `recursive_triggers=OFF`, only an already-active trigger program is suppressed; distinct trigger chains and FK actions continue. Recursion-enabled trigger graph cycles are rejected before callbacks or mutation because the managed evaluator cannot safely recurse to SQLite's native depth. |
| Ordering | Statements within one body are ordered. Separate matching triggers currently run newest declaration first, including after reopen/backup/migration, but applications must treat cross-trigger order as unspecified and place dependent work in one body. |
| ATTACH | Same-database persistent triggers on `main` or an attached database are supported. Their unqualified body references bind to that database, preserving the one-write-file transaction rule. |
| TEMP | `CREATE TEMP TRIGGER`, `CREATE TEMPORARY TRIGGER`, and `CREATE TRIGGER temp.name` create a connection-local temp-schema trigger, as does an unqualified `CREATE TRIGGER` whose target resolves to a TEMP table. A TEMP trigger may watch a `main` or attached table and its body may write any schema this connection can reach; those cross-schema writes are published only when the statement that fired the trigger succeeds. TEMP triggers are invisible to other connections, never reach a persistent schema, and are destroyed with the connection. A qualified TEMP trigger name is rejected, matching SQLite. |
| Cancellation | Cancellation rolls back the complete mutating statement; cancellation inside an explicit write transaction rolls back that transaction. Host callback side effects are not transactional. |
| Schema maintenance | DROP COLUMN and table/column rename validate trigger targets, WHEN clauses, row images, UPSERT expressions, query bodies, and named windows against the candidate schema before mutation. VACUUM, backup, page migration, and reopen preserve persistent trigger text and declaration order. |

The managed engine rejects these shapes before target-row mutation:

- Persistent declarations or body references that cross database schemas; qualified body
  DML targets or schema-qualified body dependencies. A TEMP trigger is exempt: its body is
  dispatched by the owning connection, so it may name any schema that connection can reach.
- `BEFORE UPDATE`/`BEFORE DELETE` programs that can directly or indirectly mutate their
  own target table, whose result SQLite documents as undefined.
- Trigger-body `INSERT ... DEFAULT VALUES`, DML `RETURNING`, UPDATE/DELETE
  `ORDER BY`/`LIMIT`, `INDEXED BY`/`NOT INDEXED`, top-level DML CTE prefixes, bind
  parameters, DDL, transaction control, PRAGMA, ATTACH, and DETACH.
- Schema-level UPDATE conflict algorithms on row-trigger targets, and INSTEAD OF view
  DML combined with conflict clauses, UPSERT, limited DML, or RETURNING.
- Table/column renames with structural trigger dependencies; independent column renames
  remain supported.
- File-backed view and trigger definitions may use the engine's built-in functions, which
  resolve identically on every connection. They remain rejected when they contain bind
  parameters, connection-registered (application-defined) functions, explicit custom
  collations, or target/reference tables declaring custom collations, because
  connection-local implementations cannot be reconstructed on reopen.

### Managed local encryption format

Managed local encryption implements Turso encrypted database format version `0` only. Page 1 starts with the 16-byte header `"Turso"`, version byte `0`, a cipher ID, and nine zero reserved bytes. The remaining SQLite header bytes stay readable and are authenticated as associated data. AES-GCM pages reserve 28 bytes for a 16-byte authentication tag and 12-byte nonce.

| Cipher ID | Rust format cipher | Key bytes | Page metadata bytes | Managed provider |
| ---: | --- | ---: | ---: | --- |
| 1 | AES-128-GCM | 16 | 28 | Supported |
| 2 | AES-256-GCM | 32 | 28 | Supported |
| 3 | AEGIS-256 | 32 | 48 | Rejected |
| 4 | AEGIS-256X2 | 32 | 48 | Rejected |
| 5 | AEGIS-256X4 | 32 | 48 | Rejected |
| 6 | AEGIS-128L | 16 | 32 | Rejected |
| 7 | AEGIS-128X2 | 16 | 32 | Rejected |
| 8 | AEGIS-128X4 | 16 | 32 | Rejected |

The managed provider never guesses a format or cipher. An unsupported version, unsupported cipher ID, configured/header cipher mismatch, missing key, wrong key, or authentication failure aborts the open without plaintext or alternate-cipher fallback.

Encrypted WAL files retain SQLite WAL format version `3007000`: the 32-byte WAL header and 24-byte frame headers are standard SQLite structures, while each frame's page image uses the database page cipher. A WAL therefore has no independent cipher marker. Managed connections always authenticate the main database header first and then use that exact cipher and key for WAL recovery. The low-level `SqliteWalFile` API must only be used with encryption options obtained from its paired database; a WAL cannot safely negotiate encryption by itself.

Managed backup is a logical snapshot copy. The source key decrypts the source connection and the destination connection independently chooses its output format and key, so backup can copy between plaintext, AES-128-GCM, and AES-256-GCM databases without copying encryption metadata or keys.

## SQLite-compatible facade coverage

- `Turso.Data.Sqlite` is the migration-oriented facade. It includes SQLite-style connection strings, commands, readers, schema metadata, transactions and savepoints, backup, managed fixed-length blob streams, scalar and aggregate UDFs, custom collations, and disabled-by-default extension loading.
- Managed `ATTACH` supports file-backed aliases, filename expressions and parameters, `file:` URIs with `mode=ro|rw|rwc`, inherited page encryption, same-cipher hexadecimal `KEY` overrides, same-database SELECT/DML/CTE/subquery routing, and transactions/savepoints that modify at most one persistent database. A transaction may also modify its connection-private TEMP catalog. Statements whose reads span multiple database schemas and transactions that attempt to write a second persistent database are rejected before the unsafe operation because independent WAL files cannot be committed atomically. Attached in-memory databases, URI options other than `mode`, cross-database views/triggers, and plaintext-to-encrypted `KEY` attachment without a primary cipher remain unsupported.
- Managed pooling retains at most 32 idle physical connections per canonical file/read-only key and at most 64 keys. `:memory:`, `Mode=Memory`, shared-memory, encrypted, native, remote/replica, and connections with custom functions, aggregates, or collations are not pooled. Returning a pooled connection closes readers and blobs, rolls back transactions, invalidates prepared commands, detaches databases, destroys the TEMP catalog, and resets connection-local pragmas and row-id state. Renting it refreshes the managed catalog from durable storage before reuse.
- File-backed managed indexes preserve explicit, `UNIQUE`, and `PRIMARY KEY` origin and term metadata, including mixed `ASC`/`DESC` order and SQLite's built-in `BINARY`, `NOCASE`, and `RTRIM` collations. Implicit constraint indexes cover table-level and column-level forms, including non-rowid-alias `TEXT` or composite `PRIMARY KEY` declarations and `INTEGER PRIMARY KEY DESC`, which SQLite backs with a separate `sqlite_autoindex` unique index. Rich index mutations use an atomic full-tree rewrite; the bounded in-place path remains limited to ascending `BINARY` terms. Application-defined index collations remain rejected before publication because their ordering cannot be reconstructed safely on reopen.
- Managed SELECT sources accept SQLite's `INDEXED BY index-name` and `NOT INDEXED` clauses after an optional alias. `INDEXED BY` forces the named index, including constraint-owned, expression, collated, descending, partial, and `WITHOUT ROWID` secondary indexes; a missing or wrong-table index fails instead of silently choosing another route, and a partial index fails with `no query solution` unless the query predicates safely imply its `WHERE` clause for that join side. `NOT INDEXED` suppresses managed secondary-index selection while retaining SQLite's rowid/primary-key table semantics.
- `SqliteConnection.ClearPool(connection)` retires the file/read-only pool selected by that connection string, and `SqliteConnection.ClearAllPools()` retires every managed pool. Idle handles are disposed immediately; rented handles are disposed instead of being reused when returned, so clearing is safe while connections are open.
- `TursoConnection` uses the same contract when `Pooling=True` is explicitly selected and exposes corresponding `TursoConnection.ClearPool(connection)` and `TursoConnection.ClearAllPools()` methods.
- Raw SQLitePCL `sqlite3*` handle interop is intentionally unsupported. `SqliteConnection.Handle` returns `null` rather than exposing a fake SQLite handle.
- Managed physical databases are not concurrently interoperable with ordinary SQLite clients. All managed connections in one process share exclusive ownership of SQLite's main-file lock-byte range until the last connection is disposed; other managed processes and SQLite clients receive a busy/ownership failure. Windows uses its native byte-range locks and 64-bit Linux uses open-file-description locks so closing a secondary database descriptor cannot silently release ownership. Do not mix a native SQLite client into the owning process. A handoff to SQLite is one-way: dispose every managed connection after a successful commit and, in WAL mode, checkpoint, then let SQLite own the database and companion-file lifecycle. Disposing the logical connections is not sufficient on its own when pooling is active: the SQLite facade defaults to `Pooling=True`, and a returned handle stays in the managed physical pool holding ownership, so a subsequent SQLite client still fails with `database is locked`. Open the database with `Pooling=False`, or call `SqliteConnection.ClearAllPools()` after disposing, before handing off. This applies to read-only usage too, because every physical pager takes the same ownership lock. SQLite may delete or replace managed WAL sidecars, so the managed provider deliberately refuses to reopen an altered WAL pair. After an interrupted managed writer, reopen it with the managed provider first so managed WAL or rollback-journal recovery completes. Windows can retain the ownership lock through a read-only database handle; platforms whose lock primitive requires writable access still reject read-only ownership when the file cannot be opened accordingly. The managed `-shm` file is a byte-lock carrier only and never contains a SQLite WAL-index. The one read-only exception is `Foreign Read Only=True`, which explicitly opts out of ownership to read a database owned by another engine; see [Managed foreign read-only opens](#managed-foreign-read-only-opens). [Managed WAL interoperability contract](docs/managed-wal-interoperability-contract.md) documents the exact lock-byte map, busy/recovery/cache-invalidation rules, the foreign read-only contract, and the staged work required before multi-process WAL access could be supported.
- Managed file databases implement durable `WAL` and `DELETE` journal modes. `DELETE` writes use SQLite-compatible rollback journals containing exact on-disk page images, so encrypted pages remain encrypted; writable reopen recovers a valid hot journal, while read-only reopen fails without modifying it. `PERSIST`, `TRUNCATE`, `MEMORY`, and `OFF` are not implemented for files and leave the current mode unchanged.
- Managed metadata PRAGMAs are schema-aware for `main`, `temp`, and attached databases. The supported catalog surface includes `database_list`, `table_list`, `table_info`, `table_xinfo`, `index_list`, `index_info`, `index_xinfo`, `foreign_key_list`, and `foreign_key_check`; `WITHOUT ROWID` primary-key pseudo-indexes, generated columns, STRICT flags, partial/expression terms, and foreign-key actions retain SQLite-compatible result shapes. `encoding` reports the selected database header. File-backed `page_count` and `freelist_count` report the committed pager; memory and TEMP databases reject those two queries rather than inventing page allocation. `schema_version`, `user_version`, and `application_id` are durable and transactional. Unsupported PRAGMAs still fail rather than claiming pager behavior that is not implemented. Tuning PRAGMAs that would imply unimplemented pager behavior, including `cache_size`, `synchronous`, `locking_mode`, `busy_timeout`, `wal_checkpoint`, `wal_autocheckpoint`, `auto_vacuum`, `max_page_count`, `temp_store`, and `mmap_size`, are rejected.
- `PRAGMA integrity_check` and `PRAGMA quick_check` report the declared NOT NULL and CHECK constraints that stored rows violate, honor SQLite's default 100-problem budget and its optional integer limit, accept a bare table-name restriction, and return a single `ok` row for a healthy database. Both return the same problems and differ only in their result column name: the managed file store validates stored index records and page structure while loading, so a database SQLite would describe as `non-unique entry in index` or `wrong # of entries in index` fails to open instead of being reported. There is no managed auto-checkpoint policy, so a WAL grows until one of the engine's own checkpoint points is reached.
- `REINDEX` atomically rebuilds the selected managed table/index (including rich, partial, expression, constraint, and `WITHOUT ROWID` forms) through a forced full-catalog pager rewrite without changing the schema cookie. Unqualified all-index and collation forms are supported when no database is attached; with attachments, callers must qualify one table or index so independent files are never presented as one atomic mutation. `ANALYZE` is an explicit pre-mutation error because managed `sqlite_stat*` persistence and planner consumption are not implemented.
- `PRAGMA page_size` accepts SQLite page sizes from 512 through 65536 as a pending value per selected database. `PRAGMA [schema.]journal_mode`, `VACUUM`, `VACUUM main`, and attached-schema `VACUUM` target that database independently; in-place page-size changes apply only in `DELETE` mode, while `VACUUM ... INTO <expression>` may apply the pending size in either journal mode without changing the source. `INTO` publishes a compact `DELETE`-mode snapshot through an atomic file-system replacement, retains encryption, AUTOINCREMENT sequence state, and supported catalog/header semantics, and accepts only ordinary file paths on file systems that provide that guarantee. VACUUM rejects active transactions, result readers, blob handles, read-only/query-only connections, unsafe output aliases, and non-empty destinations before mutating authoritative storage.
- Managed named shared-memory databases use `Data Source=NAME;Mode=Memory;Cache=Shared`. Connections with the same case-sensitive name share one managed catalog and page/cache owner until the last logical connection closes. Reopening while another connection remains open preserves the database; reopening after the last close creates an empty database. The SQLite facade accepts `Pooling=True` for connection-string compatibility but never pools shared memory; `TursoConnection` requires `Pooling=False` and rejects `Pooling=True` before opening. `ClearPool` and `ClearAllPools` affect file pools only.
- Managed `Cache=Shared` is accepted for file databases but treated as an ordinary private file connection per open: the managed engine cannot emulate SQLite's shared-cache semantics (cross-connection uncommitted visibility and table locks) for files, so it deliberately provides the stronger private isolation instead of rejecting the keyword. Anonymous in-memory databases follow SQLite semantics: an empty Data Source or a `:memory:` Data Source without `Mode=Memory` stays connection-private even with `Cache=Shared`, while `Data Source=:memory:;Mode=Memory;Cache=Shared` routes through the shared-cache URI form and becomes a named shared-memory database named `:memory:`, matching Microsoft.Data.Sqlite. `Cache=Private` and the default cache remain connection-private for memory databases. Managed named shared-memory connections also reject connection-local functions, aggregates, and collations because the current managed function registry belongs to the shared database rather than an individual connection; private anonymous in-memory connections keep their own catalog and therefore allow them.
- `PRAGMA read_uncommitted` remains connection-local compatibility state for native and managed private-cache connections. Managed shared-memory databases preserve transaction isolation and reject enabling `PRAGMA read_uncommitted` or beginning an `IsolationLevel.ReadUncommitted` transaction rather than claiming unsupported dirty-read behavior.
- Managed `BackupDatabase` atomically replaces existing managed destinations, including schema, rows, `schema_version`, `user_version`, and `application_id`, and accepts `main` or attached database names. The connection-private TEMP database is excluded, and selecting `temp` as a named source or destination is rejected before destination mutation. Active `main` source transactions are copied from their current snapshot without being completed, and active source readers remain usable. Selecting an attached source while its owning connection has an active transaction fails busy before destination mutation because that attachment's transaction clone cannot yet be exposed as an independent backup source. Non-transactional file sources are reopened before snapshot acquisition so commits from other connections are included. Memory and physical-file endpoints are supported, including encrypted file-to-file re-encryption; failures before publication leave the destination unchanged.
- Managed backup rejects an active destination transaction or reader, copying a recognized database identity onto itself, file-to-file copies through custom file systems with unknown identity semantics, and managed/native provider mixing before changing the destination. Physical files acquire exclusive SQLite lock-byte ownership when opened, so hard-link, junction, symbolic-link, short-path, and case aliases cannot be opened as a second managed database and therefore cannot reach destination mutation.
- Managed file persistence reads and writes SQLite-compatible `sqlite_schema` overflow chains, including encrypted and small-page databases. Overflowing definitions remain atomic across WAL/DELETE commits, backup, ATTACH, pooling refresh, and page-size migration; malformed chains fail closed before publication.
- Managed file persistence reads and writes SQLite-compatible `WITHOUT ROWID` tables with composite `ASC`/`DESC` primary keys, built-in `BINARY`/`NOCASE`/`RTRIM` collations, VIRTUAL and STORED generated columns, explicit secondary indexes, implicit `UNIQUE` indexes, expression key terms, and partial-index `WHERE` predicates. Secondary-index records carry the required primary-key suffix, and managed files round-trip through ordinary SQLite in WAL or DELETE mode, backup, ATTACH, pooling, encryption, and page-size migration. Index expressions may use the managed engine's deterministic built-ins; parameters, subqueries, aggregate/window or non-deterministic functions, registered scalar-function overrides, application-defined collations, cross-table references, and schema forms that cannot round-trip through `sqlite_schema` fail before publication.
- Managed `SqliteBlob` supports fixed-length reads and bounded writes for rowid tables in `main` and named attached databases. Use the database-name constructor for attachments. Blob writes participate in transactions subject to the managed `ATTACH` single-write-database boundary. A handle is invalidated if its row changes, and an open attached handle blocks `DETACH`.
- Resizing, writable blobs on tables with `UPDATE` triggers, and `WITHOUT ROWID` tables are rejected before changing data. Attached databases inherit the primary managed file system, including encryption, and blob changes remain durable after reopen.
- Native providers may expose virtual-table modules supplied by their native extension build. The managed provider has no safe module registration, lifecycle, planner, or execution interface for user-defined modules, so `CREATE VIRTUAL TABLE` is rejected during parsing before schema mutation; it never fabricates FTS or other module support. A fixed set of built-in table-valued FROM sources is executable: `generate_series(start, stop, step)`, `json_each`/`json_tree`, and the `pragma_*` introspection family (`pragma_table_info`, `pragma_table_xinfo`, `pragma_index_list`, `pragma_index_info`, `pragma_index_xinfo`, `pragma_foreign_key_list`, `pragma_table_list`, `pragma_cache_size`). Each accepts an ordinary table alias, an optional schema qualifier, and SQLite's hidden argument columns, either positionally or through `WHERE hidden_column = <constant>` when the name is written without parentheses. Arguments must be constant with respect to the row being produced: SQLite's implicit `LATERAL` form, where an argument references a column of a table earlier in the same `FROM` clause (`FROM t JOIN json_each(t.b)`), is rejected with an unresolved-column error rather than producing a partial result, because the managed join evaluates each source once instead of re-evaluating a correlated source per outer row. Any other `module_name(...)` source form is rejected during parsing, including inside CTAS, before query execution or catalog mutation.
- The managed engine does not implement experimental MVCC or vector-search functions. `PRAGMA journal_mode = mvcc` and functions such as `vector32()` fail rather than enabling partial behavior.
- Local managed `OpenAsync` and command/reader async methods run blocking work
  off the caller thread and cooperatively observe cancellation tokens and
  `DbCommand.Cancel()` during execution. `CommandTimeout` applies to busy reader
  waits; it is not a general query-execution deadline.

### Managed TEMP / CTAS / STRICT / virtual-table matrix

| Surface | Supported managed contract | Rejected managed contract |
|---|---|---|
| TEMP catalog | `CREATE TEMP TABLE`, `CREATE TEMPORARY TABLE`, and `CREATE TABLE temp.name`; ordinary table constraints, generated columns, indexes declared as `temp.index_name`, and same-catalog foreign keys. `CREATE TEMP VIEW` and `CREATE TEMP TRIGGER` are supported and reported by `sqlite_temp_schema`/`sqlite_temp_master` with the TEMP keyword stripped from `sql`. Unqualified lookup order is `temp`, `main`, then attachments; explicit schema names remain authoritative. TEMP state is private to one physical managed connection, participates in transactions/savepoints, survives commits, and is destroyed on connection disposal or pool reset. | TEMP view bodies that reference objects outside the `temp` schema, attached in-memory databases, TEMP incremental blobs, and named TEMP backup sources/destinations. Main/attached backup and reopen never include TEMP objects. |
| `CREATE TABLE AS SELECT` | Atomic materialization from SELECT, VALUES, compound queries, or CTEs. The destination may be `temp`, `main`, or an attachment, and its single source schema may differ from the destination. Result names use SQLite `:N` de-duplication, declared types use SQLite expression-affinity names (`INT`, `NUM`, `REAL`, `TEXT`, or empty), rowids start at 1 in result order, and empty results retain declared columns. Publication occurs only after query completion and cancellation checks. | Explicit destination column definitions, `STRICT`/`WITHOUT ROWID` CTAS options, queries reading more than one database schema, or inheritance of source constraints, generated-column status, foreign keys, indexes, or triggers. Failure/cancellation leaves no destination object. |
| STRICT tables | `INT`, `INTEGER`, `REAL`, `TEXT`, `BLOB`, and `ANY`; lossless affinity conversion followed by storage-class enforcement on INSERT, UPDATE, generated values, defaults, and trigger writes. `ANY` preserves the incoming storage class. STRICT metadata survives WAL/DELETE reopen, backup, and DELETE-mode page-size migration, appears in regenerated `sqlite_schema.sql`, and is reported by `PRAGMA table_list`. | Missing types, names outside the six SQLite STRICT types, or values that cannot be losslessly stored in the declared type. Errors occur before catalog/data publication. |
| Virtual tables | None in the managed provider. Capability reporting keeps managed extension/module support disabled. | Every `CREATE VIRTUAL TABLE` form is rejected before mutation because no managed module callbacks or planner/executor contract exists. |
| Table-valued FROM sources | Built-in `generate_series`, `json_each`, `json_tree`, and the `pragma_*` introspection family, each with an optional alias, an optional schema qualifier, positional or `WHERE`-bound hidden arguments, and SQLite's column sets. A real table, view, or CTE of the same name always wins over the built-in. | Every other `module_name(...)` source is rejected during parsing because no managed module planner/executor contract exists for user-defined modules; CTAS therefore fails before destination publication. SQLite's implicit `LATERAL` form, where an argument references a column of an earlier `FROM` entry, fails with an unresolved-column error instead of returning a partial result. |

### Managed query-plan diagnostics

The managed provider supports both SQLite diagnostic statement forms. `EXPLAIN` returns
`addr`, `opcode`, `p1`, `p2`, `p3`, `p4`, and `comment` for a statement that is fully
lowered to the managed VDBE. It returns an error for evaluator-owned statements rather
than fabricating bytecode. Supported parameter expressions emit `LoadParameter` and stay
late-bound, so their values are not embedded in the VDBE dump and rebinding does not
change an otherwise identical compiled shape.

`EXPLAIN QUERY PLAN` keeps SQLite's public `id`, `parent`, `notused`, and `detail`
columns and reports the managed execution boundary rather than fabricated SQLite
optimizer internals. Most statements return one deterministic row whose first three
columns are `0` and whose `detail` is `MANAGED COMPILED VDBE` or
`MANAGED EVALUATOR FALLBACK`. When the managed engine has selected or been forced to use
a real single-table index row source, the detail instead reports `SCAN` or `SEARCH`,
the source qualifier, and `USING INDEX index-name`. Hinted joins stay evaluator-owned
until a join compiler can represent their per-source index order, so their plan remains
`MANAGED EVALUATOR FALLBACK` rather than claiming an ignored index.

All parameters must be bound before stepping; their values are used to choose the same
route as normal execution but are never rendered in the plan row. Planning never runs
the emitted program, evaluator, user callbacks, or DML write target. Direct SELECT and
DML plans stepped with a cancellation-capable token report evaluator fallback when that
is their runtime boundary. CTE plans that materialize inputs report evaluator fallback.
A proven single-CTE `NOT
MATERIALIZED` pass-through and the guarded linear recursive worktable route report
compiled VDBE only when their whole-statement output matches the emitted program.
Unsupported non-query/non-DML statements fail explicitly. The managed plan does not
claim costs, cardinalities, covering status, or index choices that execution does not
actually use.

## Entity Framework Core

`Turso.EntityFrameworkCore.Sqlite` adds a `UseTurso` provider hook for local
managed or native Turso databases. It reuses EF Core SQLite's LINQ translation
pipeline and executes generated SQL through the local-only `Turso.Data.Sqlite`
facade. Embedded replicas and remote Hrana URLs are not part of this provider
line.

The current provider line supports EF Core 9.x on `net8.0`, `net9.0`, and `net10.0`. Its package dependency is constrained to `Microsoft.EntityFrameworkCore.Sqlite.Core` versions `[9.0.9, 10.0.0)` because the provider integrates with EF Core's internal SQLite services, and `UseTurso` rejects any other loaded EF Core major during options configuration. Do not override that dependency with EF Core 8.x or 10.x; those majors require separately compiled and tested provider lines.

```bash
dotnet add package Turso.EntityFrameworkCore.Sqlite
```

```C#
using Microsoft.EntityFrameworkCore;

public sealed class AppDbContext : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseTurso("Data Source=app.db");
}
```

You can also pass an existing Turso SQLite-compatible connection:

```C#
using Microsoft.EntityFrameworkCore;
using Turso.Data.Sqlite;

await using var connection = new SqliteConnection("Data Source=app.db");
var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseTurso(connection)
    .Options;
```

The local provider supports the normal EF Core SQLite query pipeline, including composed `IQueryable<T>` filters, navigation-property joins, ordering, paging, grouping, aggregates, async materialization, and `SaveChangesAsync`. Schema creation can use `EnsureCreated`, `EnsureCreatedAsync`, and EF migrations against local database files. Managed migrations support history tracking, literal defaults, descending indexes, and model-backed table, column, and index renames when the renamed table or column has no foreign-key, table-constraint, trigger, or computed-column dependencies. Filtered indexes, SQL-expression defaults, raw SQL operations, idempotent migration scripts, and dependent renames fail before application schema mutation because the managed engine cannot execute those forms safely.

Remote `libsql://`, `http://`, `https://`, `ws://`, and `wss://` EF Core support
is not part of the local provider. `UseTurso` rejects those data sources during
options configuration, before a context or connection is used. Embedded replicas
are also excluded because `UseTurso` executes through `SqliteConnection`. Use
`TursoConnection` directly for remote or replica ADO.NET access; remote/serverless
EF support needs a separate retry and transaction design.
