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
pushes local changes then pulls and applies remote changes. Automatic `Sync Interval`
is intentionally unsupported. The companion resolves its native runtime assets on
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

Remote mode also supports ADO.NET `DbBatch` for latency-sensitive workloads that should be sent in one Hrana batch:

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
| `Cache` | Parsed and preserved for compatibility. |
| `Foreign Keys` | Parsed and preserved for compatibility. |
| `Local Provider` | `Managed` is the default for local databases. Set `Native` when `Turso.Data.Sqlite.Native` or a RID-specific `Turso.Data.Sqlite.NativeAot.*` companion is referenced. |
| `Recursive Triggers` | Parsed and preserved for compatibility. |
| `Default Timeout` | Used as the default command timeout. Aliases include `Command Timeout`. |
| `Pooling` | Parsed and preserved for compatibility. |
| `Vfs` | Parsed and preserved for compatibility. |
| `Encryption Cipher` | Turso local encryption cipher. |
| `Encryption Key` | Hex-encoded encryption key used with `Encryption Cipher`. |
| `Auth Token` | Bearer token for remote Turso/libSQL URLs. Aliases include `AuthToken` and `Authentication Token`. |
| `Replica Path` | Local path for an embedded replica. Requires the `Turso.Data.Sqlite.Sync` companion package and a remote Turso URL. |
| `Read Your Writes` | Keeps the remote Hrana session baton across commands. Defaults to `True`. Set `False` for stateless one-shot remote requests. |
| `Sync Interval` | Automatic replica synchronization is not supported. Call `TursoConnection.Sync()` or `SyncAsync(CancellationToken)` explicitly. |
| `Tls` | Optional override for `libsql://` development URLs. Conflicting values with explicit `http://` or `https://` schemes fail early. |

## SQLite-compatible facade coverage

- `Turso.Data.Sqlite` is the migration-oriented facade. It includes SQLite-style connection strings, commands, readers, schema metadata, transactions and savepoints, backup, SQL-backed blob streams, scalar and aggregate UDFs, custom collations, and disabled-by-default extension loading.
- Raw SQLitePCL `sqlite3*` handle interop is intentionally unsupported. `SqliteConnection.Handle` returns `null` rather than exposing a fake SQLite handle.
- `PRAGMA read_uncommitted` is tracked as connection-local state for API compatibility, but Turso does not currently implement SQLite shared-cache dirty reads.
- `SqliteBlob` preserves fixed-length blob stream behavior through the managed incremental-blob storage adapter. It is not backed by a native SQLitePCL blob handle.
- SQLite virtual-table modules such as FTS3/FTS5 are not built in unless provided by a Turso extension/module.
- The managed engine does not implement experimental MVCC or vector-search functions. `PRAGMA journal_mode = mvcc` and functions such as `vector32()` fail rather than enabling partial behavior.
- Local managed `OpenAsync` and command/reader async methods run blocking work
  off the caller thread and cooperatively observe cancellation tokens and
  `DbCommand.Cancel()` during execution. `CommandTimeout` applies to busy reader
  waits; it is not a general query-execution deadline.

## Entity Framework Core

`Turso.EntityFrameworkCore.Sqlite` adds a `UseTurso` provider hook for local and embedded Turso databases. It reuses EF Core SQLite's LINQ translation pipeline and executes generated SQL through `Turso.Data.Sqlite`.

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

The local provider supports the normal EF Core SQLite query pipeline, including composed `IQueryable<T>` filters, navigation-property joins, ordering, paging, grouping, aggregates, async materialization, and `SaveChangesAsync`. Schema creation can use the standard EF Core SQLite mechanisms such as `EnsureCreated`, `EnsureCreatedAsync`, and migrations against local database files.

Remote `libsql://`/auth-token EF Core support is not part of the local provider. Use the local/embedded provider for EF Core today; remote/serverless EF support needs a separate connection, retry, and transaction design.
