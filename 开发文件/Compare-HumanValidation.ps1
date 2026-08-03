param(
    [Parameter(Mandatory = $true)][string]$InputCsv,
    [Parameter(Mandatory = $true)][string]$OutputCsv,
    [switch]$ReleaseGate,
    [int]$MinimumLabelledRows = 50,
    [int]$MinimumAliveRows = 15,
    [int]$MinimumRemovedRows = 15,
    [int]$MinimumPlatforms = 5,
    [int]$MinimumDomainFamilies = 10,
    [int]$MaximumLabelAgeDays = 7,
    [double]$MaximumFalseInvalidRate = 0.02,
    [double]$MaximumFalseAliveRate = 0.02,
    [double]$ReviewRateTarget = 0.05
)

$ErrorActionPreference = 'Stop'
$inputRows = @(Import-Csv -LiteralPath $InputCsv)
$outputRows = @(Import-Csv -LiteralPath $OutputCsv)
if ($inputRows.Count -eq 0 -or $outputRows.Count -eq 0) { throw 'Validation input or output is empty.' }

function First-Value($Row, [string[]]$Names) {
    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property -and -not [String]::IsNullOrWhiteSpace([string]$property.Value)) {
            return [string]$property.Value
        }
    }
    return ''
}

function Normalize-HumanVerdict([string]$Value) {
    $value = if ($null -eq $Value) { '' } else { $Value.Trim() }
    if ($value -match '^(下架|已失效|失效|是)$') { return 'removed' }
    if ($value -match '^(否|仍可访问|未失效|有效)$') { return 'alive' }
    return ''
}

function Normalize-ToolVerdict([string]$Value) {
    $value = if ($null -eq $Value) { '' } else { $Value.Trim() }
    if ($value -in @('已失效','是')) { return 'removed' }
    if ($value -in @('仍可访问','否')) { return 'alive' }
    return 'review'
}

function Get-DomainFamily([string]$Url) {
    $uri = $null
    $urlText = if ($null -eq $Url) { '' } else { $Url.Trim() }
    if (-not [Uri]::TryCreate($urlText, [UriKind]::Absolute, [ref]$uri)) { return '' }
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

$inputByUrl = @{}
$inputByValidationNumber = @{}
$validationNumber = 0
for ($inputIndex = 0; $inputIndex -lt $inputRows.Count; $inputIndex++) {
    $row = $inputRows[$inputIndex]
    $url = First-Value $row @('url','URL','链接','网址','原链接')
    $validationUri = $null
    if ([String]::IsNullOrWhiteSpace($url) -or -not [Uri]::TryCreate($url.Trim(), [UriKind]::Absolute, [ref]$validationUri) -or
        $validationUri.Scheme -notin @('http','https')) { continue }
    $inputByUrl[$url.Trim()] = $row
    # FastAuditRunner numbers valid URL rows in input order; supplier sequence fields may be sparse or unrelated.
    $validationNumber++
    $inputByValidationNumber[[string]$validationNumber] = $row
}

$falseInvalid = 0
$falseAlive = 0
$review = 0
$humanAlive = 0
$humanRemoved = 0
$joined = 0
$missingCurrentFields = 0
$expiredLabels = 0
$cutoff = (Get-Date).AddDays(-$MaximumLabelAgeDays)
$platforms = @{}
$domainFamilies = @{}

foreach ($result in $outputRows) {
    $url = First-Value $result @('原链接','链接','URL','url')
    $number = First-Value $result @('number','序号','编号')
    $source = if (-not [String]::IsNullOrWhiteSpace($number)) { $inputByValidationNumber[$number.Trim()] } else { $null }
    if ($null -ne $source -and -not [String]::IsNullOrWhiteSpace($url)) {
        $numberedUrl = First-Value $source @('url','URL','链接','网址','原链接')
        if (-not [String]::Equals($numberedUrl.Trim(), $url.Trim(), [StringComparison]::OrdinalIgnoreCase)) { $source = $null }
    }
    if ($null -eq $source -and -not [String]::IsNullOrWhiteSpace($url)) { $source = $inputByUrl[$url.Trim()] }
    if ($null -eq $source) { continue }

    if ($ReleaseGate) {
        $humanText = First-Value $source @('confirmed_verdict')
        $confirmedAtText = First-Value $source @('confirmed_at')
        if ([String]::IsNullOrWhiteSpace($humanText) -or [String]::IsNullOrWhiteSpace($confirmedAtText)) {
            $missingCurrentFields++
            continue
        }
        $confirmedAt = [DateTime]::MinValue
        if (-not [DateTime]::TryParse($confirmedAtText, [ref]$confirmedAt) -or $confirmedAt -lt $cutoff) {
            $expiredLabels++
            continue
        }
    }
    else {
        $humanText = First-Value $source @('confirmed_verdict','人工结果','人工核验结果','确认结果','处置情况','链接是否失效')
    }

    $human = Normalize-HumanVerdict $humanText
    if ([String]::IsNullOrWhiteSpace($human)) { continue }
    $sourceUrl = First-Value $source @('url','URL','链接','网址','原链接')
    $platform = First-Value $source @('platform','平台名称','发布平台','来源平台','平台')
    $domainFamily = Get-DomainFamily $sourceUrl
    if (-not [String]::IsNullOrWhiteSpace($platform)) { $platforms[$platform] = $true }
    if (-not [String]::IsNullOrWhiteSpace($domainFamily)) { $domainFamilies[$domainFamily] = $true }
    $tool = Normalize-ToolVerdict (First-Value $result @('核验结果','自动核验结果','链接是否失效'))
    $joined++
    if ($human -eq 'removed') {
        $humanRemoved++
        if ($tool -eq 'alive') { $falseAlive++ }
    }
    if ($human -eq 'alive') {
        $humanAlive++
        if ($tool -eq 'removed') { $falseInvalid++ }
    }
    if ($tool -eq 'review') { $review++ }
}

$falseInvalidRate = if ($humanAlive -eq 0) { 0.0 } else { $falseInvalid / [double]$humanAlive }
$falseAliveRate = if ($humanRemoved -eq 0) { 0.0 } else { $falseAlive / [double]$humanRemoved }
$reviewRate = if ($joined -eq 0) { 0.0 } else { $review / [double]$joined }

Write-Host ('VALIDATION_MODE=' + $(if ($ReleaseGate) { 'CURRENT_HUMAN_RELEASE_GATE' } else { 'HISTORICAL_REPORT_ONLY' }))
Write-Host "HUMAN_MATCHED=$joined"
Write-Host "HUMAN_ALIVE=$humanAlive"
Write-Host "HUMAN_REMOVED=$humanRemoved"
Write-Host "HUMAN_PLATFORMS=$($platforms.Count)"
Write-Host "HUMAN_DOMAIN_FAMILIES=$($domainFamilies.Count)"
Write-Host "FALSE_INVALID=$falseInvalid"
Write-Host "FALSE_ALIVE=$falseAlive"
Write-Host ("FALSE_INVALID_RATE={0:0.00}%" -f (100 * $falseInvalidRate))
Write-Host ("FALSE_ALIVE_RATE={0:0.00}%" -f (100 * $falseAliveRate))
Write-Host ("REVIEW_RATE={0:0.00}%" -f (100 * $reviewRate))
Write-Host ("REVIEW_RATE_TARGET={0:0.00}%" -f (100 * $ReviewRateTarget))
if ($reviewRate -gt $ReviewRateTarget) { Write-Warning 'Review rate is above the efficiency target; this does not change accuracy verdicts.' }

if (-not $ReleaseGate) {
    Write-Host 'RELEASE_GATE=NOT_RUN'
    Write-Host 'Historical labels are reported for diagnostics only and are not current release truth.'
    return
}

Write-Host "SKIPPED_WITHOUT_EXPLICIT_CURRENT_LABEL=$missingCurrentFields"
Write-Host "SKIPPED_EXPIRED_OR_INVALID_LABEL_TIME=$expiredLabels"
if ($joined -lt $MinimumLabelledRows) { throw "Current human sample is too small: $joined < $MinimumLabelledRows." }
if ($humanAlive -lt $MinimumAliveRows) { throw "Current alive sample is too small: $humanAlive < $MinimumAliveRows." }
if ($humanRemoved -lt $MinimumRemovedRows) { throw "Current removed sample is too small: $humanRemoved < $MinimumRemovedRows." }
if ($platforms.Count -lt $MinimumPlatforms) { throw "Current platform coverage is too small: $($platforms.Count) < $MinimumPlatforms." }
if ($domainFamilies.Count -lt $MinimumDomainFamilies) { throw "Current domain-family coverage is too small: $($domainFamilies.Count) < $MinimumDomainFamilies." }
if ($falseInvalidRate -gt $MaximumFalseInvalidRate) { throw 'False-invalid rate exceeds the release threshold.' }
if ($falseAliveRate -gt $MaximumFalseAliveRate) { throw 'False-alive rate exceeds the release threshold.' }
if ($reviewRate -ge $ReviewRateTarget) { throw 'Unresolved rate does not meet the below-5% release threshold.' }
Write-Host 'RELEASE_GATE=PASSED'
