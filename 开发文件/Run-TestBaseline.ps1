param(
    [string]$SourceCsv = '',
    [string]$BaselineResult = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceCsv)) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $SourceCsv = Get-ChildItem -LiteralPath $desktop -Filter '*.csv' |
        Where-Object { $_.Length -gt 100000 } | Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($BaselineResult)) {
    $BaselineResult = Get-ChildItem -LiteralPath $projectRoot -Recurse -Filter '*.csv' |
        Where-Object { $_.DirectoryName -notlike '*test-data*' -and $_.Name -like '*_*' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'test-data'
}

& (Join-Path $PSScriptRoot 'Build-TestBaseline.ps1') -SourceCsv $SourceCsv -CurrentResult $BaselineResult -OutputDirectory $OutputDirectory
$samples = @(Import-Csv -LiteralPath (Join-Path $OutputDirectory 'platform-samples.csv'))
$env:FAST_AUDIT_NUMBERS = (($samples | ForEach-Object { $_.number }) -join ',')

$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$runnerDirectory = Join-Path $env:TEMP 'LinkCheckerTestRunner'
New-Item -ItemType Directory -Path $runnerDirectory -Force | Out-Null
$runner = Join-Path $runnerDirectory 'LinkChecker.FastAuditRunner.exe'
$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$runnerSource = Join-Path $PSScriptRoot 'FastAuditRunner.cs'
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
Copy-Item -LiteralPath $webViewCore -Destination $runnerDirectory -Force
Copy-Item -LiteralPath $webViewForms -Destination $runnerDirectory -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runnerDirectory -Force

& $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$runner /main:FastAuditRunner `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll `
    /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms $source $runnerSource
if ($LASTEXITCODE -ne 0) { throw 'Fast audit runner compilation failed.' }

$candidate = Join-Path $OutputDirectory 'candidate-platform-samples.csv'
& $runner $SourceCsv $candidate
if ($LASTEXITCODE -ne 0) { throw 'Fast audit runner failed.' }

# Retry only resolved-to-review changes once. A transient timeout must not be reported
# as a rule regression when the same URL succeeds on an immediate second attempt.
$baselineRows = @(Import-Csv -LiteralPath $BaselineResult)
$candidateRows = @(Import-Csv -LiteralPath $candidate)
$baselineIndex = @{}
foreach ($row in $baselineRows) { $baselineIndex[[string]@($row.PSObject.Properties)[0].Value] = $row }
$removed = -join @([char]0x5DF2,[char]0x5931,[char]0x6548)
$alive = -join @([char]0x4ECD,[char]0x53EF,[char]0x8BBF,[char]0x95EE)
$retryNumbers = foreach ($row in $candidateRows) {
    $properties = @($row.PSObject.Properties)
    $number = [string]$properties[0].Value
    $candidateVerdict = [string]$properties[1].Value
    $baseline = $baselineIndex[$number]
    $baselineVerdict = if ($null -eq $baseline) { '' } else { [string]@($baseline.PSObject.Properties)[1].Value }
    if (($baselineVerdict -eq $removed -or $baselineVerdict -eq $alive) -and
        $candidateVerdict -ne $removed -and $candidateVerdict -ne $alive) { $number }
}
if (@($retryNumbers).Count -gt 0) {
    $env:FAST_AUDIT_NUMBERS = (@($retryNumbers) -join ',')
    $retryPath = Join-Path $OutputDirectory 'candidate-retry.csv'
    & $runner $SourceCsv $retryPath
    if ($LASTEXITCODE -ne 0) { throw 'Fast audit retry failed.' }
    $retryIndex = @{}
    foreach ($row in @(Import-Csv -LiteralPath $retryPath)) {
        $retryIndex[[string]@($row.PSObject.Properties)[0].Value] = $row
    }
    $merged = foreach ($row in $candidateRows) {
        $number = [string]@($row.PSObject.Properties)[0].Value
        $retry = $retryIndex[$number]
        $retryVerdict = if ($null -eq $retry) { '' } else { [string]@($retry.PSObject.Properties)[1].Value }
        if ($retryVerdict -eq $removed -or $retryVerdict -eq $alive) { $retry } else { $row }
    }
    $merged | Export-Csv -LiteralPath $candidate -NoTypeInformation -Encoding UTF8
    Remove-Item -LiteralPath $retryPath -Force
}

& (Join-Path $PSScriptRoot 'Build-TestBaseline.ps1') -SourceCsv $SourceCsv -CurrentResult $BaselineResult `
    -CandidateResult $candidate -OutputDirectory $OutputDirectory
Write-Host "Candidate sample result: $candidate"
