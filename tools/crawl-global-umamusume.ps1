param(
    [string]$OutDir = (Join-Path (Get-Location) 'resource\uma\database\global'),
    [switch]$SkipSupportCardImageRefresh
)

$ErrorActionPreference = 'Stop'
$BaseUrl = 'https://www.umamusume.run'
$UserAgent = 'UmamusumeAss data snapshot crawler/1.0'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Net.Http
$Http = New-Object System.Net.Http.HttpClient
$Http.DefaultRequestHeaders.UserAgent.ParseAdd($UserAgent)

function Get-Page([string]$Path) {
    $url = if ($Path -match '^https?://') { $Path } else { $BaseUrl + $Path }
    try {
        $bytes = $Http.GetByteArrayAsync($url).GetAwaiter().GetResult()
        return [pscustomobject]@{
            path = $Path
            url = $url
            status = 200
            html = [Text.Encoding]::UTF8.GetString($bytes)
            error = $null
        }
    } catch {
        return [pscustomobject]@{
            path = $Path
            url = $url
            status = 0
            html = $null
            error = $_.Exception.Message
        }
    }
}

function Get-Pages([string[]]$Paths, [int]$BatchSize = 4) {
    $results = New-Object System.Collections.Generic.List[object]
    for ($offset = 0; $offset -lt $Paths.Count; $offset += $BatchSize) {
        $end = [Math]::Min($offset + $BatchSize, $Paths.Count)
        $tasks = New-Object System.Collections.Generic.List[object]
        $batchPaths = New-Object System.Collections.Generic.List[string]
        for ($i = $offset; $i -lt $end; $i++) {
            $batchPaths.Add($Paths[$i])
            $tasks.Add($Http.GetByteArrayAsync($BaseUrl + $Paths[$i]))
        }
        for ($i = 0; $i -lt $tasks.Count; $i++) {
            try {
                $bytes = $tasks[$i].GetAwaiter().GetResult()
                $results.Add([pscustomobject]@{
                    path = $batchPaths[$i]
                    url = $BaseUrl + $batchPaths[$i]
                    status = 200
                    html = [Text.Encoding]::UTF8.GetString($bytes)
                    error = $null
                })
            } catch {
                $results.Add([pscustomobject]@{
                    path = $batchPaths[$i]
                    url = $BaseUrl + $batchPaths[$i]
                    status = 0
                    html = $null
                    error = $_.Exception.Message
                })
            }
        }
        Write-Progress -Activity 'Crawling Umamusume.run' -Status "$end / $($Paths.Count) detail pages" -PercentComplete ([int](100 * $end / $Paths.Count))
    }
    Write-Progress -Activity 'Crawling Umamusume.run' -Completed
    return $results.ToArray()
}

function Html-Decode([string]$Text) {
    if ($null -eq $Text) { return $null }
    return [System.Net.WebUtility]::HtmlDecode($Text)
}

function Clean-Text([string]$Text) {
    if ($null -eq $Text) { return $null }
    $value = Html-Decode $Text
    $value = [regex]::Replace($value, '<[^>]+>', ' ')
    $value = Html-Decode $value
    return [regex]::Replace($value, '\s+', ' ').Trim()
}

function Get-Attr([string]$Attrs, [string]$Name) {
    $pattern = $Name + '\s*=\s*"(?<value>.*?)"'
    $match = [regex]::Match($Attrs, $pattern, 'IgnoreCase,Singleline')
    if ($match.Success) { return Html-Decode $match.Groups['value'].Value }
    return $null
}

function Get-FirstText([string]$Html, [string]$Pattern) {
    $match = [regex]::Match($Html, $Pattern, 'IgnoreCase,Singleline')
    if ($match.Success) { return Clean-Text $match.Groups['value'].Value }
    return $null
}

function Get-FirstUrl([string]$Html, [string]$Pattern) {
    $match = [regex]::Match($Html, $Pattern, 'IgnoreCase,Singleline')
    if ($match.Success) { return Html-Decode $match.Groups['value'].Value }
    return $null
}

function Save-Json([string]$Name, $Value) {
    $path = Join-Path $OutDir $Name
    $Value | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $path -Encoding UTF8
}

function Parse-StatPairs([string]$Html) {
    $pattern = '<div[^>]*>\s*<div class="h-2[^>]*></div>\s*<div class="text-sm[^>]*capitalize">\s*(?<name>[^<]+)\s*</div>\s*<div class="text-xl[^>]*>\s*(?<value>[^<]+)\s*</div>'
    $items = New-Object System.Collections.Generic.List[object]
    foreach ($match in [regex]::Matches($Html, $pattern, 'IgnoreCase,Singleline')) {
        $name = (Clean-Text $match.Groups['name'].Value).ToLowerInvariant()
        $raw = Clean-Text $match.Groups['value'].Value
        $number = $null
        if ($raw -match '^-?\d+(?:\.\d+)?') { $number = [decimal]$Matches[0] }
        $items.Add([pscustomobject]@{ name = $name; value = $number; display = $raw })
    }
    return $items.ToArray()
}

function Parse-Aptitudes([string]$Html) {
    $groups = [ordered]@{ surface = @{}; distance = @{}; strategy = @{} }
    $sectionNames = @(
        @{ key = 'surface'; label = 'Surface Aptitudes' },
        @{ key = 'distance'; label = 'Distance Aptitudes' },
        @{ key = 'strategy'; label = 'Strategy Aptitudes' }
    )
    foreach ($section in $sectionNames) {
        $start = $Html.IndexOf($section.label)
        if ($start -lt 0) { continue }
        $next = $Html.Length
        foreach ($other in $sectionNames) {
            if ($other.key -eq $section.key) { continue }
            $candidate = $Html.IndexOf($other.label, $start + $section.label.Length)
            if ($candidate -ge 0 -and $candidate -lt $next) { $next = $candidate }
        }
        $part = $Html.Substring($start, $next - $start)
        $pairPattern = '<span class="text-sm text-gray-600">\s*(?<name>[^<]+)\s*</span>\s*<span[^>]*>\s*(?<value>[SABCDEFG])\s*</span>'
        foreach ($match in [regex]::Matches($part, $pairPattern, 'IgnoreCase,Singleline')) {
            $groups[$section.key][(Clean-Text $match.Groups['name'].Value).ToLowerInvariant()] = (Clean-Text $match.Groups['value'].Value)
        }
    }
    return [pscustomobject]$groups
}

function Parse-Effects([string]$Html) {
    $start = $Html.IndexOf('Training Effects')
    if ($start -lt 0) { return @() }
    $end = $Html.IndexOf('Key Effects (Lv50)', $start)
    if ($end -lt 0) { $end = $Html.Length }
    $part = $Html.Substring($start, $end - $start)
    $items = New-Object System.Collections.Generic.List[object]
    foreach ($row in [regex]::Matches($part, '<tr[^>]*>(?<cells>.*?)</tr>', 'IgnoreCase,Singleline')) {
        $cells = @([regex]::Matches($row.Groups['cells'].Value, '<(?:td|th)[^>]*>(?<cell>.*?)</(?:td|th)>', 'IgnoreCase,Singleline') | ForEach-Object { Clean-Text $_.Groups['cell'].Value })
        if ($cells.Count -ge 7 -and $cells[0] -ne 'Effect') {
            $items.Add([pscustomobject][ordered]@{
                effect = $cells[0]
                initial = $cells[1]
                lv10 = $cells[2]
                lv20 = $cells[3]
                lv30 = $cells[4]
                lv40 = $cells[5]
                lv50 = $cells[6]
            })
        }
    }
    return $items.ToArray()
}

$crawlStarted = (Get-Date).ToUniversalTime().ToString('o')
$charactersPage = Get-Page '/database/characters'
$supportPage = Get-Page '/database/support-cards'
if ($charactersPage.status -ne 200 -or $supportPage.status -ne 200) {
    throw 'Global database index pages could not be fetched.'
}

$characterRows = New-Object System.Collections.Generic.List[object]
$characterPattern = '<a\s+href="(?<href>/characters/(?<slug>[^"]+))"\s+class="character-card[^"]*"(?<attrs>[^>]*)>(?<body>.*?)</a>'
foreach ($match in [regex]::Matches($charactersPage.html, $characterPattern, 'IgnoreCase,Singleline')) {
    $attrs = $match.Groups['attrs'].Value
    $body = $match.Groups['body'].Value
    $slug = $match.Groups['slug'].Value
    $idText = ($slug -split '-', 2)[0]
    if ($idText -notmatch '^\d+$') { continue }
    $statPairs = @(Parse-StatPairs $body | Select-Object -First 5)
    $stats = [ordered]@{}
    foreach ($stat in $statPairs) { $stats[$stat.name] = $stat.value }
    $strategies = @()
    $strategyText = Get-Attr $attrs 'data-strategies'
    if ($strategyText) { $strategies = @($strategyText -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    $characterRows.Add([pscustomobject][ordered]@{
        trainee_id = [int]$idText
        base_character_id = [int]([int]$idText / 100)
        name_en = Get-Attr $attrs 'data-name'
        name_jp = Get-Attr $attrs 'data-jp-name'
        rarity = [int](Get-Attr $attrs 'data-rarity')
        strategies = $strategies
        base_stats = [pscustomobject]$stats
        image_url = $BaseUrl + (Get-FirstUrl $body 'src="(?<value>/images/characters/[^" ]+)"')
        detail_url = $BaseUrl + $match.Groups['href'].Value
        region = 'global'
        available = $true
        slug = $slug
    })
}

$supportRows = New-Object System.Collections.Generic.List[object]
$supportPattern = '<a\s+href="(?<href>/supports/(?<slug>[^"]+))"\s+class="card-item[^"]*"(?<attrs>[^>]*)>(?<body>.*?)</a>'
foreach ($match in [regex]::Matches($supportPage.html, $supportPattern, 'IgnoreCase,Singleline')) {
    $attrs = $match.Groups['attrs'].Value
    $body = $match.Groups['body'].Value
    $slug = $match.Groups['slug'].Value
    $idText = ($slug -split '-', 2)[0]
    if ($idText -notmatch '^\d+$') { continue }
    $supportRows.Add([pscustomobject][ordered]@{
        support_card_id = [int]$idText
        name_en = Get-FirstText $body '<h3[^>]*>(?<value>.*?)</h3>'
        featured_character_name_en = Get-FirstText $body '<p[^>]*>(?<value>.*?)</p>'
        featured_character_id = $null
        rarity = Get-Attr $attrs 'data-rarity'
        type = Get-Attr $attrs 'data-type'
        image_url = $BaseUrl + (Get-FirstUrl $body 'src="(?<value>/images/supports/[^" ]+)"')
        detail_url = $BaseUrl + $match.Groups['href'].Value
        region = 'global'
        available = $true
        slug = $slug
    })
}

$baseCharacterByName = @{}
foreach ($row in $characterRows) {
    if (-not [string]::IsNullOrWhiteSpace($row.name_en)) {
        $baseCharacterByName[$row.name_en.Trim().ToLowerInvariant()] = $row.base_character_id
    }
}
foreach ($row in $supportRows) {
    $nameKey = if ($row.featured_character_name_en) {
        $row.featured_character_name_en.Trim().ToLowerInvariant()
    } else {
        ''
    }
    if ($nameKey -and $baseCharacterByName.ContainsKey($nameKey)) {
        $row.featured_character_id = [int]$baseCharacterByName[$nameKey]
    }
}

# Umamusume.run's Global support-card index can lag behind the live Global
# release catalog. Supplement missing card identities from GachaData while
# retaining Umamusume.run as the detailed-effects source for cards it knows.
$supportCatalogUrl = 'https://gacha-data.com/umamusume/support_cards/'
$supportCatalogPage = Get-Page $supportCatalogUrl
$supportCatalogRows = New-Object System.Collections.Generic.List[object]
if ($supportCatalogPage.status -eq 200) {
    foreach ($rowMatch in [regex]::Matches($supportCatalogPage.html, '<tr[^>]*>(?<body>.*?)</tr>', 'IgnoreCase,Singleline')) {
        $body = $rowMatch.Groups['body'].Value
        $idMatch = [regex]::Match($body, '/support_cards/(?<id>\d+)\.webp', 'IgnoreCase')
        if (-not $idMatch.Success) { continue }
        $cells = @([regex]::Matches($body, '<td[^>]*>(?<value>.*?)</td>', 'IgnoreCase,Singleline') | ForEach-Object { Clean-Text $_.Groups['value'].Value })
        if ($cells.Count -lt 9) { continue }
        $name = Get-FirstText $body '<span class="nm">(?<value>.*?)</span>'
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $supportCatalogRows.Add([pscustomobject][ordered]@{
            support_card_id = [int]$idMatch.Groups['id'].Value
            name_en = $name
            featured_character_name_en = $cells[3]
            rarity_label = $cells[1]
            type = $cells[2]
            release_date = $cells[4]
            unique_effect = $cells[5]
            unique_effect_unlock_level = $cells[6]
            unique_effect_values = $cells[7]
            hint_skills = @($cells[8] -split ',\s*' | Where-Object { $_ })
            image_url = 'https://gacha-data.com/assets/img/umamusume/ds/support_cards/' + $idMatch.Groups['id'].Value + '.webp'
        })
    }
}
if ($supportCatalogPage.status -ne 200 -or $supportCatalogRows.Count -lt $supportRows.Count) {
    throw "Global support-card catalog could not be fetched or parsed safely (primary: $($supportRows.Count), catalog: $($supportCatalogRows.Count))."
}

$knownSupportCardIds = @{}
$supportCatalogById = @{}
foreach ($row in $supportCatalogRows) { $supportCatalogById[[string]$row.support_card_id] = $row }
foreach ($row in $supportRows) { $knownSupportCardIds[[string]$row.support_card_id] = $true }
$supportCardsRepairedFromCatalog = 0
foreach ($row in $supportRows) {
    $catalogRow = $supportCatalogById[[string]$row.support_card_id]
    if ($null -eq $catalogRow) { continue }
    $rowRepaired = $false
    if ([string]::IsNullOrWhiteSpace($row.name_en)) { $row.name_en = $catalogRow.name_en; $rowRepaired = $true }
    if ([string]::IsNullOrWhiteSpace($row.featured_character_name_en)) { $row.featured_character_name_en = $catalogRow.featured_character_name_en; $rowRepaired = $true }
    if ([string]::IsNullOrWhiteSpace($row.rarity)) {
        $row.rarity = switch ($catalogRow.rarity_label) { 'R' { '1' } 'SR' { '2' } 'SSR' { '3' } default { $catalogRow.rarity_label } }
        $rowRepaired = $true
    }
    if ([string]::IsNullOrWhiteSpace($row.type)) { $row.type = $catalogRow.type; $rowRepaired = $true }
    if ([string]::IsNullOrWhiteSpace($row.image_url)) { $row.image_url = $catalogRow.image_url; $rowRepaired = $true }
    if ($null -eq $row.featured_character_id -and $row.featured_character_name_en) {
        $characterKey = $row.featured_character_name_en.Trim().ToLowerInvariant()
        if ($baseCharacterByName.ContainsKey($characterKey)) { $row.featured_character_id = [int]$baseCharacterByName[$characterKey]; $rowRepaired = $true }
    }
    if ($rowRepaired) { $supportCardsRepairedFromCatalog++ }
}
$supplementalSupportCards = New-Object System.Collections.Generic.List[object]
foreach ($row in $supportCatalogRows) {
    $idKey = [string]$row.support_card_id
    if ($knownSupportCardIds.ContainsKey($idKey)) { continue }

    $rarity = switch ($row.rarity_label) {
        'R' { '1' }
        'SR' { '2' }
        'SSR' { '3' }
        default { $row.rarity_label }
    }
    $characterKey = if ($row.featured_character_name_en) { $row.featured_character_name_en.Trim().ToLowerInvariant() } else { '' }
    $featuredCharacterId = $null
    if ($characterKey -and $baseCharacterByName.ContainsKey($characterKey)) {
        $featuredCharacterId = [int]$baseCharacterByName[$characterKey]
    }
    $supplementalSupportCards.Add([pscustomobject][ordered]@{
        support_card_id = $row.support_card_id
        name_en = $row.name_en
        featured_character_id = $featuredCharacterId
        featured_character_name_en = $row.featured_character_name_en
        rarity = $rarity
        type = $row.type
        image_url = $row.image_url
        detail_url = $null
        training_effects = @()
        key_effects = @()
        source_status = 200
        source_error = $null
        region = 'global'
        available = $true
        slug = $null
        release_date = $row.release_date
        unique_effect = $row.unique_effect
        unique_effect_unlock_level = $row.unique_effect_unlock_level
        unique_effect_values = $row.unique_effect_values
        hint_skills = $row.hint_skills
        source_name = 'GachaData Global support-card catalog'
        source_url = $supportCatalogUrl
    })
    $knownSupportCardIds[$idKey] = $true
}

$characterPaths = @($characterRows | ForEach-Object { '/characters/' + $_.slug })
$supportPaths = @($supportRows | ForEach-Object { '/supports/' + $_.slug })
$characterDetails = @(Get-Pages $characterPaths)
$supportDetails = @(Get-Pages $supportPaths)

$trainees = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $characterRows.Count; $i++) {
    $row = $characterRows[$i]
    $page = $characterDetails | Where-Object { $_.path -eq ('/characters/' + $row.slug) } | Select-Object -First 1
    $detail = [ordered]@{
        trainee_id = $row.trainee_id
        base_character_id = $row.base_character_id
        name_en = $row.name_en
        name_jp = $row.name_jp
        rarity = $row.rarity
        strategies = $row.strategies
        base_stats = $row.base_stats
        detail_stats = @()
        growth_rates = @()
        aptitudes = [pscustomobject]@{ surface = @{}; distance = @{}; strategy = @{} }
        image_url = $row.image_url
        detail_url = $row.detail_url
        source_status = $page.status
        source_error = $page.error
        region = 'global'
        available = $true
        slug = $row.slug
    }
    if ($page.status -eq 200) {
        $statsStart = $page.html.IndexOf('Base Stats')
        $aptitudeStart = $page.html.IndexOf('<!-- Aptitudes Sidebar -->')
        if ($statsStart -ge 0 -and $aptitudeStart -gt $statsStart) {
            $statsPart = $page.html.Substring($statsStart, $aptitudeStart - $statsStart)
            $pairs = @(Parse-StatPairs $statsPart)
            $basePairs = @($pairs | Select-Object -First 15)
            $growthPairs = @($pairs | Select-Object -Skip 15 -First 5)
            $detailStats = New-Object System.Collections.Generic.List[object]
            for ($p = 0; $p -lt $basePairs.Count; $p += 5) {
                $group = @($basePairs | Select-Object -Skip $p -First 5)
                if ($group.Count -eq 5) {
                    $statsObject = [ordered]@{}
                    foreach ($stat in $group) { $statsObject[$stat.name] = $stat.value }
                    $detailStats.Add([pscustomobject]@{ star_level = 3 + [int]($p / 5); stats = [pscustomobject]$statsObject })
                }
            }
            $detail.detail_stats = $detailStats.ToArray()
            $growth = [ordered]@{}
            foreach ($stat in $growthPairs) { $growth[$stat.name] = $stat.value }
            $detail.growth_rates = [pscustomobject]$growth
            if ($detailStats.Count -gt 0) { $detail.base_stats = $detailStats[0].stats }
        }
        $detail.aptitudes = Parse-Aptitudes $page.html
        $detail.image_url = $BaseUrl + (Get-FirstUrl $page.html 'src="(?<value>/images/characters/[^" ]+)"')
        $detail.name_en = Get-FirstText $page.html '<h1[^>]*>(?<value>.*?)</h1>'
        $detail.name_jp = Get-FirstText $page.html '<h1[^>]*>.*?</h1>\s*<p[^>]*>(?<value>.*?)</p>'
    }
    $trainees.Add([pscustomobject]$detail)
}

$supportCards = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $supportRows.Count; $i++) {
    $row = $supportRows[$i]
    $page = $supportDetails | Where-Object { $_.path -eq ('/supports/' + $row.slug) } | Select-Object -First 1
    $detail = [ordered]@{
        support_card_id = $row.support_card_id
        name_en = $row.name_en
        featured_character_id = $row.featured_character_id
        featured_character_name_en = $row.featured_character_name_en
        rarity = $row.rarity
        type = $row.type
        image_url = $row.image_url
        detail_url = $row.detail_url
        training_effects = @()
        key_effects = @()
        source_status = $page.status
        source_error = $page.error
        region = 'global'
        available = $true
        slug = $row.slug
    }
    if ($page.status -eq 200) {
        $detail.name_en = Get-FirstText $page.html '<h1[^>]*>(?<value>.*?)</h1>'
        $detail.featured_character_name_en = Get-FirstText $page.html '<h1[^>]*>.*?</h1>\s*<p[^>]*>(?<value>.*?)</p>'
        $detail.image_url = $BaseUrl + (Get-FirstUrl $page.html 'src="(?<value>/images/supports/[^" ]+)"')
        $detail.training_effects = Parse-Effects $page.html
        $keyStart = $page.html.IndexOf('Key Effects (Lv50)')
        if ($keyStart -ge 0) {
            $keyEnd = $page.html.IndexOf('<div class="mt-6 pt-6 border-t">', $keyStart)
            if ($keyEnd -lt 0) { $keyEnd = [Math]::Min($page.html.Length, $keyStart + 8000) }
            $keyPart = $page.html.Substring($keyStart, $keyEnd - $keyStart)
            $keyItems = New-Object System.Collections.Generic.List[object]
            $keyPattern = '<div class="flex justify-between items-center">\s*<span[^>]*>(?<name>.*?)</span>\s*<span[^>]*>(?<value>.*?)</span>\s*</div>'
            foreach ($keyMatch in [regex]::Matches($keyPart, $keyPattern, 'IgnoreCase,Singleline')) {
                $keyItems.Add([pscustomobject]@{
                    effect = Clean-Text $keyMatch.Groups['name'].Value
                    value = Clean-Text $keyMatch.Groups['value'].Value
                })
            }
            $detail.key_effects = $keyItems.ToArray()
        }
    }
    $supportCards.Add([pscustomobject]$detail)
}
foreach ($card in $supplementalSupportCards) { $supportCards.Add($card) }

# Rebuild the complete snapshot in one consistent order before writing it.
# This also keeps the generated support-card indexes in the same order.
$supportCards = @($supportCards.ToArray() | Sort-Object { [int]$_.support_card_id })

$baseCharacters = New-Object System.Collections.Generic.List[object]
foreach ($group in ($trainees.ToArray() | Group-Object base_character_id | Sort-Object Name)) {
    $first = $group.Group | Select-Object -First 1
    $baseCharacters.Add([pscustomobject][ordered]@{
        base_character_id = [int]$group.Name
        name_en = $first.name_en
        name_jp = $first.name_jp
        trainee_ids = @($group.Group | ForEach-Object { [int]$_.trainee_id } | Sort-Object)
        region = 'global'
        available = $true
    })
}

function Add-IndexValue([hashtable]$Index, [string]$Key, [int]$Value) {
    if ([string]::IsNullOrWhiteSpace($Key)) { return }
    $normalized = $Key.Trim().ToLowerInvariant()
    if (-not $Index.ContainsKey($normalized)) {
        $Index[$normalized] = New-Object System.Collections.Generic.List[int]
    }
    if (-not $Index[$normalized].Contains($Value)) {
        $Index[$normalized].Add($Value)
    }
}

$traineeById = [ordered]@{}
$traineeByName = @{}
$traineeByBaseCharacter = @{}
foreach ($trainee in $trainees) {
    $traineeById[[string]$trainee.trainee_id] = [int]$trainee.trainee_id
    Add-IndexValue $traineeByName $trainee.name_en $trainee.trainee_id
    Add-IndexValue $traineeByName $trainee.name_jp $trainee.trainee_id
    Add-IndexValue $traineeByBaseCharacter ([string]$trainee.base_character_id) $trainee.trainee_id
}

$supportCardById = [ordered]@{}
$supportCardByName = @{}
$supportCardByType = @{}
$supportCardByCharacter = @{}
foreach ($card in $supportCards) {
    $supportCardById[[string]$card.support_card_id] = [int]$card.support_card_id
    Add-IndexValue $supportCardByName $card.name_en $card.support_card_id
    Add-IndexValue $supportCardByType $card.type $card.support_card_id
    if ($null -ne $card.featured_character_id) {
        Add-IndexValue $supportCardByCharacter ([string]$card.featured_character_id) $card.support_card_id
    }
}

$indexes = [pscustomobject][ordered]@{
    trainee_by_id = $traineeById
    trainee_by_name = $traineeByName
    trainee_by_base_character = $traineeByBaseCharacter
    support_card_by_id = $supportCardById
    support_card_by_name = $supportCardByName
    support_card_by_type = $supportCardByType
    support_card_by_featured_character = $supportCardByCharacter
}

$source = [ordered]@{
    source_name = 'Umamusume.run Global Database'
    source_url = $BaseUrl
    source_type = 'unofficial-community-site'
    region = 'global'
    crawled_at_utc = $crawlStarted
    index_pages = @(
        [pscustomobject]@{ name = 'trainees'; url = $BaseUrl + '/database/characters'; status = $charactersPage.status; html_bytes = [Text.Encoding]::UTF8.GetByteCount($charactersPage.html) },
        [pscustomobject]@{ name = 'support_cards'; url = $BaseUrl + '/database/support-cards'; status = $supportPage.status; html_bytes = [Text.Encoding]::UTF8.GetByteCount($supportPage.html) },
        [pscustomobject]@{ name = 'support_cards_supplement'; url = $supportCatalogUrl; status = $supportCatalogPage.status; html_bytes = if ($supportCatalogPage.html) { [Text.Encoding]::UTF8.GetByteCount($supportCatalogPage.html) } else { 0 } }
    )
    counts = [pscustomobject]@{ base_characters = $baseCharacters.Count; trainees = $trainees.Count; support_cards = $supportCards.Count; support_cards_from_umamusume_run = $supportRows.Count; support_cards_supplemented = $supplementalSupportCards.Count; trainee_detail_pages = @($characterDetails | Where-Object status -eq 200).Count; support_detail_pages = @($supportDetails | Where-Object status -eq 200).Count }
    notes = @(
        'This is a Global-version community snapshot, not an official Cygames API. Support-card identities missing from Umamusume.run are supplemented from the GachaData Global catalog.',
        'GachaData fills catalog metadata omitted from a primary support-card row when available.',
        'Supplemental support cards include catalog effects and hinted skills; detailed training-effect tables are unavailable when the primary support detail page does not list a card.',
        'The complete support-card snapshot is overwritten on each crawl and sorted by support_card_id.',
        'Support-card images and cropped selection templates are maintained by tools/crawl-support-card-images.py.',
        'Each Global育成形态 is stored as its own trainee_id entry.'
    )
}

$source.database_counts = [pscustomobject]@{
    support_cards = $supportCards.Count
    support_cards_from_umamusume_run = $supportRows.Count
    support_cards_added_from_gacha_data = $supplementalSupportCards.Count
    support_cards_repaired_from_gacha_data = $supportCardsRepairedFromCatalog
}
$source.support_card_catalog_source = [pscustomobject][ordered]@{
    source_name = 'GachaData Global support-card catalog'
    source_url = $supportCatalogUrl
    source_type = 'unofficial-community-site'
    region = 'global'
    crawled_at_utc = $crawlStarted
    cards_total = $supportCards.Count
    cards_added = $supplementalSupportCards.Count
}
$existingMetaPath = Join-Path $OutDir 'meta.json'
if (Test-Path -LiteralPath $existingMetaPath) {
    $existingMeta = Get-Content -LiteralPath $existingMetaPath -Raw | ConvertFrom-Json
    if ($null -ne $existingMeta.career_objective_source) {
        $source.career_objective_source = $existingMeta.career_objective_source
    }
}

Save-Json 'trainees.json' $trainees.ToArray()
Save-Json 'support_cards.json' $supportCards.ToArray()
Save-Json 'base_characters.json' $baseCharacters.ToArray()
Save-Json 'indexes.json' $indexes
Save-Json 'meta.json' ([pscustomobject]$source)

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$defaultDatabaseDirectory = Join-Path $repositoryRoot 'resource\uma\database\global'
$resolvedOutDir = [System.IO.Path]::GetFullPath($OutDir)
if (-not $SkipSupportCardImageRefresh -and
    $resolvedOutDir.Equals(
        [System.IO.Path]::GetFullPath($defaultDatabaseDirectory),
        [System.StringComparison]::OrdinalIgnoreCase)) {
    $imageCrawler = Join-Path $PSScriptRoot 'crawl-support-card-images.py'
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $python) {
        throw 'Python is required to refresh support-card images and selection templates.'
    }

    $imageDirectory = Join-Path $repositoryRoot 'resource\uma\assets\images\global\support_cards'
    $templateDirectory = Join-Path $repositoryRoot 'resource\uma\assets\templates\global\support_cards'
    & $python.Source $imageCrawler `
        --database-dir $resolvedOutDir `
        --output-dir $imageDirectory `
        --template-dir $templateDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Support-card image/template refresh failed with exit code $LASTEXITCODE."
    }
} elseif (-not $SkipSupportCardImageRefresh) {
    Write-Output 'Custom database output directory: run tools/crawl-support-card-images.py separately to refresh card images and selection templates.'
}

Write-Output ("Saved {0} trainees and {1} support cards to {2}" -f $trainees.Count, $supportCards.Count, $OutDir)
