#Requires -Version 7.0

<#
.SYNOPSIS
    Loads the managed Turso assemblies required by the PSSqlite.Managed module.

.DESCRIPTION
    Loads Turso.Core.dll, Turso.Data.dll, and Turso.Data.Sqlite.dll directly via
    Assembly.LoadFrom from the module's vendored lib/net8.0 folder. Load order
    matters because Turso.Data.Sqlite depends on Turso.Data, which in turn
    depends on Turso.Core.

    There is no native e_sqlite3/SQLitePCLRaw binary involved: this module only
    ever talks to the fully managed Turso.Data.Sqlite provider (Local
    Provider=Managed), so there is no native library path/RID resolution to
    worry about, and no net48 fallback branch.
#>

$libDir = Join-Path -Path $PSScriptRoot -ChildPath '..\lib\net8.0'
$libDir = [System.IO.Path]::GetFullPath($libDir)

# Order matters: Turso.Core -> Turso.Data -> Turso.Data.Sqlite.
$assemblyNames = @('Turso.Core.dll', 'Turso.Data.dll', 'Turso.Data.Sqlite.dll')

foreach ($assemblyName in $assemblyNames) {
    $assemblyPath = Join-Path -Path $libDir -ChildPath $assemblyName
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw "PSSqlite.Managed: required assembly '$assemblyName' was not found at '$assemblyPath'. " +
            "Run build.ps1 from the sample root to vendor the managed Turso assemblies before importing this module."
    }

    [System.Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
}
