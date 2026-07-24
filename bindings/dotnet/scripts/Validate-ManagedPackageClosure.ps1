[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$ProjectAssetsFile,
    [string]$PublishOutput,
    [switch]$NativeAot
)

$ErrorActionPreference = 'Stop'

$nativePackagePattern = '(?i)Turso\.(Raw|Data\.(Native|Sync)|Data\.Sqlite\.(Native[^"]*|Sync))'
$nativeConfigurationPattern = "(?i)$nativePackagePattern|cargo|rustc|cargo-ndk|turso_sdk_kit|DirectPInvoke|NativeLibrary|DllImport|LibraryImport|TursoUseStaticNativeLibrary"
$nativeArchiveEntryPattern = '(?i)(^|[\\/])(runtimes|native)[\\/]|(^|[\\/])(Turso\.Raw|Turso\.Data\.Native|Turso\.Data\.Sync)\.dll$|(^|[\\/])(lib)?turso(_sync)?_sdk_kit(\.dll|\.so|\.dylib|\.a|\.lib)?$'

function Fail([string]$Message) {
    throw "Managed release closure validation failed: $Message"
}

function Test-PackageDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Fail "package directory '$Path' does not exist."
    }

    $packages = @(Get-ChildItem -LiteralPath $Path -Filter '*.nupkg' -File)
    if ($packages.Count -eq 0) {
        Fail "package directory '$Path' contains no .nupkg files."
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($package in $packages) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            foreach ($entry in $archive.Entries) {
                if ($entry.FullName -match $nativeArchiveEntryPattern) {
                    Fail "package '$($package.Name)' contains native entry '$($entry.FullName)'."
                }

                if ($entry.FullName -notmatch '(?i)\.(nuspec|props|targets)$') {
                    continue
                }

                $reader = [System.IO.StreamReader]::new($entry.Open())
                try {
                    $content = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }

                if ($content -match $nativeConfigurationPattern) {
                    Fail "package '$($package.Name)' configuration entry '$($entry.FullName)' contains a native, P/Invoke, or Rust edge."
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
}

function Test-ProjectAssets([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "assets file '$Path' does not exist."
    }

    $assets = Get-Content -LiteralPath $Path -Raw
    if ($assets -notmatch '(?i)"Turso\.Data\.Sqlite/') {
        Fail "assets file '$Path' does not restore Turso.Data.Sqlite."
    }

    if ($assets -match "(?i)`"$nativePackagePattern/") {
        Fail "assets file '$Path' restores a native Turso companion package."
    }
}

function Test-PublishOutput([string]$Path, [bool]$IsNativeAot) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Fail "publish output '$Path' does not exist."
    }

    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse)
    if ($files.Count -eq 0) {
        Fail "publish output '$Path' is empty."
    }

    foreach ($file in $files) {
        if ($file.Name -match '(?i)^(Turso\.Raw|Turso\.Data\.Native|Turso\.Data\.Sync)\.dll$|^(lib)?turso(_sync)?_sdk_kit(\.dll|\.so|\.dylib|\.a|\.lib)?$') {
            Fail "publish output '$Path' contains native companion asset '$($file.Name)'."
        }
    }

    if (-not $IsNativeAot) {
        return
    }

    $executables = @($files | Where-Object { $_.Extension -eq '' -or $_.Extension -eq '.exe' })
    if ($executables.Count -ne 1) {
        Fail "NativeAOT publish output '$Path' must contain exactly one executable."
    }

    $unexpected = @($files | Where-Object {
            $_.Extension -ne '' -and $_.Extension -notin '.exe', '.pdb', '.dbg', '.xml'
        })
    if ($unexpected.Count -ne 0) {
        Fail "NativeAOT publish output '$Path' contains unexpected file '$($unexpected[0].Name)'."
    }
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory) -and
    [string]::IsNullOrWhiteSpace($ProjectAssetsFile) -and
    [string]::IsNullOrWhiteSpace($PublishOutput)) {
    Fail 'supply PackageDirectory, ProjectAssetsFile, or PublishOutput.'
}

if (-not [string]::IsNullOrWhiteSpace($PackageDirectory)) {
    Test-PackageDirectory $PackageDirectory
}

if (-not [string]::IsNullOrWhiteSpace($ProjectAssetsFile)) {
    Test-ProjectAssets $ProjectAssetsFile
}

if (-not [string]::IsNullOrWhiteSpace($PublishOutput)) {
    Test-PublishOutput $PublishOutput $NativeAot
}
