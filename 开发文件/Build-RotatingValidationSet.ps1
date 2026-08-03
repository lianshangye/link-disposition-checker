param(
    [Parameter(Mandatory = $true)][string[]]$InputPath,
    [string]$OutputCsv = '',
    [string]$HistoryCsv = '',
    [int]$MaximumRows = 240,
    [int]$RowsPerPlatformOrDomain = 3,
    [int]$MinimumNetMediaRows = 0,
    [string]$Seed = ''
)

$ErrorActionPreference = 'Stop'
if ([String]::IsNullOrWhiteSpace($Seed)) { $Seed = Get-Date -Format 'yyyyMMdd-HHmmss' }
if ($MinimumNetMediaRows -lt 0 -or $MinimumNetMediaRows -gt $MaximumRows) {
    throw 'MinimumNetMediaRows must be between zero and MaximumRows.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([String]::IsNullOrWhiteSpace($OutputCsv)) {
    $resultFolder = -join @([char]0x6838,[char]0x9A8C,[char]0x7ED3,[char]0x679C)
    $sampleName = (-join @([char]0x8F6E,[char]0x6362,[char]0x62BD,[char]0x6837,[char]0x005F,[char]0x5F53,[char]0x524D)) + '.csv'
    $OutputCsv = Join-Path (Join-Path $projectRoot $resultFolder) $sampleName
}
$OutputCsv = [IO.Path]::GetFullPath($OutputCsv)
if ([String]::IsNullOrWhiteSpace($HistoryCsv)) {
    $HistoryCsv = Join-Path (Join-Path $PSScriptRoot 'test-data') 'rotating-validation-history.csv'
}
$HistoryCsv = [IO.Path]::GetFullPath($HistoryCsv)
$resolvedInputs = @($InputPath | ForEach-Object {
    if (Test-Path -LiteralPath $_ -PathType Container) {
        Get-ChildItem -LiteralPath $_ -File | Where-Object { $_.Extension -in '.csv','.xlsx','.xlsm' } | ForEach-Object FullName
    }
    elseif (Test-Path -LiteralPath $_ -PathType Leaf) { [IO.Path]::GetFullPath($_) }
    else { throw "Validation source was not found: $_" }
} | Where-Object { $_ -ne $OutputCsv -and $_ -ne $HistoryCsv } | Select-Object -Unique)
if ($resolvedInputs.Count -eq 0) { throw 'No usable CSV/XLSX validation sources were found.' }
$historyDirectory = Split-Path -Parent $HistoryCsv
if (-not [String]::IsNullOrWhiteSpace($historyDirectory)) {
    New-Item -ItemType Directory -Path $historyDirectory -Force | Out-Null
}

$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$sources = @(
    $source,
    (Join-Path $PSScriptRoot 'AiReview.cs'),
    (Join-Path $PSScriptRoot 'RunLogging.cs'),
    (Join-Path $PSScriptRoot 'AcceptanceEvidence.cs'),
    (Join-Path $PSScriptRoot 'ChinaEyeballEvidence.cs'),
    (Join-Path $PSScriptRoot 'RotatingSampleBuilder.cs')
)
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runDirectory = Join-Path $env:TEMP ('LinkCheckerRotating_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null

try {
    Copy-Item -LiteralPath $webViewCore -Destination $runDirectory
    Copy-Item -LiteralPath $webViewForms -Destination $runDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runDirectory
    $runner = Join-Path $runDirectory 'RotatingSampleBuilder.exe'
    $runnerOutput = Join-Path $runDirectory 'rotating-sample.csv'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$runner /main:RotatingSampleBuilder `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll `
        /reference:System.Security.dll /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll `
        /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
        /reference:$webViewCore /reference:$webViewForms $sources
    if ($LASTEXITCODE -ne 0) { throw 'Rotating sample builder compilation failed.' }
    & $runner $runnerOutput $HistoryCsv $MaximumRows $RowsPerPlatformOrDomain `
        $MinimumNetMediaRows $Seed @($resolvedInputs)
    if ($LASTEXITCODE -eq 7) {
        throw 'The unused real-data net-media pool is smaller than MinimumNetMediaRows. Reduce only the stress quota or add new real ledgers; previously tested URLs will not be reused.'
    }
    if ($LASTEXITCODE -ne 0) { throw "Rotating sample builder failed with exit code $LASTEXITCODE." }
    $outputDirectory = Split-Path -Parent $OutputCsv
    if (-not [String]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    Move-Item -LiteralPath $runnerOutput -Destination $OutputCsv -Force
    Write-Host "ROTATING_FINAL_OUTPUT=$OutputCsv"
}
finally {
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
