



















[CmdletBinding()]
param(

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = (Join-Path -Path $PSScriptRoot -ChildPath "..\dist"),

    [Parameter(Mandatory = $false)]
    [string]$BundledResourceVersion = "",

    [Parameter(Mandatory = $false)]
    [switch]$BuildInstaller,

    [Parameter(Mandatory = $false)]
    [string]$InstallerCompilerPath = ""
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

$versionFile = Join-Path $SolutionRoot "version.txt"
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Version source not found: $versionFile"
}
$Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "version.txt must contain a stable semantic version: $Version"
}
if ([string]::IsNullOrWhiteSpace($BundledResourceVersion)) {
    $BundledResourceVersion = $Version
}

$ZipPath = Join-Path -Path $OutputDir -ChildPath "UmamusumeAss-win-x64.zip"
$VersionedZipPath = Join-Path -Path $OutputDir -ChildPath "UmamusumeAss-v$Version-win-x64-full.zip"
$InstallerPath = Join-Path -Path $OutputDir -ChildPath "UmamusumeAss-v$Version-win-x64-setup.exe"
$InstallerScript = Join-Path -Path $SolutionRoot -ChildPath "tools\installer.iss"

function Get-Sha256Hex([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToUpperInvariant()
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

Write-Host "=== UmamusumeAss Packaging ==="
Write-Host "Solution root:    $SolutionRoot"
Write-Host "Output directory: $OutputDir"
Write-Host "Version:          $Version"
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

$uraSource = Join-Path $SolutionRoot "resource\hachimi\ura"
$pipelineSource = Join-Path $SolutionRoot "resource\hachimi\pipelines"
$uraPublish = Join-Path $PublishDir "resource\hachimi\ura"
$pipelinePublish = Join-Path $PublishDir "resource\hachimi\pipelines"
$uraPublishParent = Join-Path $PublishDir "resource\hachimi"
if (-not (Test-Path -LiteralPath $uraSource)) {
    throw "URA scenario source package is incomplete: $uraSource"
}
if (-not (Test-Path -LiteralPath $pipelineSource)) {
    throw "Hachimi pipeline source package is incomplete: $pipelineSource"
}
if (-not (Test-Path -LiteralPath $pipelinePublish)) {
    throw "Hachimi pipeline resources were not staged into publish output: $pipelinePublish"
}
if (-not (Test-Path -LiteralPath $uraPublish)) {
    New-Item -ItemType Directory -Path $uraPublishParent -Force | Out-Null
    Copy-Item -LiteralPath $uraSource -Destination $uraPublishParent -Recurse -Force
}
if (-not (Test-Path -LiteralPath $uraPublish)) {
    throw "URA scenario package was not staged into publish output: $uraPublish"
}
$uraFiles = @(Get-ChildItem -LiteralPath $uraSource -File -Recurse)
$missingUraFiles = @(
    foreach ($uraFile in $uraFiles) {
        $relativePath = $uraFile.FullName.Substring($uraSource.Length + 1)
        $publishedPath = Join-Path $uraPublish $relativePath
        if (-not (Test-Path -LiteralPath $publishedPath)) {
            $relativePath
        }
    }
)
if ($missingUraFiles.Count -gt 0) {
    throw "URA scenario files were not staged into publish output: $($missingUraFiles -join ', ')"
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

$updaterExe = Join-Path -Path $NativeStaging -ChildPath "UmamusumeAss.Updater.exe"
if (-not (Test-Path -LiteralPath $updaterExe)) {
    throw "Updater artifact not found: $updaterExe"
}
Copy-Item -LiteralPath $updaterExe -Destination $PublishDir -Force
Write-Host "  Copied UmamusumeAss.Updater.exe"


& (Join-Path $PSScriptRoot 'verify-managed-output.ps1') -Directory $PublishDir -ExpectedVersion $Version
Write-Host "  Verified managed assembly and dependency versions"


$hostfxr = Join-Path -Path $PublishDir -ChildPath "hostfxr.dll"
$spcl   = Join-Path -Path $PublishDir -ChildPath "System.Private.CoreLib.dll"
if (-not (Test-Path -LiteralPath $hostfxr) -and -not (Test-Path -LiteralPath $spcl)) {
    throw "Self-contained runtime evidence missing: neither hostfxr.dll nor System.Private.CoreLib.dll found in $PublishDir"
}
Write-Host "  Verified self-contained runtime evidence"

if (-not (Test-Path -LiteralPath $uraPublish)) {
    throw "URA scenario package was not included in publish output: $uraPublish"
}
Write-Host "  Verified URA scenario resource tree ($($uraFiles.Count) files)"

$resourceInventory = @(
    Get-ChildItem -LiteralPath (Join-Path $PublishDir "resource") -File -Recurse |
        ForEach-Object {
            $relative = $_.FullName.Substring((Join-Path $PublishDir "resource").Length + 1).Replace('\', '/')
            [ordered]@{
                path = $relative
                size = $_.Length
                sha256 = Get-Sha256Hex $_.FullName
            }
        } |
        Sort-Object -Property path
)
$inventoryDocument = [ordered]@{
    schemaVersion = 1
    version = $Version
    files = $resourceInventory
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    (Join-Path $PublishDir "resource.inventory.json"),
    ($inventoryDocument | ConvertTo-Json -Depth 8),
    $utf8NoBom)
$appVersionDocument = [ordered]@{
    schemaVersion = 1
    version = $Version
    bundledResourceVersion = $BundledResourceVersion
}
[System.IO.File]::WriteAllText(
    (Join-Path $PublishDir "app-version.json"),
    ($appVersionDocument | ConvertTo-Json -Depth 4),
    $utf8NoBom)

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

Copy-Item -LiteralPath $ZipPath -Destination $VersionedZipPath -Force
Write-Host "Versioned archive: $VersionedZipPath"

$zipSize = (Get-Item -LiteralPath $ZipPath).Length
Write-Host "OK - $ZipPath ($zipSize bytes)"
Write-Host ""


if ($BuildInstaller) {
    Write-Host "--- Step 7/8: Creating Windows installer ---"

    if (-not (Test-Path -LiteralPath $InstallerScript -PathType Leaf)) {
        throw "Inno Setup script not found: $InstallerScript"
    }

    if ([string]::IsNullOrWhiteSpace($InstallerCompilerPath)) {
        $isccCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($null -ne $isccCommand) {
            $InstallerCompilerPath = $isccCommand.Source
        }
        else {
            $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
            $programFiles = [Environment]::GetEnvironmentVariable("ProgramFiles")
            $localAppData = [Environment]::GetEnvironmentVariable("LOCALAPPDATA")
            $compilerCandidates = @()
            if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
                $compilerCandidates += Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"
            }
            if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
                $compilerCandidates += Join-Path $programFiles "Inno Setup 6\ISCC.exe"
            }
            if (-not [string]::IsNullOrWhiteSpace($localAppData)) {
                $compilerCandidates += Join-Path $localAppData "Programs\Inno Setup 6\ISCC.exe"
            }
            $InstallerCompilerPath = $compilerCandidates |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
                Select-Object -First 1
        }
    }

    if ([string]::IsNullOrWhiteSpace($InstallerCompilerPath) -or
        -not (Test-Path -LiteralPath $InstallerCompilerPath -PathType Leaf)) {
        throw "Inno Setup compiler ISCC.exe was not found. Install Inno Setup 6 or pass -InstallerCompilerPath."
    }

    $installerArguments = @(
        "/DAppVersion=$Version"
        "/DSourceDir=$PublishDir"
        "/DOutputDir=$OutputDir"
        $InstallerScript
    )
    & $InstallerCompilerPath @installerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
    }
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        throw "Installer compilation completed but output was not found: $InstallerPath"
    }
    Write-Host "Installer: $InstallerPath"
    Write-Host "OK"
    Write-Host ""
}
else {
    Write-Host "--- Step 7/8: Windows installer disabled ---"
    Write-Host "Use -BuildInstaller to create the installable EXE with an uninstall entry."
    Write-Host ""
}


Write-Host "--- Step 8/8: Verifying no VC++ redistributable DLLs in archive ---"
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
