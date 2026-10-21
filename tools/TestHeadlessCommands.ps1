param(
    [string]$HostAssembly = (Join-Path $PSScriptRoot 'Umamusume.CoreBridge.IntegrationHost/bin/Debug/net10.0/Umamusume.CoreBridge.IntegrationHost.dll'),
    [string]$FakeAdb = (Join-Path $PSScriptRoot '../build/msvc-debug/tests/Connection/Debug/UmaFakeAdb.exe')
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$hostDll = (Resolve-Path -LiteralPath $HostAssembly).Path
$fakeExecutable = (Resolve-Path -LiteralPath $FakeAdb).Path
$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('uma-headless-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDirectory | Out-Null
$previousSerial = $env:UMA_FAKE_ADB_DEVICES_SERIAL
$env:UMA_FAKE_ADB_DEVICES_SERIAL = 'phase5-headless'
$checks = 0

function Invoke-Host([string[]]$Arguments, [int]$ExpectedExit) {
    $output = & dotnet $hostDll @Arguments 2> (Join-Path $tempDirectory 'events.log')
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $ExpectedExit) {
        $events = Get-Content -LiteralPath (Join-Path $tempDirectory 'events.log') -Tail 3
        throw "Headless command failed: exit=$exitCode expected=$ExpectedExit output=$output events=$events"
    }
    $script:checks++
    return ($output | ConvertFrom-Json)
}

try {
    $catalog = Invoke-Host @('catalog', 'all', $repository) 0
    if ($catalog.characters.Count -eq 0 -or $catalog.skills.Count -eq 0 -or $catalog.strategies.Count -eq 0) {
        throw 'Core catalog omitted required populated categories.'
    }
    $pipeline = Join-Path $tempDirectory 'pipeline.json'
    '{"schemaVersion":1,"name":"headless-check","tasks":{"home":{"action":"DoNothing","success":true}}}' |
        Set-Content -LiteralPath $pipeline -Encoding utf8
    $null = Invoke-Host @('validate-pipeline', $pipeline, $repository) 0
    $invalid = Join-Path $tempDirectory 'invalid.json'
    '{"schemaVersion":999,"tasks":{}}' | Set-Content -LiteralPath $invalid -Encoding utf8
    $null = Invoke-Host @('validate-pipeline', $invalid, $repository) 1

    $queuePath = Join-Path $tempDirectory 'queue.json'
    $task = @{ id = 'mail-collection'; settings = @{ definitionPath = $pipeline } }
    @{ tasks = @($task, $task) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $queuePath -Encoding utf8
    $userDirectory = Join-Path $tempDirectory 'user'
    $result = Invoke-Host @('queue-run', $fakeExecutable, 'phase5-headless', $queuePath, $repository, $userDirectory) 0
    if (-not $result.success -or $result.result.SucceededTasks -ne 2 -or $result.result.TotalTasks -ne 2) {
        throw 'The independent host did not execute the full native queue.'
    }
    $missing = @{ id = 'mail-collection'; settings = @{ definitionPath = (Join-Path $tempDirectory 'missing.json') } }
    @{ tasks = @($missing, $task) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $queuePath -Encoding utf8
    $result = Invoke-Host @('queue-run', $fakeExecutable, 'phase5-headless', $queuePath, $repository, $userDirectory) 1
    if ($result.success -or -not $result.result.Succeeded -or $result.result.FailedTasks -ne 1 -or
        $result.result.SucceededTasks -ne 1 -or $result.result.SkippedTasks -ne 0) {
        throw 'The host failed to distinguish normal queue completion from an individual task failure.'
    }
    Write-Output "Headless commands: $checks scenarios passed (catalog, valid/invalid parser, complete queue, failure continuation/exit code)."
    exit 0
}
finally {
    $env:UMA_FAKE_ADB_DEVICES_SERIAL = $previousSerial
    $resolvedTemp = [IO.Path]::GetFullPath($tempDirectory)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTemp.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedTemp)).StartsWith('uma-headless-check-')) {
        throw "Refusing cleanup outside the generated test directory: $resolvedTemp"
    }
    Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
}
