param(
    [Parameter(Mandatory = $true)][string]$ResultCsv,
    [string]$OriginalSheet = '',
    [double]$MaximumUnresolvedRate = 0.05
)

$ErrorActionPreference = 'Stop'
$rows = @(Import-Csv -LiteralPath $ResultCsv)
if ($rows.Count -eq 0) { throw 'Full validation result is empty.' }
$verdictNames = @(
    (-join @([char]0x6838,[char]0x9A8C,[char]0x7ED3,[char]0x679C)),
    (-join @([char]0x81EA,[char]0x52A8,[char]0x6838,[char]0x9A8C,[char]0x7ED3,[char]0x679C)),
    (-join @([char]0x94FE,[char]0x63A5,[char]0x662F,[char]0x5426,[char]0x5931,[char]0x6548))
)
$sourceSheetName = -join @([char]0x6765,[char]0x6E90,[char]0x5DE5,[char]0x4F5C,[char]0x8868)
$removedLabel = -join @([char]0x5DF2,[char]0x5931,[char]0x6548)
$aliveLabel = -join @([char]0x4ECD,[char]0x53EF,[char]0x8BBF,[char]0x95EE)
$verdictProperty = @($rows[0].PSObject.Properties | Where-Object { $_.Name -in $verdictNames } | Select-Object -First 1)
if ($verdictProperty.Count -eq 0) { throw 'Full validation result has no verdict column.' }
$verdictName = $verdictProperty[0].Name

function Measure-Group([object[]]$Items, [string]$Name) {
    $resolved = @($Items | Where-Object { [string]$_.$verdictName -in @($removedLabel,$aliveLabel) }).Count
    $unresolved = $Items.Count - $resolved
    $rate = if ($Items.Count -eq 0) { [decimal]1 } else { [decimal]$unresolved / [decimal]$Items.Count }
    Write-Host "GROUP=$Name"
    Write-Host "TOTAL=$($Items.Count)"
    Write-Host "RESOLVED=$resolved"
    Write-Host "UNRESOLVED=$unresolved"
    Write-Host ('UNRESOLVED_RATE={0:0.00}%' -f (100 * $rate))
    return $rate
}

$maximum = [decimal]$MaximumUnresolvedRate
$totalRate = Measure-Group $rows 'all-data'
$failed = $totalRate -ge $maximum
if (-not [String]::IsNullOrWhiteSpace($OriginalSheet)) {
    $originalRows = @($rows | Where-Object { [string]($_.PSObject.Properties[$sourceSheetName].Value) -eq $OriginalSheet })
    if ($originalRows.Count -eq 0) { throw "Result does not contain original sheet: $OriginalSheet" }
    $originalRate = Measure-Group $originalRows 'original-attachment'
    $failed = $failed -or $originalRate -ge $maximum
}
Write-Host ('UNRESOLVED_RATE_TARGET=below {0:0.00}%' -f (100 * $maximum))
if ($failed) { throw 'Full validation unresolved rate does not meet the strict below-5% target.' }
Write-Host 'FULL_VALIDATION_GATE=PASSED'
