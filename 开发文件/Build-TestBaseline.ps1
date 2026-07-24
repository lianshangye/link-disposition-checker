param(
    [string]$SourceCsv = '',
    [string]$CurrentResult = '',
    [string]$CandidateResult = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Text-FromCode([int[]]$Codes) {
    return -join ($Codes | ForEach-Object { [char]$_ })
}

if ([string]::IsNullOrWhiteSpace($SourceCsv)) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $SourceCsv = Get-ChildItem -LiteralPath $desktop -Filter '*.csv' |
        Where-Object { $_.Length -gt 100000 } | Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($CurrentResult)) {
    $resultFiles = Get-ChildItem -LiteralPath $projectRoot -Recurse -Filter '*.csv' |
        Where-Object { $_.DirectoryName -notlike '*test-data*' }
    if (@($resultFiles).Count -eq 0) { throw 'Current result CSV was not found.' }
    $CurrentResult = $resultFiles |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'test-data'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sourceRows = @(Import-Csv -LiteralPath $SourceCsv)
$currentRows = @(Import-Csv -LiteralPath $CurrentResult)
$candidateRows = if ([string]::IsNullOrWhiteSpace($CandidateResult) -or -not (Test-Path -LiteralPath $CandidateResult)) {
    @()
} else {
    @(Import-Csv -LiteralPath $CandidateResult)
}

if ($sourceRows.Count -eq 0 -or @($sourceRows[0].PSObject.Properties).Count -lt 13) {
    throw 'The source CSV does not have the expected 13 columns.'
}
if ($currentRows.Count -eq 0 -or @($currentRows[0].PSObject.Properties).Count -lt 10) {
    throw 'The current result CSV does not have the expected columns.'
}

$verdictRemoved = Text-FromCode @(0x5DF2,0x5931,0x6548)
$verdictAlive = Text-FromCode @(0x4ECD,0x53EF,0x8BBF,0x95EE)
$verdictReview = Text-FromCode @(0x4EBA,0x5DE5,0x590D,0x6838)
$historicalDown = Text-FromCode @(0x4E0B,0x67B6)

function Value-At([object]$Row, [int]$Index) {
    if ($null -eq $Row) { return '' }
    $properties = @($Row.PSObject.Properties)
    if ($properties.Count -le $Index) { return '' }
    return [string]$properties[$Index].Value
}

function Index-ByNumber([object[]]$Rows) {
    $index = @{}
    foreach ($row in $Rows) { $index[(Value-At $row 0)] = $row }
    return $index
}

function Get-SampleRisk([string]$Platform, [string]$Url, [string]$Title) {
    $flags = New-Object System.Collections.Generic.List[string]
    if ($Url -match 'weibo|xueqiu|guba|zhihu|toutiao|dongchedi') { $flags.Add('title-may-be-first-sentence-or-comment') }
    if ($Url -match 'video|douyin|bilibili|haokan|kuaishou|weishi') { $flags.Add('video-content') }
    if ($Url -match 'weixin|zhihu|xiaohongshu|xueqiu') { $flags.Add('login-or-risk-control') }
    if ($Title.Length -gt 80) { $flags.Add('long-title-or-first-sentence') }
    return ($flags -join ';')
}

$currentIndex = Index-ByNumber $currentRows
$candidateIndex = Index-ByNumber $candidateRows
$manifest = foreach ($source in $sourceRows) {
    $number = Value-At $source 0
    $title = Value-At $source 4
    $url = Value-At $source 5
    $author = Value-At $source 7
    $platform = Value-At $source 8
    $historical = Value-At $source 12
    $current = $currentIndex[$number]
    $candidate = $candidateIndex[$number]
    $currentVerdict = Value-At $current 1
    $candidateVerdict = Value-At $candidate 1
    [pscustomobject]@{
        number = $number
        platform = $platform
        title = $title
        author = $author
        url = $url
        historical_disposition = $historical
        baseline_verdict = $currentVerdict
        baseline_evidence = Value-At $current 9
        candidate_verdict = $candidateVerdict
        transition = if ([string]::IsNullOrWhiteSpace($candidateVerdict) -or $candidateVerdict -eq $currentVerdict) { '' } else { "$currentVerdict -> $candidateVerdict" }
        sample_risk = Get-SampleRisk $platform $url $title
    }
}

$manifestPath = Join-Path $OutputDirectory 'baseline-manifest.csv'
$manifest | Export-Csv -LiteralPath $manifestPath -NoTypeInformation -Encoding UTF8

# One deterministic network sample per supplier platform. Prefer unresolved historical
# removals, then resolved removed/alive examples. Historical disposition is not truth.
$samples = foreach ($group in ($manifest | Group-Object platform | Sort-Object Name)) {
    $group.Group | Sort-Object @{ Expression = {
        if ($_.'historical_disposition' -eq $historicalDown -and $_.'baseline_verdict' -eq $verdictReview) { 0 }
        elseif ($_.'baseline_verdict' -eq $verdictRemoved) { 1 }
        elseif ($_.'baseline_verdict' -eq $verdictAlive) { 2 }
        elseif ($_.'baseline_verdict' -eq $verdictReview) { 3 }
        else { 4 }
    }}, @{ Expression = { [int]$_.'number' } } | Select-Object -First 1
}
$samplePath = Join-Path $OutputDirectory 'platform-samples.csv'
$samples | Export-Csv -LiteralPath $samplePath -NoTypeInformation -Encoding UTF8

$confirmedPath = Join-Path $OutputDirectory 'manual-ground-truth.csv'
if (-not (Test-Path -LiteralPath $confirmedPath)) {
    $samples | Select-Object number,platform,title,author,url,
        @{Name='confirmed_verdict';Expression={''}},@{Name='confirmed_at';Expression={''}},@{Name='confirmation_note';Expression={''}} |
        Export-Csv -LiteralPath $confirmedPath -NoTypeInformation -Encoding UTF8
}

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add('Link checker test baseline')
$summary.Add('Generated: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$summary.Add('Source: ' + $SourceCsv)
$summary.Add('Baseline result: ' + $CurrentResult)
$summary.Add('Rows: ' + $manifest.Count)
$summary.Add('Platforms: ' + @($manifest | Group-Object platform).Count)
$summary.Add('Fixed network samples: ' + $samples.Count + ' (one per supplier platform)')
$summary.Add('Note: historical disposition is workflow history, not current ground truth.')
$summary.Add('Note: candidate network changes must be retried before they are treated as regressions.')
$summary.Add('')
$summary.Add('Baseline verdicts:')
foreach ($group in ($manifest | Group-Object baseline_verdict | Sort-Object Count -Descending)) {
    $name = if ([string]::IsNullOrWhiteSpace($group.Name)) { '(blank)' } else { $group.Name }
    $summary.Add("- ${name}: $($group.Count)")
}
$summary.Add('')
$summary.Add('Historical disposition=down (reference only, not current truth):')
foreach ($group in ($manifest | Where-Object historical_disposition -eq $historicalDown | Group-Object baseline_verdict | Sort-Object Count -Descending)) {
    $name = if ([string]::IsNullOrWhiteSpace($group.Name)) { '(blank)' } else { $group.Name }
    $summary.Add("- ${name}: $($group.Count)")
}
$summary.Add('')
$summary.Add('Largest review groups:')
foreach ($group in ($manifest | Where-Object baseline_verdict -eq $verdictReview | Group-Object platform | Sort-Object Count -Descending | Select-Object -First 15)) {
    $summary.Add("- $($group.Name): $($group.Count)")
}
if ($candidateRows.Count -gt 0) {
    $summary.Add('')
    $summary.Add('Candidate transitions:')
    foreach ($group in ($manifest | Where-Object { -not [string]::IsNullOrWhiteSpace($_.'transition') } | Group-Object transition | Sort-Object Count -Descending)) {
        $summary.Add("- $($group.Name): $($group.Count)")
    }
}

$summaryPath = Join-Path $OutputDirectory 'baseline-summary.txt'
[IO.File]::WriteAllLines($summaryPath, $summary, (New-Object Text.UTF8Encoding($true)))
Write-Host "Created: $manifestPath"
Write-Host "Created: $samplePath"
Write-Host "Created: $summaryPath"
Write-Host "Ground truth template: $confirmedPath"
