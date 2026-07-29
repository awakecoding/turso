# PSSqlite.Managed

A minimal PowerShell 7 module sample that wires PowerShell directly to the
fully **managed** `Turso.Data.Sqlite` provider — there are **no native
`e_sqlite3`/SQLitePCLRaw binaries** involved anywhere in this sample. Every
connection opts into `Local Provider=Managed`, so all reads/writes go through
Turso's managed storage engine.

This is a self-contained sibling to [`ManagedPackageConsumer`](../ManagedPackageConsumer),
demonstrating a vendoring pattern for PowerShell module authors: build once
with the .NET SDK to fetch and copy the managed Turso assemblies into the
module folder, then import the module anywhere pwsh 7 runs (Windows or Linux)
without needing the .NET SDK or any native SQLite binary at import time.

## What's here

- `nuget.config` — adds a local NuGet feed pointing at
  `bindings/dotnet/artifacts/nupkg` (where the pre-release
  `Turso.Data.Sqlite.0.8.0-pre.2` package lives), alongside nuget.org.
- `PSSqlite.Managed.csproj` — a `net8.0` helper project (the lowest TFM the
  package ships; `net7.0`/`netstandard2.0` fail restore with NU1202) that
  references `Turso.Data.Sqlite` and, after building, copies
  `Turso.Core.dll`, `Turso.Data.dll`, and `Turso.Data.Sqlite.dll` into
  `source/lib/net8.0/`.
- `source/PSSqlite.Managed.psd1` — the module manifest (PowerShell 7+,
  `RootModule = 'PSSqlite.Managed.psm1'`,
  `ScriptsToProcess = 'ScriptsToProcess\PreLoadTypes.ps1'`).
- `source/PSSqlite.Managed.psm1` — the root module, exporting:
  - `New-ManagedConnection` — opens a
    `Data Source=:memory:;Cache=Shared;Local Provider=Managed` connection.
  - `Invoke-ManagedQuery` — runs a command against an open connection and
    returns rows as `PSCustomObject`s.
  - `Start-ManagedSample` — end-to-end demo: creates a metadata table,
    inserts a row, reads it back, prints it, then calls
    `[Turso.Data.Sqlite.SqliteConnection]::ClearAllPools()` on close.
- `source/ScriptsToProcess/PreLoadTypes.ps1` — loads the three vendored
  assemblies via `[System.Reflection.Assembly]::LoadFrom()` from
  `source/lib/net8.0`, in dependency order: `Turso.Core` → `Turso.Data` →
  `Turso.Data.Sqlite`. No native library, no PATH/RID resolution, no net48
  branch. Throws a clear error if any DLL is missing (run `build.ps1` first).
- `build.ps1` — runs `dotnet build`, which triggers the restore + vendor
  copy, and prints where the DLLs landed.

## Build

```powershell
./build.ps1
```

This restores `Turso.Data.Sqlite` from the local feed and vendors
`Turso.Core.dll`, `Turso.Data.dll`, and `Turso.Data.Sqlite.dll` into
`source/lib/net8.0/`.

## Import and run the demo

```powershell
Import-Module ./source/PSSqlite.Managed.psd1
Start-ManagedSample
```

## Notes

- **No native SQLite binaries.** This sample never restores or loads
  `e_sqlite3`, `SQLitePCLRaw`, or `Microsoft.Data.Sqlite` — only the three
  managed Turso assemblies.
- **PowerShell 7+ only** (`#Requires -Version 7.0`, `CompatiblePSEditions =
  'Core'`).
- **net8.0** is the target framework for the vendoring helper project — it's
  the lowest TFM the `Turso.Data.Sqlite` package ships (`net8.0`, `net9.0`,
  `net10.0`).
- Loads on Windows and Linux amd64 pwsh 7 without any native SQLite binary or
  platform-specific RID resolution, because `Assembly.LoadFrom` is used
  directly instead of relying on the .NET native asset resolver.
