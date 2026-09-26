[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Directory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "ExpectedVersion must be major.minor.patch: $ExpectedVersion"
}

$outputDirectory = (Resolve-Path -LiteralPath $Directory -ErrorAction Stop).Path
$expectedAssemblyVersion = [Version]"$ExpectedVersion.0"

foreach ($name in @('UmamusumeAss.dll', 'Umamusume.CoreBridge.dll')) {
    $path = Join-Path $outputDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Managed output is missing $name in $outputDirectory"
    }

    $actualVersion = [System.Reflection.AssemblyName]::GetAssemblyName($path).Version
    if ($actualVersion -ne $expectedAssemblyVersion) {
        throw "$name has assembly version $actualVersion; expected $expectedAssemblyVersion in $outputDirectory"
    }
}

$depsPath = Join-Path $outputDirectory 'UmamusumeAss.deps.json'
if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
    throw "Managed output is missing UmamusumeAss.deps.json in $outputDirectory"
}

$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
$bridgeDependency = "Umamusume.CoreBridge/$ExpectedVersion"
if ($deps.libraries.PSObject.Properties.Name -notcontains $bridgeDependency) {
    throw "UmamusumeAss.deps.json does not reference $bridgeDependency in $outputDirectory"
}

Write-Host "Verified managed output: GUI and CoreBridge $expectedAssemblyVersion; dependency $bridgeDependency"
