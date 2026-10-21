param([switch]$IgnoreNuGetAudit, [switch]$SkipNativeBuildAndTests, [switch]$WpfOnly)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$logDirectory = Join-Path $repository 'build/phase5'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$temporaryDirectory = Join-Path $repository 'build/msvc-debug/test-temp'
New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$env:TEMP = $temporaryDirectory
$env:TMP = $temporaryDirectory

function Invoke-LoggedCommand([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $log = Join-Path $logDirectory ($Name + '.log')
    Write-Output "Running $Name; log: $log"
    & $Executable @Arguments *> $log
    $exitCode = $LASTEXITCODE
    Get-Content -LiteralPath $log -Tail 8
    if ($exitCode -ne 0) { throw "$Name failed with exit code $exitCode. See $log" }
}

Push-Location $repository
try {
    if ($WpfOnly) {
        Write-Output 'Running the WPF build and offline suite with existing native outputs.'
    }
    elseif ($SkipNativeBuildAndTests) {
        Write-Output 'Using existing native outputs; native build and CTest are omitted in this invocation.'
    }
    else {
        Invoke-LoggedCommand 'native-configure' 'cmake' @('--preset', 'msvc-debug')
        Invoke-LoggedCommand 'native-build' 'cmake' @('--build', '--preset', 'msvc-debug', '--parallel', '4')
        Invoke-LoggedCommand 'native-tests' 'ctest' @('--preset', 'msvc-debug', '-E', 'ManagedBridgeTests|WpfGuiTests', '--output-on-failure')
    }
    if (-not $WpfOnly) {
        Invoke-LoggedCommand 'pipeline-replay' 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'TestPipelineReplay.ps1'))
        Invoke-LoggedCommand 'career-package-inspection' 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'TestCareerPackageInspection.ps1'))
        Invoke-LoggedCommand 'architecture' 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'TestArchitectureBoundary.ps1'))
    }

    # Staging is a local test input copy; this script never invokes publish,
    # install, package, signing or installer creation.
    $nativeDll = Join-Path $repository 'build/msvc-debug/src/UmaAssistantCore/Debug/UmamusumeCore.dll'
    $nativeStaging = Join-Path $repository 'build/native-staging'
    New-Item -ItemType Directory -Path $nativeStaging -Force | Out-Null
    Copy-Item -LiteralPath $nativeDll -Destination $nativeStaging -Force
    $replayTool = Join-Path $repository 'build/msvc-debug/tools/Debug/uma_pipeline_replay.exe'
    Copy-Item -LiteralPath $replayTool -Destination $nativeStaging -Force
    $updater = Join-Path $repository 'build/msvc-debug/src/UmamusumeAssUpdater/Debug/UmamusumeAss.Updater.exe'
    Copy-Item -LiteralPath $updater -Destination $nativeStaging -Force
    if (-not $WpfOnly) {
        Invoke-LoggedCommand 'bridge-tests' 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'RunCoreBridgeTests.ps1'))
        Invoke-LoggedCommand 'headless-commands' 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'TestHeadlessCommands.ps1'))
    }

    $managedArguments = @('build', 'tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj', '-c', 'Release', '-p:SkipNativeRuntimeBuild=true')
    if ($IgnoreNuGetAudit) { $managedArguments += '-p:NuGetAudit=false' }
    Invoke-LoggedCommand 'managed-build' 'dotnet' $managedArguments
    $guiOutput = Join-Path $repository 'src/UmamusumeWpfGui/bin/Release/net10.0-windows10.0.17763.0'
    foreach ($runtime in @($nativeDll, $replayTool, $updater, (Join-Path $repository 'version.txt'))) {
        Copy-Item -LiteralPath $runtime -Destination $guiOutput -Force
    }
    Invoke-LoggedCommand 'golden-runner-build' 'dotnet' @('build', 'tools/TemplateMatcherGoldenRunner/TemplateMatcherGoldenRunner.csproj', '-c', 'Release')

    # Run every surviving offline UI/adapter/settings regression. Live traits
    # and packaging traits are excluded at discovery, including live tests with
    # environment opt-in gates. The user's Phase 5 scope excludes packaging.
    $runnerArguments = @('-NoProfile', '-Command',
        '& ./tools/RunTemplateMatcherGolden.ps1 -NoBuild -Offline -Filter @() -TracePath build/phase5/wpf-trace.jsonl; exit $LASTEXITCODE')
    Invoke-LoggedCommand 'wpf-tests' 'pwsh' $runnerArguments
    if ($WpfOnly) {
        Write-Output "Phase 5 WPF build and offline checks passed. Logs: $logDirectory"
    }
    elseif ($SkipNativeBuildAndTests) {
        Write-Output "Phase 5 replay, architecture and managed checks passed. Logs: $logDirectory"
    }
    else {
        Write-Output "Phase 5 build and offline checks passed. Logs: $logDirectory"
    }
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Pop-Location
}
