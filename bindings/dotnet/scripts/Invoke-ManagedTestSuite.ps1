<#
.SYNOPSIS
    Runs the managed Turso test suite and proves that it actually executed tests.

.DESCRIPTION
    `dotnet test` exits 0 both when a suite passes and when a suite silently
    discovers nothing, so a broken build graph, a stale filter, or a missing
    runtime pack can produce a green job that ran zero tests. This wrapper reads
    the TRX result file back and fails when the run did not execute at least the
    expected number of tests, or when a class that must really run on this
    platform was never discovered or was skipped away entirely.
#>
[CmdletBinding()]
param(
    [string]$Project = './src/Turso.Tests/Turso.Tests.csproj',
    [string]$Framework,
    [string]$Filter,
    [string]$Configuration = 'Debug',
    [string]$ResultsDirectory = './artifacts/test-results',
    [int]$MinimumExecutedTests = 1,
    # Classes that must contribute at least one passing (non-skipped) result.
    [string[]]$RequirePassingClass = @(),
    # Classes that must be discovered and reported, even if the platform guards
    # every case away. This keeps a platform gap visible instead of silent.
    [string[]]$RequireDiscoveredClass = @(),
    [switch]$NoBuild,
    # Reproduces the managed lane's "must not shell out to Rust" invariant by
    # putting failing cargo/rustc shims ahead of the real toolchain on PATH.
    [switch]$DenyNativeToolchain
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Fail([string]$Message) {
    throw "Managed test suite validation failed: $Message"
}

function New-ToolchainDenyDirectory {
    $directory = Join-Path ([System.IO.Path]::GetTempPath()) "turso-managed-deny-$([System.Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($tool in @('cargo', 'rustc')) {
        $message = "managed .NET validation must not invoke $tool"
        if ($IsWindows) {
            Set-Content -LiteralPath (Join-Path $directory "$tool.cmd") -Encoding ascii -Value @(
                '@echo off'
                "echo $message 1>&2"
                'exit /b 97'
            )
        }
        else {
            $shim = Join-Path $directory $tool
            Set-Content -LiteralPath $shim -Encoding ascii -Value @(
                '#!/usr/bin/env sh'
                "echo '$message' >&2"
                'exit 97'
            )
            & chmod +x $shim
            if ($LASTEXITCODE -ne 0) {
                Fail "could not mark '$shim' executable."
            }
        }
    }

    return $directory
}

function Get-TrxSummary([string]$TrxPath) {
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw

    $classByTestId = @{}
    foreach ($definition in $trx.SelectNodes("//*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")) {
        $method = $definition.SelectSingleNode("*[local-name()='TestMethod']")
        if ($null -ne $method) {
            $classByTestId[$definition.GetAttribute('id')] = $method.GetAttribute('className')
        }
    }

    $passed = 0
    $failed = 0
    $skipped = 0
    $other = 0
    $failedNames = [System.Collections.Generic.List[string]]::new()
    $passedByClass = @{}
    $resultsByClass = @{}

    foreach ($result in $trx.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
        $testId = $result.GetAttribute('testId')
        $className = if ($classByTestId.ContainsKey($testId)) { $classByTestId[$testId] } else { '<unknown>' }
        $resultsByClass[$className] = 1 + $(if ($resultsByClass.ContainsKey($className)) { $resultsByClass[$className] } else { 0 })

        switch ($result.GetAttribute('outcome')) {
            'Passed' {
                $passed++
                $passedByClass[$className] = 1 + $(if ($passedByClass.ContainsKey($className)) { $passedByClass[$className] } else { 0 })
            }
            'Failed' {
                $failed++
                $failedNames.Add($result.GetAttribute('testName'))
            }
            { $_ -in @('NotExecuted', 'Inconclusive', 'Warning') } { $skipped++ }
            default { $other++ }
        }
    }

    return [pscustomobject]@{
        Passed        = $passed
        Failed        = $failed
        Skipped       = $skipped
        Other         = $other
        Total         = $passed + $failed + $skipped + $other
        FailedNames   = $failedNames
        PassedByClass = $passedByClass
        ResultsByClass = $resultsByClass
    }
}

function Test-ClassMatch([hashtable]$Counts, [string]$ClassName) {
    foreach ($key in $Counts.Keys) {
        if ($key -eq $ClassName -or $key.EndsWith(".$ClassName", [System.StringComparison]::Ordinal)) {
            return $true
        }
    }

    return $false
}

$legName = if ($Framework) { $Framework } else { 'all-frameworks' }
$resultsRoot = Join-Path $ResultsDirectory $legName
if (Test-Path -LiteralPath $resultsRoot) {
    Remove-Item -LiteralPath $resultsRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null

$trxFileName = 'managed-test-suite.trx'
$arguments = @(
    'test'
    $Project
    '--configuration', $Configuration
    '--results-directory', $resultsRoot
    '--logger', "trx;LogFileName=$trxFileName"
)
if ($Framework) { $arguments += @('--framework', $Framework) }
if ($Filter) { $arguments += @('--filter', $Filter) }
if ($NoBuild) { $arguments += '--no-build' }

$denyDirectory = $null
$originalPath = $env:PATH
$originalCargo = $env:CARGO
$originalRustc = $env:RUSTC
try {
    if ($DenyNativeToolchain) {
        $denyDirectory = New-ToolchainDenyDirectory
        $env:PATH = "$denyDirectory$([System.IO.Path]::PathSeparator)$originalPath"
        $env:CARGO = Join-Path $denyDirectory $(if ($IsWindows) { 'cargo.cmd' } else { 'cargo' })
        $env:RUSTC = Join-Path $denyDirectory $(if ($IsWindows) { 'rustc.cmd' } else { 'rustc' })
    }

    Write-Host "dotnet $($arguments -join ' ')"
    & dotnet @arguments
    $testExitCode = $LASTEXITCODE
}
finally {
    $env:PATH = $originalPath
    $env:CARGO = $originalCargo
    $env:RUSTC = $originalRustc
    if ($null -ne $denyDirectory -and (Test-Path -LiteralPath $denyDirectory)) {
        Remove-Item -LiteralPath $denyDirectory -Recurse -Force
    }
}

$trxPath = Join-Path $resultsRoot $trxFileName
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    Fail "the run produced no TRX report at '$trxPath', so it cannot be proven to have executed any test (dotnet test exit code $testExitCode)."
}

$summary = Get-TrxSummary -TrxPath $trxPath
$executed = $summary.Passed + $summary.Failed
$headline = "$legName on $([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier): executed $executed (passed $($summary.Passed), failed $($summary.Failed)), skipped $($summary.Skipped), discovered $($summary.Total)"
Write-Host $headline

if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "- $headline"
}

if ($summary.Failed -gt 0) {
    Fail "$($summary.Failed) test(s) failed: $($summary.FailedNames -join ', ')."
}

if ($executed -lt $MinimumExecutedTests) {
    Fail "only $executed test(s) executed but at least $MinimumExecutedTests were expected; a run this small means the suite was not really exercised."
}

foreach ($className in $RequirePassingClass) {
    if (-not (Test-ClassMatch -Counts $summary.PassedByClass -ClassName $className)) {
        Fail "'$className' contributed no passing result on this platform, so its coverage was skipped away or never discovered."
    }
}

foreach ($className in $RequireDiscoveredClass) {
    if (-not (Test-ClassMatch -Counts $summary.ResultsByClass -ClassName $className)) {
        Fail "'$className' was never discovered, so its platform gap is no longer being reported."
    }
}

if ($testExitCode -ne 0) {
    Fail "dotnet test exited with code $testExitCode."
}
