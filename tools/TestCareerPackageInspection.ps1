param([string]$Executable = (Join-Path $PSScriptRoot '../build/msvc-debug/tools/Debug/uma_pipeline_replay.exe'))
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$replayExe = (Resolve-Path -LiteralPath $Executable).Path
$resourceRoot = (Resolve-Path (Join-Path $repository 'resource')).Path
$manifestPath = Join-Path $resourceRoot 'hachimi/ura/manifest.json'
$originalHash = (Get-FileHash -LiteralPath $manifestPath).Hash
$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('uma-package-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDirectory | Out-Null
$requestPath = Join-Path $tempDirectory 'request.json'
$checks = 0

function Invoke-Inspection([object[]]$Overrides, [bool]$ExpectedValid) {
    @{ resource_root = $resourceRoot; files = @($Overrides) } | ConvertTo-Json -Depth 100 |
        Set-Content -LiteralPath $requestPath -Encoding utf8
    $output = & $replayExe --inspect-career-package $manifestPath $requestPath
    $exitCode = $LASTEXITCODE
    $result = $output | ConvertFrom-Json
    if (($exitCode -eq 0) -ne $ExpectedValid -or $result.valid -ne $ExpectedValid) {
        throw "Unexpected native inspection result: exit=$exitCode diagnostics=$($result.diagnostics) error=$($result.error)"
    }
    $script:checks++
    return $result
}

try {
    $baseline = Invoke-Inspection @() $true
    $tasks = @($baseline.execution.definition.tasks.PSObject.Properties)
    if (-not $baseline.ready -or $tasks.Count -eq 0 -or
        $tasks.Count -ne $baseline.execution.task_order.Count -or $baseline.files.Count -eq 0) {
        throw 'The native inspector omitted the composed execution or source files.'
    }
    $manifestText = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
    $manifestText.schemaVersion = 999
    $null = Invoke-Inspection @(@{path = $manifestPath; raw_json = ($manifestText | ConvertTo-Json -Depth 100)}) $false

    $fragmentPath = $baseline.execution.fragment_paths[0]
    $fragment = ($baseline.files | Where-Object path -eq $fragmentPath | Select-Object -First 1).raw_json | ConvertFrom-Json
    $firstTask = @($fragment.tasks.PSObject.Properties)[0]
    $firstTask.Value | Add-Member -NotePropertyName 'unknownExecutionProperty' -NotePropertyValue $true
    $null = Invoke-Inspection @(@{path = $fragmentPath; raw_json = ($fragment | ConvertTo-Json -Depth 100)}) $false

    $profileFile = $null
    $profile = $null
    foreach ($file in $baseline.files) {
        $document = $file.raw_json | ConvertFrom-Json
        if (@($document.screens | Where-Object { $_.actions.Count -gt 0 }).Count -gt 0) {
            $profileFile = $file
            $profile = $document
            break
        }
    }
    if ($null -eq $profileFile) { throw 'The fixture has no screen action binding to validate.' }
    $screen = $profile.screens | Where-Object { $_.actions.Count -gt 0 } | Select-Object -First 1
    $screen.actions[0].task = 'missing-task-for-unsaved-edit'
    $result = Invoke-Inspection @(@{path = $profileFile.path; raw_json = ($profile | ConvertTo-Json -Depth 100)}) $false
    if ($result.diagnostics.Count -eq 0) { throw 'A broken binding had no native diagnostic.' }

    $null = Invoke-Inspection @(@{path = (Join-Path $tempDirectory 'outside.json'); raw_json = '{}'}) $false
    $originalText = Get-Content -LiteralPath $manifestPath -Raw
    $duplicate = @{path = $manifestPath; raw_json = $originalText}
    $null = Invoke-Inspection @($duplicate, $duplicate) $false
    $invalidText = '{unsaved-invalid-json'
    $result = Invoke-Inspection @(@{path = $manifestPath; raw_json = $invalidText}) $false
    if (-not @($result.files | Where-Object raw_json -eq $invalidText).Count) {
        throw 'The inspector lost invalid unsaved editor text.'
    }
    if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne $originalHash) {
        throw 'Inspection modified the source package.'
    }
    Write-Output "Career package inspection: $checks scenarios passed (production package, unsaved manifest/fragment/binding, containment, duplicate paths, invalid editor text)."
    exit 0
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath($tempDirectory)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTemp.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedTemp)).StartsWith('uma-package-check-')) {
        throw "Refusing cleanup outside the generated test directory: $resolvedTemp"
    }
    Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
}
