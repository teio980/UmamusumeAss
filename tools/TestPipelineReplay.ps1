param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\build\msvc-debug\tools\Debug\uma_pipeline_replay.exe')
)
$ErrorActionPreference = 'Stop'
$replayExe = (Resolve-Path -LiteralPath $Executable).Path
$fixtureRoot = Join-Path $PSScriptRoot 'replay-fixtures'
$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('uma-replay-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDirectory | Out-Null
$checks = 0

function Invoke-Replay([string]$Pipeline, [string]$Evidence, [bool]$ExpectedSuccess) {
    $output = & $replayExe $Pipeline $Evidence
    $exitCode = $LASTEXITCODE
    $result = $output | ConvertFrom-Json
    if (($exitCode -eq 0) -ne $ExpectedSuccess -or $result.success -ne $ExpectedSuccess) {
        throw "Replay unexpected exit/result: exit=$exitCode output=$output"
    }
    $script:checks++
    return $result
}

try {
    $pipeline = Join-Path $fixtureRoot 'transition.pipeline.json'
    $replay = Join-Path $fixtureRoot 'transition.replay.json'
    $result = Invoke-Replay $pipeline $replay $true
    if ($result.actions.Count -ne 1 -or $result.actions[0].type -ne 'tap' -or
        $result.actions[0].x -ne 120 -or $result.actions[0].y -ne 230 -or
        @($result.events | Where-Object type -eq 'TransitionVerified').Count -ne 1) {
        throw "Transition replay did not confirm the matched input: $($result | ConvertTo-Json -Depth 10 -Compress)"
    }

    $evidence = Join-Path $tempDirectory 'evidence.json'
    '{"version":1,"frames":[{"cancelled":true}]}' | Set-Content -LiteralPath $evidence -Encoding utf8
    $null = Invoke-Replay $pipeline $evidence $false

    '{"version":1,"frames":[{"matches":{"entry.png":{"score":0.99,"x":100,"y":200,"width":40,"height":60}}}]}' |
        Set-Content -LiteralPath $evidence -Encoding utf8
    $result = Invoke-Replay $pipeline $evidence $false
    if (-not $result.exhausted) { throw 'Missing evidence did not report frame exhaustion.' }

    '{"version":1,"frames":[{"matches":{"entry.png":{"score":0.99,"x":100,"y":200,"width":40,"height":60}},"actionSucceeded":false}]}' |
        Set-Content -LiteralPath $evidence -Encoding utf8
    $result = Invoke-Replay $pipeline $evidence $false
    if ($result.actions.Count -ne 1 -or $result.actions[0].succeeded) { throw 'Failed input was hidden.' }

    '{"version":2,"frames":[{}]}' | Set-Content -LiteralPath $evidence -Encoding utf8
    $null = Invoke-Replay $pipeline $evidence $false

    '{"version":1,"operationBudget":1,"frames":[{}]}' | Set-Content -LiteralPath $evidence -Encoding utf8
    $result = Invoke-Replay $pipeline $evidence $false
    if (-not $result.budgetExceeded) { throw 'Replay budget was not enforced.' }

    $unicodeDirectory = Join-Path $tempDirectory '回放路径'
    New-Item -ItemType Directory -Path $unicodeDirectory | Out-Null
    $unicodePipeline = Join-Path $unicodeDirectory '流程.json'
    Copy-Item -LiteralPath $pipeline -Destination $unicodePipeline
    $null = Invoke-Replay $unicodePipeline $replay $true

    $customPipeline = Join-Path $tempDirectory 'flow.json'
    '{"schemaVersion":1,"start":"Claim","tasks":{"Claim":{"targetText":"领取","action":"ClickText","success":true}}}' |
        Set-Content -LiteralPath $customPipeline -Encoding utf8
    '{"version":1,"frames":[{"ocr":[{"text":"领取","x":100,"y":300,"width":40,"height":30,"confidence":1}]}]}' |
        Set-Content -LiteralPath $evidence -Encoding utf8
    $result = Invoke-Replay $customPipeline $evidence $true
    if ($result.actions.Count -ne 1 -or $result.actions[0].x -ne 120 -or $result.actions[0].y -ne 315) {
        throw 'Fixed OCR evidence did not reach the shared ClickText action.'
    }

    '{"schemaVersion":1,"start":"Monitor","tasks":{"Monitor":{"algorithm":"ParallelMonitor","successTask":"SuccessPage","monitorTasks":["Button"],"timeoutMs":0},"SuccessPage":{"template":"success.png","action":"DoNothing","success":true},"Button":{"template":"button.png","action":"ClickSelf"}}}' |
        Set-Content -LiteralPath $customPipeline -Encoding utf8
    '{"version":1,"frames":[{"matches":{"success.png":{"score":0.97,"x":10,"y":20,"width":30,"height":40},"button.png":{"score":0.99,"x":100,"y":200,"width":30,"height":40}}},{"matches":{"success.png":{"score":0.97,"x":10,"y":20,"width":30,"height":40}}}]}' |
        Set-Content -LiteralPath $evidence -Encoding utf8
    $result = Invoke-Replay $customPipeline $evidence $true
    if ($result.actions.Count -ne 0) { throw 'Same-frame success evidence lost priority over a click candidate.' }

    Write-Output "Pipeline replay: $checks scenarios passed (transition, cancellation, exhaustion, input failure, schema, budget, Unicode paths, OCR evidence, same-frame priority)."
    exit 0
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath($tempDirectory)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemp.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemp).StartsWith('uma-replay-check-')) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
