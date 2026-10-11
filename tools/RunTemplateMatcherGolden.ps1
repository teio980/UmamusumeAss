param(
    [string[]]$Filter = @('DeveloperToolsImageMatchTests'),
    [string]$TracePath = "build/template-matcher-trace.jsonl",
    [switch]$Native,
    [switch]$Offline,
    [switch]$NoBuild,
    [switch]$IgnoreNuGetAudit
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testProject = Join-Path $repoRoot 'tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj'
$testAssembly = Join-Path $repoRoot 'tests/UmamusumeWpfGui.Tests/bin/Release/net10.0-windows10.0.17763.0/UmamusumeWpfGui.Tests.dll'
$runnerProject = Join-Path $repoRoot 'tools/TemplateMatcherGoldenRunner/TemplateMatcherGoldenRunner.csproj'
$builtRunnerAssembly = Join-Path $repoRoot 'build/template-matcher-runner/Release/net10.0-windows10.0.17763.0/TemplateMatcherGoldenRunner.dll'
$testOutput = Split-Path -Parent $testAssembly
$runnerAssembly = Join-Path $testOutput 'TemplateMatcherGoldenRunner.dll'
$temp = Join-Path $repoRoot 'build/msvc-debug/test-temp'
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$env:TEMP = $temp
$env:TMP = $temp
if ($IgnoreNuGetAudit) { $env:NuGetAudit = 'false' }
$traceFullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $TracePath))
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($traceFullPath)) -Force | Out-Null
$env:TM_GOLDEN_TRACE = $traceFullPath

if (-not $NoBuild) {
    $testBuildArgs = @('build', $testProject, '-c', 'Release')
    if ($IgnoreNuGetAudit) { $testBuildArgs += '-p:NuGetAudit=false' }
    & dotnet @testBuildArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build $runnerProject -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path -LiteralPath $testAssembly)) { throw "Missing test assembly: $testAssembly" }
if (-not (Test-Path -LiteralPath $builtRunnerAssembly)) { throw "Missing golden runner assembly: $builtRunnerAssembly" }
foreach ($extension in @('.dll', '.deps.json', '.runtimeconfig.json')) {
    $source = [IO.Path]::ChangeExtension($builtRunnerAssembly, $extension)
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing golden runner file: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $testOutput ([IO.Path]::GetFileName($source))) -Force
}
$runnerArgs = @($testAssembly)
if ($Native) { $runnerArgs += '--native' }
if ($Offline) { $runnerArgs += '--offline' }
$runnerArgs += $Filter
dotnet $runnerAssembly @runnerArgs
exit $LASTEXITCODE
