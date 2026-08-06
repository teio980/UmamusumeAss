



















[CmdletBinding()]
param(

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = (Join-Path -Path $PSScriptRoot -ChildPath "..\dist")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"


$SolutionRoot = Resolve-Path -LiteralPath (Join-Path -Path $PSScriptRoot -ChildPath "..")
$BuildDir      = Join-Path -Path $SolutionRoot -ChildPath "build\release"
$NativeStaging = Join-Path -Path $SolutionRoot -ChildPath "build\native-staging"
$PublishDir    = Join-Path -Path $SolutionRoot -ChildPath "build\publish"


$OutputDir = Resolve-Path -LiteralPath $OutputDirectory -ErrorAction SilentlyContinue
if (-not $OutputDir) {
    $OutputDir = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
}

$ZipPath = Join-Path -Path $OutputDir -ChildPath "UmamusumeAss-win-x64.zip"

Write-Host "=== UmamusumeAss Packaging ==="
Write-Host "Solution root:    $SolutionRoot"
Write-Host "Output directory: $OutputDir"
Write-Host ""


Write-Host "--- Step 1/6: CMake configure (Release) ---"
$cmakeConfigureArgs = @(
    "-S", ".",
    "-B", $BuildDir,
    "-G", "Visual Studio 17 2022",
    "-A", "x64",
    "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"
)
& cmake @cmakeConfigureArgs
if ($LASTEXITCODE -ne 0) {
    throw "cmake Release configure failed with exit code $LASTEXITCODE"
}
Write-Host "OK"
Write-Host ""


Write-Host "--- Step 2/6: CMake build (Release) ---"
$cmakeBuildArgs = @(
    "--build", $BuildDir,
    "--config", "Release"
)
& cmake @cmakeBuildArgs
if ($LASTEXITCODE -ne 0) {
    throw "cmake Release build failed with exit code $LASTEXITCODE"
}
Write-Host "OK"
Write-Host ""


Write-Host "--- Step 3/6: CMake install (native staging) ---"

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


Write-Host "--- Step 4/6: dotnet publish (self-contained win-x64) ---"

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


Write-Host "--- Step 5/6: Merge native artifacts into publish directory ---"


$nativeCoreDll = Join-Path -Path $NativeStaging -ChildPath "UmamusumeCore.dll"
if (-not (Test-Path -LiteralPath $nativeCoreDll)) {
    throw "Native artifact not found: $nativeCoreDll"
}
Copy-Item -LiteralPath $nativeCoreDll -Destination $PublishDir -Force
Write-Host "  Copied UmamusumeCore.dll"


$bridgeDll = Join-Path -Path $PublishDir -ChildPath "Umamusume.CoreBridge.dll"
if (-not (Test-Path -LiteralPath $bridgeDll)) {
    throw "Umamusume.CoreBridge.dll not found in publish output: $bridgeDll"
}
Write-Host "  Verified Umamusume.CoreBridge.dll"


$hostfxr = Join-Path -Path $PublishDir -ChildPath "hostfxr.dll"
$spcl   = Join-Path -Path $PublishDir -ChildPath "System.Private.CoreLib.dll"
if (-not (Test-Path -LiteralPath $hostfxr) -and -not (Test-Path -LiteralPath $spcl)) {
    throw "Self-contained runtime evidence missing: neither hostfxr.dll nor System.Private.CoreLib.dll found in $PublishDir"
}
Write-Host "  Verified self-contained runtime evidence"

Write-Host "OK"
Write-Host ""


Write-Host "--- Step 6/6: Creating ZIP archive ---"


if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}



Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

if (-not (Test-Path -LiteralPath $ZipPath)) {
    throw "ZIP creation failed: $ZipPath not found after compression"
}

$zipSize = (Get-Item -LiteralPath $ZipPath).Length
Write-Host "OK - $ZipPath ($zipSize bytes)"
Write-Host ""


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
        throw "VC++ redistributable DLLs found in archive - /MT static linking is not effective: $found"
    }
    Write-Host "OK - no VC++ redistributable DLLs detected"
}
finally {
    $zipCheck.Dispose()
}
Write-Host ""

Write-Host "=== Packaging complete ==="
