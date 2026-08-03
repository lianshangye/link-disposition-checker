param(
    [string[]]$InputPath = @(),
    [string]$SourceList = '',
    [int]$SampleRows = 240,
    [int]$RowsPerPlatformOrDomain = 3,
    [int]$MinimumNetMediaRows = 80,
    [string]$Seed = '',
    [string]$SampleCsv = '',
    [string]$ResultCsv = '',
    [string]$HistoryCsv = '',
    [double]$MinimumResolvedRate = 0.95,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$resultFolder = Join-Path $projectRoot (-join @([char]0x6838,[char]0x9A8C,[char]0x7ED3,[char]0x679C))
if ($InputPath.Count -eq 0) {
    if ([String]::IsNullOrWhiteSpace($SourceList)) {
        $SourceList = Join-Path (Join-Path $PSScriptRoot 'test-data') 'rotating-validation-sources.txt'
    }
    if (-not (Test-Path -LiteralPath $SourceList)) {
        throw 'No validation inputs were supplied and the local source list was not found.'
    }
    $InputPath = @(Get-Content -LiteralPath $SourceList -Encoding UTF8 | ForEach-Object { $_.Trim() } |
        Where-Object { -not [String]::IsNullOrWhiteSpace($_) -and -not $_.StartsWith('#') })
}
if ($InputPath.Count -eq 0) { throw 'The rotating validation source list is empty.' }
if ([String]::IsNullOrWhiteSpace($SampleCsv)) {
    $SampleCsv = Join-Path $resultFolder ((-join @([char]0x8F6E,[char]0x6362,[char]0x9A8C,[char]0x8BC1,[char]0x6837,[char]0x672C,[char]0x005F,[char]0x5F53,[char]0x524D)) + '.csv')
}
if ([String]::IsNullOrWhiteSpace($ResultCsv)) {
    $ResultCsv = Join-Path $resultFolder ((-join @([char]0x8F6E,[char]0x6362,[char]0x9A8C,[char]0x8BC1,[char]0x7ED3,[char]0x679C,[char]0x005F,[char]0x5F53,[char]0x524D)) + '.csv')
}

& (Join-Path $PSScriptRoot 'Build-RotatingValidationSet.ps1') -InputPath $InputPath `
    -OutputCsv $SampleCsv -HistoryCsv $HistoryCsv -MaximumRows $SampleRows `
    -RowsPerPlatformOrDomain $RowsPerPlatformOrDomain `
    -MinimumNetMediaRows $MinimumNetMediaRows -Seed $Seed

if ($BuildOnly) {
    Write-Host "ROTATING_BUILD_ONLY=1"
    exit 0
}

& (Join-Path $PSScriptRoot 'Run-RepresentativeValidation.ps1') -InputCsv $SampleCsv `
    -OutputCsv $ResultCsv -MinimumResolvedRate $MinimumResolvedRate
