[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'

try {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $sourceRoot = Join-Path $repositoryRoot 'resource\hachimi'
    $publishRoot = [System.IO.Path]::GetFullPath($PublishDirectory)
    $destinationRoot = Join-Path $publishRoot 'resource\hachimi'

    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Hachimi source resources were not found: $sourceRoot"
    }
    if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
        throw "Publish directory was not found: $publishRoot"
    }

    $sourcePrefix = [System.IO.Path]::GetFullPath($sourceRoot) + [System.IO.Path]::DirectorySeparatorChar
    $stagedCount = 0
    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
        $relativePath = $sourceFile.FullName.Substring($sourcePrefix.Length)
        $normalizedPath = $relativePath.Replace('\', '/')

        # Keep the publish exclusions in sync with UmamusumeWpfGui.csproj.
        if ($normalizedPath.StartsWith('debug/', [System.StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath -match '(^|/)testdata(/|$)' -or
            $normalizedPath.StartsWith('ura/screens/captures/', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $destinationFile = Join-Path $destinationRoot $relativePath
        $destinationDirectory = Split-Path -Parent $destinationFile
        [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null

        $copyRequired = -not (Test-Path -LiteralPath $destinationFile -PathType Leaf)
        if (-not $copyRequired) {
            $destinationInfo = Get-Item -LiteralPath $destinationFile
            $copyRequired = $destinationInfo.Length -ne $sourceFile.Length -or
                $destinationInfo.LastWriteTimeUtc -lt $sourceFile.LastWriteTimeUtc
        }

        if ($copyRequired) {
            Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile -Force
        }

        $stagedInfo = Get-Item -LiteralPath $destinationFile -ErrorAction SilentlyContinue
        if ($null -eq $stagedInfo -or $stagedInfo.Length -ne $sourceFile.Length) {
            throw "Hachimi resource was not staged correctly: $relativePath"
        }
        $stagedCount++
    }

    $requiredCareerAsset = Join-Path $destinationRoot 'career\race\templates\runtime_frames\career_intro_event_skip_ura.png'
    if (-not (Test-Path -LiteralPath $requiredCareerAsset -PathType Leaf)) {
        throw "Required Career template is missing from publish output: $requiredCareerAsset"
    }

    Write-Host "Staged and verified $stagedCount Hachimi resource files."
    exit 0
}
catch {
    [Console]::Error.WriteLine("[ERROR] $($_.Exception.Message)")
    exit 1
}
