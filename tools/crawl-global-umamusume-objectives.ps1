param(
    [string]$DatabaseDir = (Join-Path (Get-Location) 'resource\uma\database\global')
)

$ErrorActionPreference = 'Stop'
$GameToraBaseUrl = 'https://gametora.com/umamusume/characters'
$UserAgent = 'UmamusumeAss career objective crawler/1.0'

if (-not (Test-Path -LiteralPath $DatabaseDir -PathType Container)) {
    throw "Database directory was not found: $DatabaseDir"
}

$traineesPath = Join-Path $DatabaseDir 'trainees.json'
$indexesPath = Join-Path $DatabaseDir 'indexes.json'
$metaPath = Join-Path $DatabaseDir 'meta.json'
$racesPath = Join-Path $DatabaseDir 'races.json'

$trainees = @(Get-Content -Raw -LiteralPath $traineesPath | ConvertFrom-Json)
if ($trainees.Count -eq 0) {
    throw 'The existing trainees.json is empty.'
}

Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.DefaultRequestHeaders.UserAgent.ParseAdd($UserAgent)

function Get-Page([string[]]$Urls) {
    foreach ($url in $Urls) {
        try {
            $response = $http.GetAsync($url).GetAwaiter().GetResult()
            if (-not $response.IsSuccessStatusCode) {
                continue
            }

            return [pscustomobject]@{
                url = $url
                html = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                status = [int]$response.StatusCode
            }
        } catch {
            # Try the next stable slug variant. The site removes the hyphen in
            # "T.M." for the two currently available TM Opera O pages.
        }
    }

    return [pscustomobject]@{
        url = $null
        html = $null
        status = 0
    }
}

function Html-Decode([string]$Text) {
    if ($null -eq $Text) { return $null }
    return [System.Net.WebUtility]::HtmlDecode($Text)
}

function Clean-Text([string]$Text) {
    if ($null -eq $Text) { return $null }
    $value = [regex]::Replace($Text, '<!--.*?-->', ' ', 'Singleline')
    $value = [regex]::Replace($value, '<[^>]+>', ' ')
    $value = Html-Decode $value
    return [regex]::Replace($value, '\s+', ' ').Trim()
}

function Get-NextData([string]$Html) {
    $match = [regex]::Match(
        $Html,
        '<script id="__NEXT_DATA__" type="application/json">(?<json>.*?)</script>',
        'IgnoreCase,Singleline')
    if (-not $match.Success) {
        throw 'GameTora page did not contain __NEXT_DATA__.'
    }

    return $match.Groups['json'].Value | ConvertFrom-Json
}

function Get-ObjectiveLabels([string]$Html) {
    $start = $Html.IndexOf('Objectives', [StringComparison]::OrdinalIgnoreCase)
    if ($start -lt 0) { return @() }
    $end = $Html.IndexOf('Character Rate Up', $start, [StringComparison]::OrdinalIgnoreCase)
    if ($end -lt 0) { $end = $Html.Length }

    $part = $Html.Substring($start, $end - $start)
    return @(
        [regex]::Matches($part, '<b>(?<value>.*?)</b>', 'IgnoreCase,Singleline') |
            ForEach-Object { Clean-Text $_.Groups['value'].Value }
    )
}

function Get-GameToraSlugCandidates([string]$Slug) {
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add($Slug)
    if ($Slug -match '^(?<id>\d+)-t-m-(?<rest>.+)$') {
        $candidates.Add($Matches['id'] + '-tm-' + $Matches['rest'])
    }
    return @($candidates | Select-Object -Unique)
}

function Get-GradeName([int]$Grade) {
    switch ($Grade) {
        100 { return 'G1' }
        200 { return 'G2' }
        300 { return 'G3' }
        400 { return 'G4' }
        900 { return 'DEBUT' }
        default { return 'UNKNOWN' }
    }
}

function Get-DistanceBand([int]$Distance) {
    if ($Distance -le 1400) { return 'short' }
    if ($Distance -le 1800) { return 'mile' }
    if ($Distance -le 2400) { return 'medium' }
    return 'long'
}

function New-Condition([object]$Objective) {
    return [pscustomobject][ordered]@{
        type = [int]$Objective.cond_type
        id = [int]$Objective.cond_id
        value = [int]$Objective.cond_value
        value_2 = [int]$Objective.cond_value_2
        race_type = [int]$Objective.race_type
        target_type = [int]$Objective.target_type
        race_choice = [int]$Objective.race_choice
        race_choice_details = [int]$Objective.race_choice_details
    }
}

function New-Target([object]$Objective, [int]$RaceCount) {
    $target = [ordered]@{}
    $conditionType = [int]$Objective.cond_type
    $conditionValue = [int]$Objective.cond_value

    if ($conditionType -eq 3) {
        $target.minimum = $conditionValue
    } elseif ($RaceCount -gt 0 -and $conditionValue -eq 0) {
        $target.participation = $true
    } elseif ($RaceCount -gt 0) {
        # GameTora's objective data stores the displayed "N-th or better"
        # requirement as cond_value. Keep the raw condition beside it.
        $target.placement_at_most = $conditionValue
    }

    return [pscustomobject]$target
}

function New-RaceRecord([object]$Race) {
    $grade = [int]$Race.grade
    $distance = [int]$Race.distance
    $record = [ordered]@{
        race_id = [int]$Race.id
        name_en = [string]$Race.name_en
        name_jp = [string]$Race.name_jp
        name_ko = [string]$Race.name_ko
        name_tw = [string]$Race.name_tw
        group = [int]$Race.group
        grade = (Get-GradeName $grade)
        grade_code = $grade
        track_id = [int]$Race.track
        surface = if ([int]$Race.terrain -eq 1) { 'turf' } elseif ([int]$Race.terrain -eq 2) { 'dirt' } else { 'unknown' }
        terrain_code = [int]$Race.terrain
        distance = $distance
        distance_band = (Get-DistanceBand $distance)
        fans_needed = [int]$Race.fans_needed
        fans_gained = [int]$Race.fans_gained
        icon_id = [int]$Race.icon_id
    }
    if ($Race.PSObject.Properties.Name -contains 'url_name' -and $Race.url_name) {
        $record.url_name = [string]$Race.url_name
    }
    return [pscustomobject]$record
}

function New-ObjectiveRecord(
    [object]$Trainee,
    [object]$Objective,
    [string]$RequirementText) {
    $races = if ($null -eq $Objective.races) {
        @()
    } else {
        @($Objective.races | Where-Object { $null -ne $_ -and [int]$_.id -gt 0 })
    }
    $raceIds = @($races | ForEach-Object { ([int]$_.id).ToString([Globalization.CultureInfo]::InvariantCulture) })
    $conditionType = [int]$Objective.cond_type
    $kind = if ($conditionType -eq 3) { 'fans' } elseif ($raceIds.Count -gt 0) { 'race_result' } else { 'condition' }
    $record = [ordered]@{
        objective_id = 'career_' + $Trainee.trainee_id.ToString([Globalization.CultureInfo]::InvariantCulture) + '_' + ([int]$Objective.order).ToString('00')
        order = [int]$Objective.order
        kind = $kind
        turn = [int]$Objective.turn
        race_ids = $raceIds
        target = New-Target $Objective $raceIds.Count
        condition = New-Condition $Objective
        requirement_text = $RequirementText
    }
    if ($Objective.PSObject.Properties.Name -contains 'turns_break') {
        $record.turns_after_previous = [int]$Objective.turns_break
    }
    return [pscustomobject]$record
}

function Copy-WithProperty($Object, [string]$Name, $Value) {
    $copy = [ordered]@{}
    foreach ($property in $Object.PSObject.Properties) {
        $copy[$property.Name] = $property.Value
    }
    $copy[$Name] = $Value
    return [pscustomobject]$copy
}

$crawlStarted = (Get-Date).ToUniversalTime().ToString('o')
$updatedTrainees = New-Object System.Collections.Generic.List[object]
$raceById = New-Object 'System.Collections.Generic.Dictionary[int,object]'
$traineeByRace = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[int]]'
$objectiveById = New-Object 'System.Collections.Generic.Dictionary[string,int]'
$failures = New-Object System.Collections.Generic.List[string]

for ($i = 0; $i -lt $trainees.Count; $i++) {
    $trainee = $trainees[$i]
    $candidateUrls = @(Get-GameToraSlugCandidates $trainee.slug | ForEach-Object { $GameToraBaseUrl + '/' + $_ })
    $page = Get-Page $candidateUrls
    if ($page.status -ne 200) {
        $failures.Add("$($trainee.trainee_id) $($trainee.slug): GameTora page was not found.")
        continue
    }

    try {
        $data = Get-NextData $page.html
        $pageProps = $data.props.pageProps
        $item = $pageProps.itemData
        if ([int]$item.card_id -ne [int]$trainee.trainee_id) {
            throw "GameTora card ID $($item.card_id) did not match trainee ID $($trainee.trainee_id)."
        }

        $objectiveData = @($pageProps.objectiveData)
        $labels = @(Get-ObjectiveLabels $page.html)
        if ($objectiveData.Count -eq 0) {
            throw 'GameTora page did not contain objective data.'
        }
        if ($labels.Count -ne $objectiveData.Count) {
            throw "Objective label count $($labels.Count) did not match data count $($objectiveData.Count)."
        }

        $objectives = New-Object System.Collections.Generic.List[object]
        for ($objectiveIndex = 0; $objectiveIndex -lt $objectiveData.Count; $objectiveIndex++) {
            $objective = $objectiveData[$objectiveIndex]
            $record = New-ObjectiveRecord $trainee $objective $labels[$objectiveIndex]
            if (-not $objectiveById.TryAdd($record.objective_id, [int]$trainee.trainee_id)) {
                throw "Duplicate objective ID '$($record.objective_id)'."
            }
            $objectiveRaces = if ($null -eq $objective.races) {
                @()
            } else {
                @($objective.races | Where-Object { $null -ne $_ -and [int]$_.id -gt 0 })
            }
            foreach ($race in $objectiveRaces) {
                $raceRecord = New-RaceRecord $race
                if ($raceById.ContainsKey($raceRecord.race_id)) {
                    $existing = $raceById[$raceRecord.race_id]
                    if (($existing | ConvertTo-Json -Depth 10 -Compress) -ne ($raceRecord | ConvertTo-Json -Depth 10 -Compress)) {
                        throw "Race ID $($raceRecord.race_id) has conflicting definitions."
                    }
                } else {
                    $raceById.Add($raceRecord.race_id, $raceRecord)
                }

                $raceKey = $raceRecord.race_id.ToString([Globalization.CultureInfo]::InvariantCulture)
                if (-not $traineeByRace.ContainsKey($raceKey)) {
                    $traineeByRace.Add($raceKey, [System.Collections.Generic.List[int]]::new())
                }
                if (-not $traineeByRace[$raceKey].Contains([int]$trainee.trainee_id)) {
                    $traineeByRace[$raceKey].Add([int]$trainee.trainee_id)
                }
            }
            $objectives.Add($record)
        }

        $updatedTrainees.Add((Copy-WithProperty $trainee 'career_objectives' $objectives.ToArray()))
        $updatedTrainees[$updatedTrainees.Count - 1] = Copy-WithProperty $updatedTrainees[$updatedTrainees.Count - 1] 'career_objectives_source_url' $page.url
    } catch {
        $failures.Add("$($trainee.trainee_id) $($trainee.slug): $($_.Exception.Message)")
    }

    Write-Progress -Activity 'Crawling GameTora career objectives' -Status "$($i + 1) / $($trainees.Count)" -PercentComplete ([int](100 * ($i + 1) / $trainees.Count))
}
Write-Progress -Activity 'Crawling GameTora career objectives' -Completed

if ($failures.Count -gt 0) {
    throw "Career objective crawl failed for $($failures.Count) trainee(s):`n$($failures -join "`n")"
}
if ($updatedTrainees.Count -ne $trainees.Count) {
    throw "Expected $($trainees.Count) updated trainees, got $($updatedTrainees.Count)."
}

$races = @($raceById.Values | Sort-Object race_id)
$races | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $racesPath -Encoding UTF8
$updatedTrainees.ToArray() | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $traineesPath -Encoding UTF8

$indexes = Get-Content -Raw -LiteralPath $indexesPath | ConvertFrom-Json
$indexCopy = [ordered]@{}
foreach ($property in $indexes.PSObject.Properties) {
    $indexCopy[$property.Name] = $property.Value
}
$raceIndex = [ordered]@{}
foreach ($race in $races) {
    $raceIndex[$race.race_id.ToString([Globalization.CultureInfo]::InvariantCulture)] = [int]$race.race_id
}
$traineeRaceIndex = [ordered]@{}
foreach ($key in ($traineeByRace.Keys | Sort-Object { [int]$_ })) {
    $traineeRaceIndex[$key] = @($traineeByRace[$key] | Sort-Object)
}
$objectiveIndex = [ordered]@{}
foreach ($key in ($objectiveById.Keys | Sort-Object)) {
    $objectiveIndex[$key] = [int]$objectiveById[$key]
}
$indexCopy['race_by_id'] = [pscustomobject]$raceIndex
$indexCopy['trainee_by_race_id'] = [pscustomobject]$traineeRaceIndex
$indexCopy['career_objective_by_id'] = [pscustomobject]$objectiveIndex
[pscustomobject]$indexCopy | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $indexesPath -Encoding UTF8

$meta = Get-Content -Raw -LiteralPath $metaPath | ConvertFrom-Json
$metaCopy = [ordered]@{}
foreach ($property in $meta.PSObject.Properties) {
    $metaCopy[$property.Name] = $property.Value
}
$metaCopy['career_objective_source'] = [pscustomobject][ordered]@{
    source_name = 'GameTora Global Character Database'
    source_url = 'https://gametora.com/umamusume/characters'
    source_type = 'unofficial-community-site'
    region = 'global'
    crawled_at_utc = $crawlStarted
    counts = [pscustomobject][ordered]@{
        trainees = $updatedTrainees.Count
        objectives = $objectiveById.Count
        races = $races.Count
    }
    notes = @(
        'Career objectives are extracted from each character page __NEXT_DATA__.objectiveData payload.',
        'The existing Umamusume.run character records remain the source for base stats, aptitudes and images.',
        'Race entities are deduplicated by the GameTora race ID and stored in races.json.'
    )
}
[pscustomobject]$metaCopy | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $metaPath -Encoding UTF8

Write-Output ("Saved {0} trainee objective chains, {1} objectives and {2} races to {3}" -f $updatedTrainees.Count, $objectiveById.Count, $races.Count, $DatabaseDir)
