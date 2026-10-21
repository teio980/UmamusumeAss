param(
    [Parameter(Mandatory)][string]$ReferenceTrace,
    [Parameter(Mandatory)][string]$ActualTrace,
    [string]$ReportPath,
    [double]$ScoreTolerance = 1e-6,
    [int]$PositionTolerance = 1
)

$ErrorActionPreference = 'Stop'
if (-not [double]::IsFinite($ScoreTolerance) -or $ScoreTolerance -lt 0 -or $PositionTolerance -lt 0) {
    throw 'Comparison tolerances must be finite and nonnegative.'
}

function Read-CompletedTrace([string]$path) {
    $matches = [Collections.Generic.List[object]]::new()
    $summary = $null
    foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $path).Path)) {
        $record = $line | ConvertFrom-Json
        if ($record.recordType -eq 'match') { $matches.Add($record) }
        if ($record.recordType -eq 'run_summary') { $summary = $record }
    }
    if ($null -eq $summary -or $summary.status -ne 'completed' -or $summary.failed -ne 0) {
        throw "Trace is incomplete or has failed tests: $path"
    }
    if ($matches.Count -eq 0) { throw "Trace has no match records: $path" }
    return @{ Matches = $matches; Summary = $summary }
}

function Get-InputKey($record) {
    # Polling may repeat the same input a different number of times. Compare
    # distinct inputs and every observed result, excluding output-file paths.
    return ConvertTo-Json -Compress -Depth 8 -InputObject @(
        $record.test, $record.method, $record.screenSha256, $record.templateSha256,
        $record.screenWidth, $record.screenHeight, $record.templateWidth, $record.templateHeight,
        $record.roi, $record.threshold, $record.referenceWidth, $record.referenceHeight, $record.parameters
    )
}

$reference = Read-CompletedTrace $ReferenceTrace
$actual = Read-CompletedTrace $ActualTrace
$expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($record in $reference.Matches) { $expected[(Get-InputKey $record)] = $record }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$added = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$examples = [Collections.Generic.List[object]]::new()
$compared = 0
$mismatches = 0
$maximumScoreError = 0.0
$maximumPositionError = 0
foreach ($record in $actual.Matches) {
    $key = Get-InputKey $record
    if (-not $expected.ContainsKey($key)) { [void]$added.Add($key); continue }
    [void]$seen.Add($key)
    $baseline = $expected[$key]
    $compared++
    $scoreError = [Math]::Abs([double]$baseline.score - [double]$record.score)
    $positionError = 0
    foreach ($axis in @('x', 'y', 'width', 'height')) {
        $positionError = [Math]::Max($positionError, [Math]::Abs([int]$baseline.$axis - [int]$record.$axis))
    }
    $maximumScoreError = [Math]::Max($maximumScoreError, $scoreError)
    $maximumPositionError = [Math]::Max($maximumPositionError, $positionError)
    if ($baseline.found -ne $record.found -or -not [double]::IsFinite($scoreError) -or
        $scoreError -gt $ScoreTolerance -or $positionError -gt $PositionTolerance) {
        $mismatches++
        if ($examples.Count -lt 10) {
            $examples.Add(@{ Test = $record.test; Method = $record.method; ScoreError = $scoreError;
                PositionError = $positionError; ExpectedFound = $baseline.found; ActualFound = $record.found })
        }
    }
}
$report = [ordered]@{
    ReferenceTrace = $ReferenceTrace; ActualTrace = $ActualTrace
    ReferenceMatches = $reference.Matches.Count; ActualMatches = $actual.Matches.Count
    ReferenceDistinctInputs = $expected.Count; Compared = $compared
    ComparedDistinctInputs = $seen.Count; AddedDistinctInputs = $added.Count
    MissingDistinctInputs = $expected.Count - $seen.Count; Mismatches = $mismatches
    MaxScoreError = $maximumScoreError; MaxPositionError = $maximumPositionError
    TestSummary = $actual.Summary; Examples = $examples
}
$json = $report | ConvertTo-Json -Depth 10
if ($ReportPath) { $json | Set-Content -LiteralPath $ReportPath -Encoding utf8 }
Write-Output $json
if ($mismatches -gt 0 -or $report.MissingDistinctInputs -gt 0) { exit 1 }
