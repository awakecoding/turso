# Ahtola .NET

Pure managed (C#) SQLite-compatible engine and ADO.NET / EF Core provider, by
[Devolutions](https://devolutions.net).

Ahtola is a from-scratch C# engine that reads and writes SQLite’s on-disk format.
It is **not** a binding over native SQLite or over any Rust core. No native
companion, P/Invoke SDK, or Rust toolchain is required to restore, build, pack,
or run.

## Install

```bash
dotnet add package Devolutions.Ahtola.Data.Sqlite
# optional EF Core 9.x provider:
dotnet add package Devolutions.Ahtola.EntityFrameworkCore.Sqlite
```

Targets: `net8.0`, `net9.0`, `net10.0`. No `net48` / .NET Framework assets.

| Package | Role |
| --- | --- |
| `Devolutions.Ahtola.Core` | Managed engine |
| `Devolutions.Ahtola.Data.Sqlite` | ADO.NET provider + `Microsoft.Data.Sqlite`-compatible facade; embeds `Ahtola.Data` |
| `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` | EF Core provider (`UseAhtola`) |

| Layer | Name |
| --- | --- |
| NuGet PackageId | `Devolutions.Ahtola.*` |
| Namespaces / assemblies / types | `Ahtola.*` (`AhtolaConnection`, `UseAhtola`, …) |
| Project folders | `src/Ahtola.*` |

## Quick start

**SQLite-compatible facade** (drop-in `using` swap from Microsoft.Data.Sqlite):

```csharp
using Ahtola.Data.Sqlite;

using var connection = new SqliteConnection("Data Source=app.db");
connection.Open();
connection.ExecuteNonQuery("CREATE TABLE t(a INTEGER, b TEXT)");
connection.ExecuteNonQuery("INSERT INTO t VALUES (1, 'hello')");

using var command = connection.CreateCommand();
command.CommandText = "SELECT a, b FROM t";
using var reader = command.ExecuteReader();
while (reader.Read())
    Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
```

**Ahtola types** (same package):

```csharp
using Ahtola;

using var connection = new AhtolaConnection("Data Source=:memory:");
connection.Open();
connection.ExecuteNonQuery("CREATE TABLE t(a, b)");
// AhtolaConnection, AhtolaCommand, AhtolaParameter, AhtolaFactory.Instance, …
```

**EF Core:**

```csharp
options.UseAhtola("Data Source=app.db");
```

Common connection-string keywords: `Data Source`, `Mode`, `Cache`, `Pooling`,
`Foreign Keys`, `Default Timeout` / `Command Timeout`, `Foreign Read Only`,
`Encryption Cipher` + `Encryption Key` (AES-128/256-GCM). Default local provider
is managed-only.

## What this is good for

- Fully managed local SQLite-format databases with **no native assets**
- Small-to-moderate workloads, in-process embedding, constrained deployment
- A familiar ADO.NET / MDS-shaped API and an EF Core provider

## Important limits

Treat Ahtola as SQLite-*compatible*, not a full SQLite replacement:

- **In-memory working set** — tables and intermediate results stay in the process
  heap; nothing spills to disk. Prefer modest databases and explicit transactions
  for writes (managed writes are slower than native SQLite and the gap grows with
  table size).
- **Planner** — no cost-based optimizer, join reordering, or `ANALYZE` stats.
  First usable index by name wins. Prefer `ORDER BY` when order matters
  (`GROUP BY` is first-encounter order).
- **File-backed platforms** — Windows and 64-bit Linux only today. In-memory
  works everywhere; macOS / 32-bit Linux physical opens throw
  `PlatformNotSupportedException`.
- **Process-exclusive files (Stage 0)** — one managed process owns a physical DB;
  Turso, ordinary SQLite, and other processes get busy/ownership failures.
  Concurrent multi-process WAL with the Turso Rust engine is the contract goal,
  not current behavior. Handoff requires disposing connections and clearing pools
  (`Pooling=False` or `SqliteConnection.ClearAllPools()`). See
  [docs/wal-interoperability-contract.md](docs/wal-interoperability-contract.md).
- **Foreign read-only** — `Mode=ReadOnly;Foreign Read Only=True;Pooling=False`
  can read a DB still owned by native SQLite/Turso (e.g. winget `index.db`) without
  taking ownership.
- **Not implemented** — virtual tables / FTS / R-Tree, loadable extensions, raw
  `sqlite3*` handles (`Handle` is null), MVCC, `BEGIN CONCURRENT`, `ANALYZE`,
  AEGIS encryption ciphers.
- **Native / Sync companions** — not shipped. Connection-string paths that need
  them fail closed. OS P/Invoke in the pager for locks/WAL is intentional engine
  code, not a Rust SDK binding.
- **Remote Hrana** — optional pure-managed HTTP `/v2/pipeline` on
  `AhtolaConnection` (tests use a canned server). Not a cloud product surface.

Encryption format v0 uses a fixed 5-byte magic `AHTLA`, then version and cipher
id (AES-GCM page AEAD).

## Build and test

PowerShell entrypoint (preferred):

```powershell
./build.ps1 restore
./build.ps1 build
./build.ps1 test              # packaged consumer gate + managed suite
./build.ps1 pack
./build.ps1 validate-package
./build.ps1 format-check
```

Optional parameters: `-Configuration Debug|Release`, `-Framework net10.0`,
`-PackageVersion …`, `-PackageOutput ./artifacts/managed-packages`,
`-MinimumExecutedTests 2500`.

Or with the .NET SDK only:

```bash
dotnet build Ahtola.slnx -c Release
dotnet test src/Ahtola.Tests/Ahtola.Tests.csproj -c Release -f net10.0
pwsh ./scripts/Validate-ManagedPackageClosure.ps1
```

`scripts/Invoke-ManagedTestSuite.ps1` runs tests and fails the job if too few
tests executed (guards against silent empty runs). Conformance gaps for the
embedded `sqlite-sqltests` corpus live in
`src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt`.

## Layout

```text
ahtola/
├── src/Ahtola.Core/                      # engine
├── src/Ahtola.Data/                      # embedded ADO core (not a separate nupkg)
├── src/Ahtola.Data.Sqlite/               # provider + MDS facade
├── src/Ahtola.EntityFrameworkCore.Sqlite/
├── src/Ahtola.Tests/
├── samples/                              # ManagedPackageConsumer, PSSqlite.Managed
├── docs/                                 # WAL interop contract (Turso target)
├── scripts/                              # test + package closure validators
├── build.ps1                             # restore / build / test / pack
├── NuGet.config
├── LICENSE
└── Ahtola.slnx
```

## License

MIT — see [LICENSE](LICENSE).
