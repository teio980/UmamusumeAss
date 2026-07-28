<#
.SYNOPSIS
    Builds native and managed artifacts, merges them, and produces a portable
    UmamusumeAss-win-x64.zip suitable for extraction on a clean Windows x64
    machine without a .NET runtime or VC++ redistributable.

.DESCRIPTION
    Phase 6 packaging script.  Accepts an -OutputDirectory parameter so the
    package-layout test can supply a temporary root.  Fails fast on any error.

    Steps:
      1. cmake --preset release          (configure)
      2. cmake --build --preset release   (build native)
      3. cmake --install ...              (stage native artifacts)
      4. dotnet publish --self-contained  (publish managed app)
      5. Merge native install artifacts into publish output
      6. Create UmamusumeAss-win-x64.zip
      7. Verify no VC++ dynamic runtime DLLs in archive
#>

[CmdletBinding()]
param(
    # Directory where the final ZIP is written.  Created if absent.
    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = (Join-Path -Path $PSScriptRoot -ChildPath "..\dist")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Resolve paths ──────────────────────────────────────────────────────────
$SolutionRoot = Resolve-Path -LiteralPath (Join-Path -Path $PSScriptRoot -ChildPath "..")
$BuildDir      = Join-Path -Path $SolutionRoot -ChildPath "build\release"
$NativeStaging = Join-Path -Path $SolutionRoot -ChildPath "build\native-staging"
$PublishDir    = Join-Path -Path $SolutionRoot -ChildPath "build\publish"

# Resolved output directory
$OutputDir = Resolve-Path -LiteralPath $OutputDirectory -ErrorAction SilentlyContinue
if (-not $OutputDir) {
    $OutputDir = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
}

$ZipPath = Join-Path -Path $OutputDir -ChildPath "UmamusumeAss-win-x64.zip"

Write-Host "=== UmamusumeAss Packaging ==="
Write-Host "Solution root:    $SolutionRoot"
Write-Host "Output directory: $OutputDir"
Write-Host ""

# ── Step 1: CMake configure ────────────────────────────────────────────────
Write-Host "--- Step 1/6: CMake configure (release preset) ---"
$proc = Start-Process -FilePath "cmake" -ArgumentList "--preset", "release" `
    -WorkingDirectory $SolutionRoot -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "cmake --preset release failed with exit code $($proc.ExitCode)"
}
Write-Host "OK"
Write-Host ""

# ── Step 2: CMake build ────────────────────────────────────────────────────
Write-Host "--- Step 2/6: CMake build (release preset) ---"
$proc = Start-Process -FilePath "cmake" -ArgumentList "--build", "--preset", "release" `
    -WorkingDirectory $SolutionRoot -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "cmake --build --preset release failed with exit code $($proc.ExitCode)"
}
Write-Host "OK"
Write-Host ""

# ── Step 3: CMake install to native staging ────────────────────────────────
Write-Host "--- Step 3/6: CMake install (native staging) ---"
# Clean staging directory first
if (Test-Path -LiteralPath $NativeStaging) {
    Remove-Item -LiteralPath $NativeStaging -Recurse -Force
}
New-Item -ItemType Directory -Path $NativeStaging -Force | Out-Null

$proc = Start-Process -FilePath "cmake" -ArgumentList @(
    "--install", $BuildDir,
    "--config", "Release",
    "--prefix", $NativeStaging
) -WorkingDirectory $SolutionRoot -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "cmake --install failed with exit code $($proc.ExitCode)"
}
Write-Host "OK"
Write-Host ""

# ── Step 4: dotnet publish (self-contained) ────────────────────────────────
Write-Host "--- Step 4/6: dotnet publish (self-contained win-x64) ---"
# Clean publish directory first
if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}

$project = Join-Path -Path $SolutionRoot -ChildPath "src\UmamusumeWpfGui\UmamusumeWpfGui.csproj"
$proc = Start-Process -FilePath "dotnet" -ArgumentList @(
    "publish", $project,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--output", $PublishDir
) -WorkingDirectory $SolutionRoot -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "dotnet publish failed with exit code $($proc.ExitCode)"
}
Write-Host "OK"
Write-Host ""

# ── Step 5: Merge native install artifacts into publish output ─────────────
Write-Host "--- Step 5/6: Merge native artifacts into publish directory ---"

# 5a. UmamusumeCore.dll — from native staging root to publish root
$nativeCoreDll = Join-Path -Path $NativeStaging -ChildPath "UmamusumeCore.dll"
if (-not (Test-Path -LiteralPath $nativeCoreDll)) {
    throw "Native artifact not found: $nativeCoreDll"
}
Copy-Item -LiteralPath $nativeCoreDll -Destination $PublishDir -Force
Write-Host "  Copied UmamusumeCore.dll"

# 5b. resource/connection.json — from native staging resource/ to publish resource/
$nativeResourceDir = Join-Path -Path $NativeStaging -ChildPath "resource"
$nativeResourceFile = Join-Path -Path $nativeResourceDir -ChildPath "connection.json"
if (-not (Test-Path -LiteralPath $nativeResourceFile)) {
    throw "Native resource not found: $nativeResourceFile"
}
$publishResourceDir = Join-Path -Path $PublishDir -ChildPath "resource"
if (-not (Test-Path -LiteralPath $publishResourceDir)) {
    New-Item -ItemType Directory -Path $publishResourceDir -Force | Out-Null
}
Copy-Item -LiteralPath $nativeResourceFile -Destination $publishResourceDir -Force
Write-Host "  Copied resource/connection.json"

# 5c. Verify Umamusume.CoreBridge.dll exists in publish output
$bridgeDll = Join-Path -Path $PublishDir -ChildPath "Umamusume.CoreBridge.dll"
if (-not (Test-Path -LiteralPath $bridgeDll)) {
    throw "Umamusume.CoreBridge.dll not found in publish output: $bridgeDll"
}
Write-Host "  Verified Umamusume.CoreBridge.dll"

# 5d. Verify self-contained runtime evidence
$hostfxr = Join-Path -Path $PublishDir -ChildPath "hostfxr.dll"
$spcl   = Join-Path -Path $PublishDir -ChildPath "System.Private.CoreLib.dll"
if (-not (Test-Path -LiteralPath $hostfxr) -and -not (Test-Path -LiteralPath $spcl)) {
    throw "Self-contained runtime evidence missing: neither hostfxr.dll nor System.Private.CoreLib.dll found in $PublishDir"
}
Write-Host "  Verified self-contained runtime evidence"

Write-Host "OK"
Write-Host ""

# ── Step 6: Create ZIP ─────────────────────────────────────────────────────
Write-Host "--- Step 6/6: Creating ZIP archive ---"

# Remove existing ZIP if present
if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

# Use .NET System.IO.Compression via PowerShell to create the ZIP with
# flat relative paths (no leading directory prefix).
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

if (-not (Test-Path -LiteralPath $ZipPath)) {
    throw "ZIP creation failed: $ZipPath not found after compression"
}

$zipSize = (Get-Item -LiteralPath $ZipPath).Length
Write-Host "OK — $ZipPath ($zipSize bytes)"
Write-Host ""

# ── Step 7: Verify no VC++ redistributable DLLs in archive ─────────────────
Write-Host "--- Step 7/7: Verifying no VC++ redistributable DLLs in archive ---"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipCheck = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $vcRedistNames = @(
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "vcruntime140d.dll",
        "msvcp140.dll",
        "msvcp140_1.dll",
        "msvcp140_2.dll",
        "msvcp140d.dll",
        "concrt140.dll",
        "concrt140d.dll"
    )
    $vcEntries = $zipCheck.Entries | Where-Object {
        $vcRedistNames -contains $_.Name
    }
    if ($vcEntries) {
        $found = ($vcEntries | ForEach-Object { $_.FullName }) -join ", "
        throw "VC++ redistributable DLLs found in archive — /MT static linking is not effective: $found"
    }
    Write-Host "OK — no VC++ redistributable DLLs detected"
}
finally {
    $zipCheck.Dispose()
}
Write-Host ""

Write-Host "=== Packaging complete ==="