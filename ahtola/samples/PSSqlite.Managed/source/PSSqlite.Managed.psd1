@{
    RootModule        = 'PSSqlite.Managed.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = 'b3d2f8e0-6a4a-4f7c-9a3b-2a7e6a1d9c10'
    Author            = 'Turso'
    CompanyName       = 'Turso'
    Copyright         = '(c) Turso. All rights reserved.'
    Description       = 'Minimal PowerShell 7 module sample wiring PowerShell to the fully managed Turso.Data.Sqlite provider (no native e_sqlite3/SQLitePCLRaw binaries).'
    PowerShellVersion = '7.0'
    CompatiblePSEditions = @('Core')

    # Loads the vendored Turso.Core / Turso.Data / Turso.Data.Sqlite assemblies
    # (in that order) via Assembly.LoadFrom before the root module is imported.
    ScriptsToProcess  = @('ScriptsToProcess\PreLoadTypes.ps1')

    FunctionsToExport = @('New-ManagedConnection', 'Invoke-ManagedQuery', 'Start-ManagedSample')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags       = @('Turso', 'Sqlite', 'Managed')
            ProjectUri = 'https://github.com/tursodatabase/turso'
        }
    }
}
