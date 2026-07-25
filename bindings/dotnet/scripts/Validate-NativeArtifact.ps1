param(
    [Parameter(Mandatory)]
    [ValidateSet(
        "x86_64-pc-windows-msvc",
        "aarch64-pc-windows-msvc",
        "x86_64-unknown-linux-gnu",
        "aarch64-unknown-linux-gnu",
        "aarch64-linux-android",
        "armv7-linux-androideabi",
        "x86_64-linux-android",
        "i686-linux-android",
        "x86_64-apple-darwin",
        "aarch64-apple-darwin",
        "aarch64-apple-ios",
        "aarch64-apple-ios-sim",
        "x86_64-apple-ios",
        "ios-simulator-universal")]
    [string] $Target,

    [Parameter(Mandatory)]
    [string[]] $DynamicArtifacts,

    [string[]] $StaticArtifacts = @(),

    [switch] $RequireWindowsSignature
)

$ErrorActionPreference = "Stop"

$profiles = @{
    "x86_64-pc-windows-msvc" = @{ Format = "PE"; Architecture = 0x8664 }
    "aarch64-pc-windows-msvc" = @{ Format = "PE"; Architecture = 0xAA64 }
    "x86_64-unknown-linux-gnu" = @{ Format = "ELF"; Architecture = "Advanced Micro Devices X86-64"; Platform = "linux" }
    "aarch64-unknown-linux-gnu" = @{ Format = "ELF"; Architecture = "AArch64"; Platform = "linux" }
    "aarch64-linux-android" = @{ Format = "ELF"; Architecture = "AArch64"; Platform = "android" }
    "armv7-linux-androideabi" = @{ Format = "ELF"; Architecture = "ARM"; Platform = "android" }
    "x86_64-linux-android" = @{ Format = "ELF"; Architecture = "Advanced Micro Devices X86-64"; Platform = "android" }
    "i686-linux-android" = @{ Format = "ELF"; Architecture = "Intel 80386"; Platform = "android" }
    "x86_64-apple-darwin" = @{ Format = "MachO"; Architectures = @("x86_64"); Platform = "MACOS" }
    "aarch64-apple-darwin" = @{ Format = "MachO"; Architectures = @("arm64"); Platform = "MACOS" }
    "aarch64-apple-ios" = @{ Format = "MachO"; Architectures = @("arm64"); Platform = "IOS" }
    "aarch64-apple-ios-sim" = @{ Format = "MachO"; Architectures = @("arm64"); Platform = "IOSSIMULATOR" }
    "x86_64-apple-ios" = @{ Format = "MachO"; Architectures = @("x86_64"); Platform = "IOSSIMULATOR" }
    "ios-simulator-universal" = @{ Format = "MachO"; Architectures = @("arm64", "x86_64"); Platform = "IOSSIMULATOR" }
}

$windowsSystemLibraries = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @(
        "ADVAPI32.dll",
        "BCRYPT.dll",
        "CRYPT32.dll",
        "DBGHELP.dll",
        "KERNEL32.dll",
        "NTDLL.dll",
        "OLE32.dll",
        "SHELL32.dll",
        "USER32.dll",
        "USERENV.dll",
        "VCRUNTIME140.dll",
        "VCRUNTIME140_1.dll",
        "WS2_32.dll",
        "bcryptprimitives.dll",
        "ucrtbase.dll"
    ),
    [StringComparer]::OrdinalIgnoreCase)

$linuxSystemLibraries = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @(
        "libc.so.6",
        "libdl.so.2",
        "libgcc_s.so.1",
        "libm.so.6",
        "libpthread.so.0",
        "libresolv.so.2",
        "librt.so.1",
        "libstdc++.so.6",
        "libutil.so.1",
        "libz.so.1"
    ),
    [StringComparer]::Ordinal)

$androidSystemLibraries = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @(
        "libandroid.so",
        "libc.so",
        "libdl.so",
        "liblog.so",
        "libm.so",
        "libunwind.so",
        "libz.so"
    ),
    [StringComparer]::Ordinal)

function Invoke-Tool {
    param(
        [Parameter(Mandatory)]
        [string] $Command,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $output = & $Command @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed:`n$($output -join [Environment]::NewLine)"
    }

    return $output
}

function Resolve-Dumpbin {
    param(
        [Parameter(Mandatory)]
        [int] $ExpectedMachine
    )

    $command = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "Could not locate dumpbin.exe or vswhere.exe."
    }

    $installationPath = (& $vswhere -latest -products * -property installationPath | Select-Object -First 1)
    if (-not $installationPath) {
        throw "vswhere.exe did not find a Visual Studio installation."
    }

    $targetDirectory = if ($ExpectedMachine -eq 0x8664) { "x64" } else { "arm64" }
    $candidates = @(Get-ChildItem -Path (Join-Path $installationPath "VC\Tools\MSVC") -Recurse -Filter dumpbin.exe |
        Where-Object { $_.Directory.Name -eq $targetDirectory } |
        Sort-Object FullName -Descending)
    if (-not $candidates) {
        throw "Could not locate a $targetDirectory dumpbin.exe under $installationPath."
    }

    return $candidates[0].FullName
}

function Assert-PeArchitecture {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [int] $ExpectedMachine
    )

    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "$Path is not a PE image."
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "$Path has an invalid PE signature."
        }

        $machine = $reader.ReadUInt16()
        if ($machine -ne $ExpectedMachine) {
            throw "$Path has PE machine 0x$($machine.ToString('X4')); expected 0x$($ExpectedMachine.ToString('X4'))."
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-WindowsDynamicArtifact {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [int] $ExpectedMachine
    )

    Assert-PeArchitecture -Path $Path -ExpectedMachine $ExpectedMachine

    $dumpbin = Resolve-Dumpbin -ExpectedMachine $ExpectedMachine
    $output = Invoke-Tool -Command $dumpbin -Arguments @("/nologo", "/dependents", $Path)
    $dependencies = $output |
        ForEach-Object {
            if ($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$') {
                $Matches[1]
            }
        } |
        Sort-Object -Unique
    if (-not $dependencies) {
        throw "$Path did not report any imported Windows libraries."
    }

    foreach ($dependency in $dependencies) {
        if ($dependency -match '^(api-ms-win-|ext-ms-win-).+\.dll$') {
            continue
        }
        if (
            -not $windowsSystemLibraries.Contains($dependency) -and
            -not (Test-Path (Join-Path ([Environment]::SystemDirectory) $dependency))
        ) {
            throw "$Path imports non-system Windows library '$dependency'."
        }
    }

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($RequireWindowsSignature -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "$Path must have a valid Authenticode signature for native package publication; status is $($signature.Status)."
    }
    if (
        -not $RequireWindowsSignature -and
        $signature.Status -notin @(
            [System.Management.Automation.SignatureStatus]::Valid,
            [System.Management.Automation.SignatureStatus]::NotSigned)
    ) {
        throw "$Path has an invalid Authenticode signature; status is $($signature.Status)."
    }
}

function Assert-WindowsStaticArtifact {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [int] $ExpectedMachine
    )

    $machineName = if ($ExpectedMachine -eq 0x8664) { "x64" } else { "ARM64" }
    $dumpbin = Resolve-Dumpbin -ExpectedMachine $ExpectedMachine
    $output = Invoke-Tool -Command $dumpbin -Arguments @("/nologo", "/headers", $Path)
    if (($output -join "`n") -notmatch "(?im)^\s+[0-9A-F]+\s+machine \($machineName\)") {
        throw "$Path does not contain $machineName COFF objects."
    }
}

function Assert-ElfArchitecture {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ExpectedArchitecture
    )

    $header = Invoke-Tool -Command "readelf" -Arguments @("-h", $Path)
    $machines = @($header |
        ForEach-Object {
            if ($_ -match '^\s*Machine:\s+(.+?)\s*$') {
                $Matches[1]
            }
        } |
        Sort-Object -Unique)
    if (-not $machines -or $machines.Count -ne 1 -or $machines[0] -ne $ExpectedArchitecture) {
        throw "$Path has ELF machine '$($machines -join ', ')'; expected '$ExpectedArchitecture'."
    }
}

function Assert-ElfDynamicArtifact {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [hashtable] $Profile
    )

    Assert-ElfArchitecture -Path $Path -ExpectedArchitecture $Profile.Architecture
    $dynamic = Invoke-Tool -Command "readelf" -Arguments @("-d", $Path)
    $dependencies = $dynamic |
        ForEach-Object {
            if ($_ -match '\(NEEDED\).+Shared library: \[(.+?)\]') {
                $Matches[1]
            }
        } |
        Sort-Object -Unique
    if (-not $dependencies) {
        throw "$Path did not report any imported ELF libraries."
    }

    $allowed = if ($Profile.Platform -eq "android") {
        $androidSystemLibraries
    }
    else {
        $linuxSystemLibraries
    }
    foreach ($dependency in $dependencies) {
        if (-not $allowed.Contains($dependency)) {
            throw "$Path imports non-system $($Profile.Platform) library '$dependency'."
        }
    }
}

function Assert-MachOArtifact {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [hashtable] $Profile,

        [switch] $Dynamic
    )

    $architectures = @((Invoke-Tool -Command "lipo" -Arguments @("-archs", $Path)) -split '\s+' |
        Where-Object { $_ } |
        Sort-Object)
    $expectedArchitectures = @([string[]] $Profile.Architectures | Sort-Object)
    if (Compare-Object $expectedArchitectures $architectures) {
        throw "$Path has Mach-O architectures '$($architectures -join ', ')'; expected '$($expectedArchitectures -join ', ')'."
    }

    $loadCommands = Invoke-Tool -Command "otool" -Arguments @("-l", $Path)
    if (($loadCommands -join "`n") -notmatch "(?m)^\s*platform\s+$($Profile.Platform)\s*$") {
        throw "$Path does not declare Mach-O platform $($Profile.Platform)."
    }

    if ($Dynamic) {
        $dependencies = Invoke-Tool -Command "otool" -Arguments @("-L", $Path)
        foreach ($line in $dependencies | Select-Object -Skip 1) {
            $dependency = ($line.Trim() -split '\s+\(')[0]
            if (
                $dependency -and
                -not $dependency.StartsWith("@rpath/", [StringComparison]::Ordinal) -and
                -not $dependency.StartsWith("/usr/lib/", [StringComparison]::Ordinal) -and
                -not $dependency.StartsWith("/System/Library/", [StringComparison]::Ordinal)
            ) {
                throw "$Path imports non-system Apple library '$dependency'."
            }
        }

        $signatureOutput = & codesign --verify --strict $Path 2>&1
        if ($LASTEXITCODE -ne 0 -and ($signatureOutput -join "`n") -notmatch 'code object is not signed at all') {
            throw "$Path has an invalid Apple code signature:`n$($signatureOutput -join [Environment]::NewLine)"
        }
    }
}

$profile = $profiles[$Target]
foreach ($artifact in $DynamicArtifacts) {
    $path = (Resolve-Path $artifact).Path
    switch ($profile.Format) {
        "PE" {
            Assert-WindowsDynamicArtifact -Path $path -ExpectedMachine $profile.Architecture
        }
        "ELF" {
            Assert-ElfDynamicArtifact -Path $path -Profile $profile
        }
        "MachO" {
            Assert-MachOArtifact -Path $path -Profile $profile -Dynamic
        }
    }
}

foreach ($artifact in $StaticArtifacts) {
    $path = (Resolve-Path $artifact).Path
    switch ($profile.Format) {
        "PE" {
            Assert-WindowsStaticArtifact -Path $path -ExpectedMachine $profile.Architecture
        }
        "ELF" {
            Assert-ElfArchitecture -Path $path -ExpectedArchitecture $profile.Architecture
        }
        "MachO" {
            Assert-MachOArtifact -Path $path -Profile $profile
        }
    }
}

Write-Host "Validated $($DynamicArtifacts.Count) dynamic and $($StaticArtifacts.Count) static artifact(s) for $Target."
