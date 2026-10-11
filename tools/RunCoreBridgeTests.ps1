param(
    [string]$NativeDllPath = (Join-Path $PSScriptRoot '..\build\msvc-debug\src\UmaAssistantCore\Debug\UmamusumeCore.dll'),
    [string]$FakeAdbPath = (Join-Path $PSScriptRoot '..\build\msvc-debug\tests\Connection\Debug\UmaFakeAdb.exe'),
    [switch]$NoBuild,
    [string[]]$Filter = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testProject = Join-Path $repoRoot 'tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj'
$testAssembly = Join-Path $repoRoot 'tests/Umamusume.CoreBridge.Tests/bin/Debug/net10.0/Umamusume.CoreBridge.Tests.dll'
$testOutput = Split-Path -Parent $testAssembly
$hostProject = Join-Path $repoRoot 'tools/Umamusume.CoreBridge.IntegrationHost/Umamusume.CoreBridge.IntegrationHost.csproj'
$hostAssembly = Join-Path $repoRoot 'tools/Umamusume.CoreBridge.IntegrationHost/bin/Debug/net10.0/Umamusume.CoreBridge.IntegrationHost.dll'
$runnerProject = Join-Path $repoRoot 'tools/BridgeTestRunner/BridgeTestRunner.csproj'
$runnerAssembly = Join-Path $repoRoot 'build/bridge-test-runner/Debug/net10.0/BridgeTestRunner.dll'
$nativeDll = (Resolve-Path -LiteralPath $NativeDllPath).Path
$fakeAdb = (Resolve-Path -LiteralPath $FakeAdbPath).Path

if (-not $NoBuild) {
    $hostBuildArgs = @('build', $hostProject, '-c', 'Debug', "-p:NativeDllPath=$nativeDll", "-p:FakeAdbPath=$fakeAdb")
    & dotnet @hostBuildArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $testBuildArgs = @('build', $testProject, '-c', 'Debug', "-p:NativeDllPath=$nativeDll", "-p:FakeAdbPath=$fakeAdb")
    & dotnet @testBuildArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & dotnet build $runnerProject -c Debug
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

foreach ($requiredFile in @($testAssembly, $hostAssembly, $runnerAssembly, $nativeDll, $fakeAdb)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required Bridge test input is missing: $requiredFile"
    }
}

Copy-Item -LiteralPath $nativeDll -Destination $testOutput -Force
$hostOutput = Split-Path -Parent $hostAssembly
Copy-Item -LiteralPath $nativeDll -Destination $hostOutput -Force
Copy-Item -LiteralPath $fakeAdb -Destination $hostOutput -Force

$env:UMA_NATIVE_INTEGRATION = '1'
$env:UMA_INTEGRATION_HOST_PATH = $hostAssembly
$env:UMA_FAKE_ADB_PATH = $fakeAdb
$ocrGolden = Join-Path $repoRoot 'build/supervisor-ocr-baseline/ocr-golden.jsonl'
if (Test-Path -LiteralPath $ocrGolden -PathType Leaf) {
    $env:UMA_OCR_GOLDEN_PATH = (Resolve-Path -LiteralPath $ocrGolden).Path
}
else {
    Remove-Item Env:UMA_OCR_GOLDEN_PATH -ErrorAction SilentlyContinue
}

$runnerArgs = @($testAssembly) + $Filter
& dotnet $runnerAssembly @runnerArgs
exit $LASTEXITCODE
