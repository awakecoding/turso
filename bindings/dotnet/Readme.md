# Turso .NET

ADO.NET bindings for Turso local and remote databases.

The `Turso.Data.Sqlite` package includes both a SQLite-compatible `Turso.Data.Sqlite` facade and Turso-specific `System.Data.Common` types such as `TursoConnection`, `TursoCommand`, `TursoDataReader`, `TursoParameter`, `TursoTransaction`, and `TursoFactory`.

## Install

```bash
dotnet add package Turso.Data.Sqlite
```

Application code only needs to reference `Turso.Data.Sqlite`.

The package targets `net8.0`, `net9.0`, and `net10.0`. It is managed-only: local connections use the managed provider by default and no Rust toolchain or native runtime asset is needed to restore, build, pack, or run it. Its package contains no `runtimes/` assets, `Turso.Raw` dependency, or native build target.

## Dynamic native compatibility

Applications that intentionally select `Local Provider=Native` can reference the matching-version `Turso.Data.Sqlite.Native` companion package:

```xml
<ItemGroup>
  <PackageReference Include="Turso.Data.Sqlite" Version="0.7.0-pre.18" />
  <PackageReference Include="Turso.Data.Sqlite.Native" Version="0.7.0-pre.18" />
</ItemGroup>
```

`Turso.Data.Sqlite.Native` activates the native provider and resolves its `Turso.Raw` runtime companion for Windows, Linux, macOS, Android (`android-arm64`, `android-arm`, `android-x64`, and `android-x86`), and iOS as an XCFramework with an arm64 device slice and a universal arm64+x64 simulator slice. These are optional companion packages with their own native-asset validation; they are not restored or packed by the managed release path. Remote Turso/libSQL connections use the managed HTTP client and do not require either native package.

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
  <PackageReference Include="Turso.Data.Sqlite" Version="0.7.0-pre.18" />
  <PackageReference Include="Turso.Data.Sqlite.Sync" Version="0.7.0-pre.18" />
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
  <PackageReference Include="Turso.Data.Sqlite" Version="0.7.0-pre.18" />
  <PackageReference Include="Turso.Data.Sqlite.NativeAot.win-x64" Version="0.7.0-pre.18" PrivateAssets="all" />
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

### Managed DML bytecode

Managed `INSERT`, `UPDATE`, and `DELETE` reuse the same generic expression lowering for `RETURNING`: literals, late-bound parameters, affected-row columns or rowid, qualified stars/columns, nested numeric arithmetic with SQLite affinity, value-only `COLLATE`, and the allow-listed built-ins above compile. `UPDATE` and `DELETE` predicates may use the evaluator's row-local scalar expression subset, including nested arithmetic, logical/comparison operators, and scalar functions, because the VDBE filter invokes that evaluator at the original per-row position.

The compiled program first scans predicates and buffers all mutations, then evaluates buffered `RETURNING` rows in source and projection order, and commits only after projection succeeds. This retains predicate/assignment user-callback timing, keeps projection errors statement-atomic, and remains resumable across returned rows. Subqueries, aggregates/windows, `CASE`, `CAST`, concatenation/comparison projections, volatile or context-dependent functions, and shadowed user functions remain evaluator-owned. DML with a cancellation-capable token, foreign-key enforcement, open incremental blobs, conflict algorithms, source `INSERT`, CTE scope, or schema tables also falls back.

The managed SQL contract enables SQLite's optional single-table `UPDATE`/`DELETE` `ORDER BY ... LIMIT` grammar independently of the bundled native SQLite compile options. `LIMIT ... OFFSET ...` and `LIMIT offset, count` accept bound parameters, negative limits are unbounded, and negative offsets clamp to zero. `RETURNING`, when present, precedes `ORDER BY`; ordering chooses the affected subset but does not reorder mutation or `RETURNING` output. Limited DML stays evaluator-owned so selection expressions run before source-ordered buffered mutation and statement-atomic projection. UPDATE conflict algorithms, UPDATE-FROM, row-value assignments, target aliases, `INDEXED BY`/`NOT INDEXED`, `ORDER BY` without `LIMIT`, and limited DML inside trigger bodies are rejected during parsing.

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
| Async open, command, reader, transaction, batch | Yes, worker-backed local I/O | Yes, worker-backed local I/O | Yes, HTTP I/O | Yes, replica/native I/O | Yes, worker-backed local I/O | Yes, worker-backed local I/O |
| Transactions | Yes | Yes | Yes | Yes | Yes | Yes |
| Savepoints | Yes | Yes | Yes | Yes | Yes | Yes |
| `BackupDatabase` | No facade API | No facade API | No | No | Yes | Yes |
| `SqliteBlob` fixed-length incremental I/O | No facade API | No facade API | No | No | Yes, managed handle | Yes, SQL-backed compatibility |
| Scalar UDFs / aggregates / collations | No facade API | No facade API | No | No | Yes | Yes |
| Loadable extensions | No facade API | No facade API | No | No | No | Yes, disabled by default |
| `ATTACH` / `DETACH` | Yes, with managed limits | Yes | No | No | Yes, with managed limits | Yes |
| Managed connection pooling | Eligible unencrypted files when `Pooling=True`; named shared memory accepts the keyword but is not pooled | No | No | No | Eligible unencrypted files; named shared memory accepts the keyword but is not pooled | No |
| Explicit `Sync` | No | No | No | Yes | No | No |

`Turso.Data.Sqlite` is a local-only migration facade; remote URLs fail before they
can be interpreted as file paths. Use `TursoConnection` for Hrana and embedded
replicas. `TursoConnection` also rejects `Pooling=True` before provider or network
access unless the target is an eligible unencrypted managed file or a valid named
managed shared-memory database. Named shared memory accepts the compatibility
keyword without placing its shared owner in the physical connection pool. The SQLite facade
continues to accept its default `Pooling=True` keyword for native compatibility, but
native handles are not pooled. Memory, shared-memory, encrypted, callback-bearing,
remote, and replica connections are never pooled.

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

Supported common connection string keywords include:

| Keyword | Notes |
| --- | --- |
| `Data Source` | Database path or `:memory:`. Aliases include `DataSource` and `Filename`. |
| `Mode` | Parsed and preserved for compatibility. |
| `Cache` | Managed `Cache=Shared` is supported only with a named `Data Source` and `Mode=Memory`; see the shared-cache contract below. |
| `Foreign Keys` | Parsed and preserved for compatibility. |
| `Local Provider` | `Managed` is the default for local databases. Set `Native` when `Turso.Data.Sqlite.Native` or a RID-specific `Turso.Data.Sqlite.NativeAot.*` companion is referenced. |
| `Recursive Triggers` | Parsed and preserved for compatibility. |
| `Default Timeout` | Used as the default command timeout. Aliases include `Command Timeout`. |
| `Pooling` | Defaults to `True` on the SQLite-compatible facade. Managed physical pooling applies only to ordinary unencrypted file-backed databases; valid named shared-memory databases accept the keyword without being pooled. |
| `Vfs` | Parsed and preserved for compatibility. |
| `Encryption Cipher` | Turso local encryption cipher. |
| `Encryption Key` | Hex-encoded encryption key used with `Encryption Cipher`. |
| `Auth Token` | Bearer token for remote Turso/libSQL URLs. Aliases include `AuthToken` and `Authentication Token`. |
| `Replica Path` | Local path for an embedded replica. Requires the `Turso.Data.Sqlite.Sync` companion package and a remote Turso URL. |
| `Read Your Writes` | Keeps the remote Hrana session baton across commands. Defaults to `True`. Set `False` for stateless one-shot remote requests. |
| `Sync Interval` | Retained for connection-string compatibility. Only `0` is accepted; call `TursoConnection.Sync()` or `SyncAsync(CancellationToken)` explicitly and await every operation. |
| `Tls` | Optional override for `libsql://` development URLs. Conflicting values with explicit `http://` or `https://` schemes fail early. |

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
- Managed `ATTACH` supports file-backed aliases, filename expressions and parameters, `file:` URIs with `mode=ro|rw|rwc`, inherited page encryption, same-cipher hexadecimal `KEY` overrides, same-database SELECT/DML/CTE/subquery routing, and transactions/savepoints that modify at most one database. Statements spanning databases and transactions that attempt to write a second database are rejected before that second write because independent WAL files cannot be committed atomically. The managed engine does not implement `temp`, attached in-memory databases, URI options other than `mode`, cross-database views/triggers, or plaintext-to-encrypted `KEY` attachment without a primary cipher.
- Managed pooling retains at most 32 idle physical connections per canonical file/read-only key and at most 64 keys. `:memory:`, `Mode=Memory`, shared-memory, encrypted, native, remote/replica, and connections with custom functions, aggregates, or collations are not pooled. Returning a pooled connection closes readers and blobs, rolls back transactions, invalidates prepared commands, detaches databases, and resets connection-local pragmas and row-id state. Renting it refreshes the managed catalog from durable storage before reuse.
- `SqliteConnection.ClearPool(connection)` retires the file/read-only pool selected by that connection string, and `SqliteConnection.ClearAllPools()` retires every managed pool. Idle handles are disposed immediately; rented handles are disposed instead of being reused when returned, so clearing is safe while connections are open.
- `TursoConnection` uses the same contract when `Pooling=True` is explicitly selected and exposes corresponding `TursoConnection.ClearPool(connection)` and `TursoConnection.ClearAllPools()` methods.
- Raw SQLitePCL `sqlite3*` handle interop is intentionally unsupported. `SqliteConnection.Handle` returns `null` rather than exposing a fake SQLite handle.
- Managed physical databases are not concurrently interoperable with ordinary SQLite clients. All managed connections in one process share exclusive ownership of SQLite's main-file lock-byte range until the last connection is disposed; other managed processes and SQLite clients receive a busy/ownership failure. Windows uses its native byte-range locks and 64-bit Linux uses open-file-description locks so closing a secondary database descriptor cannot silently release ownership. Do not mix a native SQLite client into the owning process. A handoff to SQLite is one-way: dispose every managed connection after a successful commit and, in WAL mode, checkpoint, then let SQLite own the database and companion-file lifecycle. SQLite may delete or replace managed WAL sidecars, so the managed provider deliberately refuses to reopen an altered WAL pair. After an interrupted managed writer, reopen it with the managed provider first so managed WAL or rollback-journal recovery completes. Windows can retain the ownership lock through a read-only database handle; platforms whose lock primitive requires writable access still reject read-only ownership when the file cannot be opened accordingly.
- Managed file databases implement durable `WAL` and `DELETE` journal modes. `DELETE` writes use SQLite-compatible rollback journals containing exact on-disk page images, so encrypted pages remain encrypted; writable reopen recovers a valid hot journal, while read-only reopen fails without modifying it. `PERSIST`, `TRUNCATE`, `MEMORY`, and `OFF` are not implemented for files and leave the current mode unchanged.
- `PRAGMA page_size` accepts SQLite page sizes from 512 through 65536 as a pending value. `VACUUM` applies a different size only in `DELETE` mode by publishing a complete journal-protected replacement; in `WAL` mode it retains the current size. Migration preserves supported schema constraints, text encoding, encryption, and header metadata, and rejects active transactions, readers, blob handles, or attachments rather than publishing a partial format.
- Managed named shared-memory databases use `Data Source=NAME;Mode=Memory;Cache=Shared`. Connections with the same case-sensitive name share one managed catalog and page/cache owner until the last logical connection closes. Reopening while another connection remains open preserves the database; reopening after the last close creates an empty database. The SQLite facade accepts `Pooling=True` for connection-string compatibility but never pools shared memory; `TursoConnection` requires `Pooling=False` and rejects `Pooling=True` before opening. `ClearPool` and `ClearAllPools` affect file pools only.
- Managed `Cache=Shared` is rejected for file databases, empty or `:memory:` data sources, and modes other than `Memory`, because those configurations cannot provide a true shared managed page/cache owner. `Cache=Private` and the default cache remain connection-private for memory databases. Managed shared-memory connections also reject connection-local functions, aggregates, and collations because the current managed function registry belongs to the shared database rather than an individual connection.
- `PRAGMA read_uncommitted` remains connection-local compatibility state for native and managed private-cache connections. Managed shared-memory databases preserve transaction isolation and reject enabling `PRAGMA read_uncommitted` or beginning an `IsolationLevel.ReadUncommitted` transaction rather than claiming unsupported dirty-read behavior.
- Managed `BackupDatabase` atomically replaces existing managed destinations, including schema, rows, `schema_version`, `user_version`, and `application_id`, and accepts `main` or attached database names. Active `main` source transactions are copied from their current snapshot without being completed, and active source readers remain usable. Selecting an attached source while its owning connection has an active transaction fails busy before destination mutation because that attachment's transaction clone cannot yet be exposed as an independent backup source. Non-transactional file sources are reopened before snapshot acquisition so commits from other connections are included. Memory and physical-file endpoints are supported, including encrypted file-to-file re-encryption; failures before publication leave the destination unchanged.
- Managed backup rejects an active destination transaction or reader, copying a recognized database identity onto itself, file-to-file copies through custom file systems with unknown identity semantics, and managed/native provider mixing before changing the destination. Physical files acquire exclusive SQLite lock-byte ownership when opened, so hard-link, junction, symbolic-link, short-path, and case aliases cannot be opened as a second managed database and therefore cannot reach destination mutation.
- Managed file persistence rejects schema SQL that requires `sqlite_schema` overflow pages before publishing the catalog or WAL commit. Encryption reduces usable page space, so encrypted databases can reach this explicit bound sooner.
- Managed `SqliteBlob` supports fixed-length reads and bounded writes for rowid tables in `main` and named attached databases. Use the database-name constructor for attachments. Blob writes participate in transactions subject to the managed `ATTACH` single-write-database boundary. A handle is invalidated if its row changes, and an open attached handle blocks `DETACH`.
- Resizing, writable blobs on tables with `UPDATE` triggers, and `WITHOUT ROWID` tables are rejected before changing data. Attached databases inherit the primary managed file system, including encryption, and blob changes remain durable after reopen.
- SQLite virtual-table modules such as FTS3/FTS5 are not built in unless provided by a Turso extension/module.
- The managed engine does not implement experimental MVCC or vector-search functions. `PRAGMA journal_mode = mvcc` and functions such as `vector32()` fail rather than enabling partial behavior.
- Local managed `OpenAsync` and command/reader async methods run blocking work
  off the caller thread and cooperatively observe cancellation tokens and
  `DbCommand.Cancel()` during execution. `CommandTimeout` applies to busy reader
  waits; it is not a general query-execution deadline.

### Managed query-plan diagnostics

The managed provider supports both SQLite diagnostic statement forms. `EXPLAIN` returns
`addr`, `opcode`, `p1`, `p2`, `p3`, `p4`, and `comment` for a statement that is fully
lowered to the managed VDBE. It returns an error for evaluator-owned statements rather
than fabricating bytecode. Supported parameter expressions emit `LoadParameter` and stay
late-bound, so their values are not embedded in the VDBE dump and rebinding does not
change an otherwise identical compiled shape.

`EXPLAIN QUERY PLAN` keeps SQLite's public `id`, `parent`, `notused`, and `detail`
columns but reports the managed execution boundary, not SQLite optimizer internals. It
returns one deterministic row: the first three columns are `0`, and `detail` is either
`MANAGED COMPILED VDBE` or `MANAGED EVALUATOR FALLBACK`. All parameters must be bound
before stepping; their values are used to choose the same route as normal execution but
are never rendered in the plan row. Planning never runs the emitted program, evaluator,
or DML write target. Direct SELECT and DML plans stepped with a cancellation-capable
token report evaluator fallback, matching their runtime cancellation boundaries. CTE
plans report evaluator fallback because execution materializes CTE inputs before any
later compiled phase. Unsupported non-query/non-DML statements fail explicitly.

These rows intentionally do not claim table scans, index choices, costs, cardinalities,
or other planner details that the managed engine does not expose. SQLite's column shape
is the compatibility contract; the two managed `detail` strings and fixed IDs are the
stable managed contract.

## Entity Framework Core

`Turso.EntityFrameworkCore.Sqlite` adds a `UseTurso` provider hook for local and embedded Turso databases. It reuses EF Core SQLite's LINQ translation pipeline and executes generated SQL through `Turso.Data.Sqlite`.

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

The local provider supports the normal EF Core SQLite query pipeline, including composed `IQueryable<T>` filters, navigation-property joins, ordering, paging, grouping, aggregates, async materialization, and `SaveChangesAsync`. Schema creation can use `EnsureCreated`, `EnsureCreatedAsync`, and EF migrations against local database files. Managed migrations support history tracking, literal defaults, and model-backed table, column, and index renames when the renamed table or column has no foreign-key, table-constraint, trigger, or computed-column dependencies. Filtered indexes, descending indexes, SQL-expression defaults, raw SQL operations, idempotent migration scripts, and dependent renames fail before application schema mutation because the managed engine cannot execute those forms safely.

Remote `libsql://`, `http://`, `https://`, `ws://`, and `wss://` EF Core support is not part of the local provider. `UseTurso` rejects those data sources during options configuration, before a context or connection is used. Use the local/embedded provider for EF Core today or use `TursoConnection` directly for remote ADO.NET access; remote/serverless EF support needs a separate retry and transaction design.
