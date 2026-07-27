param(
    [Parameter(Mandatory = $true)][string]$LedgerCsv,
    [string]$ReferenceCsv = '',
    [string]$CurrentHumanTruthCsv = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $LedgerCsv)) { throw 'Ledger CSV was not found.' }
if ([String]::IsNullOrWhiteSpace($ReferenceCsv)) {
    $ReferenceCsv = Join-Path $PSScriptRoot 'test-data\representative-validation-samples.csv'
}
if ([String]::IsNullOrWhiteSpace($CurrentHumanTruthCsv)) {
    $CurrentHumanTruthCsv = Join-Path $PSScriptRoot 'test-data\manual-ground-truth.csv'
}
if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:TEMP ('LinkCheckerCoverage_' + [Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function First-Value($Row, [string[]]$Names) {
    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property -and -not [String]::IsNullOrWhiteSpace([string]$property.Value)) { return ([string]$property.Value).Trim() }
    }
    return ''
}

function Get-DomainFamily([string]$Url) {
    if ([String]::IsNullOrWhiteSpace($Url)) { return '(无有效域名)' }
    $uri = $null
    if (-not [Uri]::TryCreate($Url.Trim(), [UriKind]::Absolute, [ref]$uri)) { return '(无有效域名)' }
    $domainHost = $uri.DnsSafeHost.ToLowerInvariant().Trim('.')
    if ($domainHost -match '^\d{1,3}(\.\d{1,3}){3}$' -or $domainHost -eq 'localhost') { return $domainHost }
    $parts = @($domainHost.Split('.') | Where-Object { $_ -ne '' })
    if ($parts.Count -le 2) { return $domainHost }
    $compoundSuffixes = @('com.cn','net.cn','org.cn','gov.cn','edu.cn','co.uk','com.hk','com.tw','com.au','co.jp')
    $suffix = ($parts[($parts.Count - 2)..($parts.Count - 1)] -join '.')
    if ($compoundSuffixes -contains $suffix -and $parts.Count -ge 3) {
        return ($parts[($parts.Count - 3)..($parts.Count - 1)] -join '.')
    }
    return $suffix
}

function Read-Families([string]$Path, [switch]$RequireCurrentTruth) {
    $families = @{}
    if ([String]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) { return $families }
    foreach ($row in @(Import-Csv -LiteralPath $Path)) {
        if ($RequireCurrentTruth) {
            $verdict = First-Value $row @('confirmed_verdict')
            $confirmedAt = First-Value $row @('confirmed_at')
            if ([String]::IsNullOrWhiteSpace($verdict) -or [String]::IsNullOrWhiteSpace($confirmedAt)) { continue }
        }
        $family = Get-DomainFamily (First-Value $row @('url','原链接','链接','URL','网址'))
        if ($family -ne '(无有效域名)') { $families[$family] = $true }
    }
    return $families
}

$sampledFamilies = Read-Families $ReferenceCsv
$validatedFamilies = Read-Families $CurrentHumanTruthCsv -RequireCurrentTruth
$rows = @(Import-Csv -LiteralPath $LedgerCsv)
if ($rows.Count -eq 0) { throw 'Ledger CSV is empty.' }

$details = foreach ($row in $rows) {
    $platform = First-Value $row @('平台名称','发布平台','来源平台','平台','platform')
    if ([String]::IsNullOrWhiteSpace($platform)) { $platform = '(未填写平台)' }
    $url = First-Value $row @('链接','原链接','URL','url','网址')
    $family = Get-DomainFamily $url
    [pscustomobject]@{
        platform = $platform
        domain_family = $family
        sampled_before = $sampledFamilies.ContainsKey($family)
        current_human_validated = $validatedFamilies.ContainsKey($family)
    }
}

$domainReport = foreach ($group in ($details | Group-Object domain_family | Sort-Object Count -Descending)) {
    $first = $group.Group[0]
    [pscustomobject]@{
        domain_family = $group.Name
        rows = $group.Count
        sampled_before = $first.sampled_before
        current_human_validated = $first.current_human_validated
        platforms = (($group.Group.platform | Sort-Object -Unique) -join '、')
    }
}
$platformReport = foreach ($group in ($details | Group-Object platform | Sort-Object Count -Descending)) {
    [pscustomobject]@{
        platform = $group.Name
        rows = $group.Count
        domain_families = @($group.Group.domain_family | Sort-Object -Unique).Count
        unseen_family_rows = @($group.Group | Where-Object { -not $_.sampled_before }).Count
        unvalidated_family_rows = @($group.Group | Where-Object { -not $_.current_human_validated }).Count
    }
}

$domainPath = Join-Path $OutputDirectory 'domain-coverage.csv'
$platformPath = Join-Path $OutputDirectory 'platform-coverage.csv'
$summaryPath = Join-Path $OutputDirectory 'coverage-summary.txt'
$domainReport | Export-Csv -LiteralPath $domainPath -NoTypeInformation -Encoding UTF8
$platformReport | Export-Csv -LiteralPath $platformPath -NoTypeInformation -Encoding UTF8

$unseenFamilies = @($domainReport | Where-Object { $_.sampled_before -eq $false })
$unvalidatedFamilies = @($domainReport | Where-Object { $_.current_human_validated -eq $false })
$genericRows = @($details | Where-Object { $_.platform -match '^(网媒|网络媒体|其他网媒)$' })
$genericUnvalidated = @($genericRows | Where-Object { -not $_.current_human_validated })
$summary = @(
    '新台账覆盖检查',
    ('生成时间：' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')),
    ('台账：' + $LedgerCsv),
    ('总记录：' + $rows.Count),
    ('平台标签：' + $platformReport.Count),
    ('域名家族：' + $domainReport.Count),
    ('历史样本未见域名家族：' + $unseenFamilies.Count),
    ('缺少当期人工真值的域名家族：' + $unvalidatedFamilies.Count),
    ('“网媒”记录：' + $genericRows.Count),
    ('“网媒”中缺少当期人工真值的记录：' + $genericUnvalidated.Count),
    '',
    '说明：平台列只是供应商标签；“网媒”不能视为一种已验证平台，必须继续按真实域名拆分。',
    '说明：历史见过只代表进入过样本，不代表准确性已经通过当期人工验收。'
)
[IO.File]::WriteAllLines($summaryPath, $summary, (New-Object Text.UTF8Encoding($true)))

Write-Host "LEDGER_ROWS=$($rows.Count)"
Write-Host "PLATFORMS=$($platformReport.Count)"
Write-Host "DOMAIN_FAMILIES=$($domainReport.Count)"
Write-Host "UNSEEN_DOMAIN_FAMILIES=$($unseenFamilies.Count)"
Write-Host "CURRENTLY_UNVALIDATED_DOMAIN_FAMILIES=$($unvalidatedFamilies.Count)"
Write-Host "GENERIC_WEBMEDIA_ROWS=$($genericRows.Count)"
Write-Host "GENERIC_WEBMEDIA_UNVALIDATED_ROWS=$($genericUnvalidated.Count)"
if ($genericUnvalidated.Count -gt 0) { Write-Warning 'Generic web-media rows contain domain families without current human validation.' }
Write-Host "COVERAGE_SUMMARY=$summaryPath"
