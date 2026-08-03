param(
    [Parameter(Mandatory = $true)][string]$InputXlsx,
    [string]$OutputDirectory = '',
    [string]$PythonExecutable = 'python',
    [int]$ExpectedRows = 0,
    [string]$OriginalSheet = '',
    [int]$ExpectedOriginalRows = 0,
    [int]$Workers = 6,
    [double]$MaximumUnresolvedRate = 0.05,
    [switch]$Resume,
    [switch]$RebuildInput
)

$ErrorActionPreference = 'Stop'
if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Join-Path $PSScriptRoot 'test-data') 'full-validation-current'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$inputCsv = Join-Path $OutputDirectory 'full-validation-input.csv'
$resultCsv = Join-Path $OutputDirectory 'full-validation-result.csv'

if ($RebuildInput -or -not (Test-Path -LiteralPath $inputCsv)) {
    $builderArguments = @(
        (Join-Path $PSScriptRoot 'Build-FullValidationInput.py'),
        [IO.Path]::GetFullPath($InputXlsx),
        $inputCsv
    )
    if ($ExpectedRows -gt 0) { $builderArguments += @('--expected-rows', [string]$ExpectedRows) }
    if (-not [String]::IsNullOrWhiteSpace($OriginalSheet)) { $builderArguments += @('--original-sheet', $OriginalSheet) }
    if ($ExpectedOriginalRows -gt 0) { $builderArguments += @('--expected-original-rows', [string]$ExpectedOriginalRows) }
    & $PythonExecutable @builderArguments
    if ($LASTEXITCODE -ne 0) { throw 'Full validation input build failed.' }
}

$oldWorkers = $env:FAST_AUDIT_WORKERS
try {
    $env:FAST_AUDIT_WORKERS = [string][Math]::Max(1, $Workers)
    $runParameters = @{
        InputCsv = $inputCsv
        OutputCsv = $resultCsv
        MinimumResolvedRate = 1.0 - $MaximumUnresolvedRate
        ReportOnly = $true
    }
    if ($Resume) { $runParameters.Resume = $true }
    & (Join-Path $PSScriptRoot 'Run-RepresentativeValidation.ps1') @runParameters
}
finally { $env:FAST_AUDIT_WORKERS = $oldWorkers }

& (Join-Path $PSScriptRoot 'Measure-FullValidation.ps1') -ResultCsv $resultCsv `
    -OriginalSheet $OriginalSheet -MaximumUnresolvedRate $MaximumUnresolvedRate
