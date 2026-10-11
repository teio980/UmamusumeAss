$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$wpfRoot = Join-Path $repository 'src/UmamusumeWpfGui'
$bridgeRoot = Join-Path $repository 'src/Umamusume.CoreBridge'
$violations = [Collections.Generic.List[string]]::new()

# These guards intentionally inspect production sources. Legacy implementation
# must be deleted, not hidden with Compile Remove or retained for old tests.
$oldExecutionTypes = @(
    '\b(class|interface)\s+(ManagedGrassTaskExecutor|CareerTrainingEngine|CareerFlowDispatcher|CareerJsonActionExecutor)\b',
    '\b(class|interface)\s+(AdbNormalCareerTrainingPipeline|AdbIndependentTrainingPipeline|UraSmartTrainingStrategy|RatioTrainingStrategy)\b',
    '\b(class|interface)\s+(AdbDailyRacePipeline|AdbMailCollectionPipeline|AdbMissionCollectionPipeline|AdbTeamRacePipeline|AdbStartGamePipeline)\b',
    '\b(class|interface)\s+(AdbConnectionSessionFactory|AdbTouchRuntime|WindowsOcrTextRecognizer)\b'
    '\b(class|interface)\s+(UraScenarioModule|UraScenarioState|UraTurnPositionParser|CareerVisualPackageLoader|UraScenarioPackLoader)\b'
)
foreach ($file in Get-ChildItem -LiteralPath $wpfRoot -Recurse -File -Filter '*.cs') {
    if ($file.FullName -match '[\\/](bin|obj)[\\/]') { continue }
    $source = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($pattern in $oldExecutionTypes) {
        if ($source -match $pattern) { $violations.Add("Legacy execution type remains: $($file.FullName)") }
    }
}
foreach ($file in Get-ChildItem -LiteralPath $bridgeRoot -Recurse -File -Include '*.cs','*.csproj') {
    if ($file.FullName -match '[\\/](bin|obj)[\\/]') { continue }
    $source = Get-Content -LiteralPath $file.FullName -Raw
    if ($source -match 'using\s+System\.Windows\b|<UseWPF>\s*true|ProjectReference[^>]*UmamusumeWpfGui') {
        $violations.Add("Bridge depends on WPF: $($file.FullName)")
    }
}
$hostProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Umamusume.CoreBridge.IntegrationHost/Umamusume.CoreBridge.IntegrationHost.csproj') -Raw
if ($hostProject -match 'UmamusumeWpfGui|<UseWPF>\s*true') { $violations.Add('The independent host references WPF.') }

if ($violations.Count -gt 0) { throw ($violations -join [Environment]::NewLine) }
Write-Output 'Architecture boundary passed: legacy execution removed, Bridge and host independent of WPF.'
