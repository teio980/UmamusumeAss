[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ReleaseTag,
    [Parameter(Mandatory = $true)][ValidateSet('program', 'resource')][string]$Kind,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string[]]$PreviousPackagePath,
    [string[]]$FromVersion,
    [string[]]$PreviousManifestPath,
    [string]$BundledResourceVersion = $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$versionSource = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim()
if ($Kind -eq 'program') {
    if ($versionSource -ne $Version) { throw "Requested program version does not match version.txt ($versionSource)." }
    if ($ReleaseTag -ne "v$Version") { throw "Program tag must exactly match version.txt." }
} else {
    if ($Version -notmatch '^\d{4}\.\d{2}\.\d{2}\.\d+$' -or $ReleaseTag -ne "resource-v$Version") {
        throw "Resource version/tag must be resource-vYYYY.MM.DD.N."
    }
}
$package = (Resolve-Path $PackagePath).Path
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName

function Get-FileSha256([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try {
        return ([Convert]::ToHexString($sha.ComputeHash($stream))).ToUpperInvariant()
    } finally {
        $stream.Dispose(); $sha.Dispose()
    }
}

function Get-Inventory([string]$directory) {
    $items = @(
        Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object {
            $relative = $_.FullName.Substring($directory.Length + 1).Replace('\', '/')
            [ordered]@{ path = $relative; size = $_.Length; sha256 = Get-FileSha256 $_.FullName }
        } | Sort-Object path
    )
    return $items
}

function Get-TreeHash([string]$directory) {
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $relativeFiles = @(
            Get-ChildItem -LiteralPath $directory -File -Recurse |
                ForEach-Object { $_.FullName.Substring($directory.Length + 1).Replace('\', '/') }
        )
        [Array]::Sort($relativeFiles, [StringComparer]::Ordinal)
        foreach ($relative in $relativeFiles) {
            $nameBytes = [Text.Encoding]::UTF8.GetBytes($relative)
            $hash.AppendData($nameBytes); $hash.AppendData([byte[]](0))
            $stream = [IO.File]::OpenRead((Join-Path $directory ($relative.Replace('/', '\'))))
            try {
                $buffer = New-Object byte[] (128 * 1024)
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hash.AppendData($buffer, 0, $read)
                }
            } finally { $stream.Dispose() }
        }
        return ([Convert]::ToHexString($hash.GetHashAndReset())).ToUpperInvariant()
    } finally { $hash.Dispose() }
}

function Expand-Package([string]$archive, [string]$destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
}

$fullName = Split-Path $package -Leaf
$fullHash = Get-FileSha256 $package
$fullInventory = @()
$deltaAssets = @()
$previousPackages = @(if ($null -eq $PreviousPackagePath) { } else { $PreviousPackagePath })
$fromVersions = @(if ($null -eq $FromVersion) { } else { $FromVersion })
$previousManifests = @(if ($null -eq $PreviousManifestPath) { } else { $PreviousManifestPath })
$temporary = [IO.Path]::Combine([IO.Path]::GetTempPath(), "uma-manifest-$([Guid]::NewGuid().ToString('N'))")
try {
    $targetTree = Join-Path $temporary 'target'
    Expand-Package $package $targetTree
    if ($Kind -eq 'resource' -and (Test-Path -LiteralPath (Join-Path $targetTree 'resource'))) {
        throw 'Resource package ZIP must contain resource contents at its root, not a resource/ wrapper.'
    }
    $fullInventory = @(Get-Inventory $targetTree)
    $hashRoot = $targetTree
    $targetTreeHash = Get-TreeHash $hashRoot

    if ($previousPackages.Count -ne $fromVersions.Count) {
        throw 'PreviousPackagePath and FromVersion must have the same number of entries.'
    }
    if ($previousManifests.Count -gt 0 -and $previousManifests.Count -ne $previousPackages.Count) {
        throw 'PreviousManifestPath must be omitted or have one entry per previous package.'
    }
    for ($previousIndex = 0; $previousIndex -lt $previousPackages.Count; $previousIndex++) {
        $sourceTree = Join-Path $temporary "source-$previousIndex"
        $deltaTree = Join-Path $temporary "delta-$previousIndex"
        Expand-Package ((Resolve-Path $previousPackages[$previousIndex]).Path) $sourceTree
        if ($Kind -eq 'resource' -and (Test-Path -LiteralPath (Join-Path $sourceTree 'resource'))) {
            throw 'Previous resource package ZIP must contain resource contents at its root.'
        }
        New-Item -ItemType Directory -Path $deltaTree -Force | Out-Null
        $sourceInventory = @(Get-Inventory $sourceTree)
        $includeBundledResource = $true
        if ($Kind -eq 'program' -and $previousManifests.Count -gt 0) {
            $previousManifest = Get-Content -LiteralPath $previousManifests[$previousIndex] -Raw | ConvertFrom-Json
            $includeBundledResource = -not [string]::Equals(
                [string]$previousManifest.bundledResourceVersion,
                [string]$BundledResourceVersion,
                [StringComparison]::Ordinal)
        }
        $sourceMap = @{}; foreach ($entry in $sourceInventory) { $sourceMap[$entry.path] = $entry }
        $targetMap = @{}; foreach ($entry in $fullInventory) { $targetMap[$entry.path] = $entry }
        foreach ($entry in $fullInventory) {
            if ($Kind -eq 'program' -and $entry.path.StartsWith('resource/', [StringComparison]::OrdinalIgnoreCase)
                -and -not $includeBundledResource) { continue }
            if (-not $sourceMap.ContainsKey($entry.path) -or $sourceMap[$entry.path].sha256 -ne $entry.sha256) {
                $sourceFile = Join-Path $targetTree ($entry.path.Replace('/', '\'))
                $destFile = Join-Path $deltaTree ($entry.path.Replace('/', '\'))
                New-Item -ItemType Directory -Path (Split-Path $destFile) -Force | Out-Null
                Copy-Item $sourceFile $destFile -Force
            }
        }
        $deletes = @($sourceInventory | Where-Object {
            (-not $targetMap.ContainsKey($_.path)) -and
            -not ($Kind -eq 'program' -and $_.path.StartsWith('resource/', [StringComparison]::OrdinalIgnoreCase)
                -and -not $includeBundledResource)
        } | ForEach-Object { $_.path })
        $deltaInventory = @(Get-Inventory $deltaTree)
        if ($deltaInventory.Count -eq 0 -and $deletes.Count -eq 0) {
            continue
        }
        $from = $FromVersion[$previousIndex]
        $deltaName = "UmamusumeAss-v$from-to-v$Version-$Kind-win-x64-delta.zip"
        $deltaPath = Join-Path $output $deltaName
        if ($deltaInventory.Count -gt 0) {
            Compress-Archive -Path (Join-Path $deltaTree '*') -DestinationPath $deltaPath -CompressionLevel Optimal -Force
        } else {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $emptyArchive = [System.IO.Compression.ZipFile]::Open(
                $deltaPath, [System.IO.Compression.ZipArchiveMode]::Create)
            $emptyArchive.Dispose()
        }
        $deltaAssets += [ordered]@{
            type = 'delta'; fromVersion = $from; assetName = $deltaName
            size = (Get-Item $deltaPath).Length; sha256 = Get-FileSha256 $deltaPath
            targetTreeSha256 = $targetTreeHash; sourceTreeSha256 = (Get-TreeHash $sourceTree)
            sourceManifestSha256 = if ($previousManifests.Count -gt 0) { Get-FileSha256 $previousManifests[$previousIndex] } else { '' }
            files = $deltaInventory; deletes = $deletes
        }
    }

    $assets = @()
    $assets += $deltaAssets
    $assets += [ordered]@{
        type = 'full'; assetName = $fullName; size = (Get-Item $package).Length; sha256 = $fullHash
        targetTreeSha256 = $targetTreeHash; sourceTreeSha256 = ''; sourceManifestSha256 = ''
        files = $fullInventory; deletes = @()
    }
    $manifest = [ordered]@{
        schemaVersion = 1; product = 'UmamusumeAss'; kind = $Kind; channel = 'stable'
        targetOs = 'windows'; targetArch = 'x64'; version = $Version; releaseTag = $ReleaseTag
        bundledResourceVersion = $BundledResourceVersion; supportedResourceSchema = '1'
        minAppVersion = '0.2.0'; maxAppVersion = ''; resourceSchema = '1'; assets = $assets
    }
    $manifestPath = Join-Path $output "$Kind-manifest-$Version.json"
    $manifest | ConvertTo-Json -Depth 20 -Compress | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    Write-Output $manifestPath
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
