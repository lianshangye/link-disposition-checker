param(
    [Parameter(Mandatory = $true)][string]$InputCsv,
    [Parameter(Mandatory = $true)][string]$BaselineResultCsv,
    [string]$SupplementResultCsv = '',
    [string]$MergedResultCsv = '',
    [int]$Workers = 1,
    [switch]$IncludeNoPublicPage
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$baselineRows = @(Import-Csv -LiteralPath $BaselineResultCsv)
if ($baselineRows.Count -eq 0) { throw 'Baseline result is empty.' }
$numberName = -join @([char]0x5E8F,[char]0x53F7)
$verdictName = -join @([char]0x6838,[char]0x9A8C,[char]0x7ED3,[char]0x679C)
$statusName = 'HTTP' + (-join @([char]0x72B6,[char]0x6001))
$removedLabel = -join @([char]0x5DF2,[char]0x5931,[char]0x6548)
$aliveLabel = -join @([char]0x4ECD,[char]0x53EF,[char]0x8BBF,[char]0x95EE)
$noPublicPageLabel = -join @([char]0x65E0,[char]0x516C,[char]0x5F00,[char]0x9875)
$candidates = @($baselineRows | Where-Object {
    [string]($_.PSObject.Properties[$verdictName].Value) -notin @($removedLabel,$aliveLabel) -and
    ($IncludeNoPublicPage -or [string]($_.PSObject.Properties[$statusName].Value) -ne $noPublicPageLabel)
})
if ($candidates.Count -eq 0) { throw 'Baseline result has no browser-supplement candidates.' }

$baselineDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($BaselineResultCsv))
if ([String]::IsNullOrWhiteSpace($SupplementResultCsv)) { $SupplementResultCsv = Join-Path $baselineDirectory 'browser-supplement-result.csv' }
if ([String]::IsNullOrWhiteSpace($MergedResultCsv)) { $MergedResultCsv = Join-Path $baselineDirectory 'browser-merged-result.csv' }
$SupplementResultCsv = [IO.Path]::GetFullPath($SupplementResultCsv)
$MergedResultCsv = [IO.Path]::GetFullPath($MergedResultCsv)

$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$aiSource = Join-Path $PSScriptRoot 'AiReview.cs'
$logSource = Join-Path $PSScriptRoot 'RunLogging.cs'
$acceptanceSource = Join-Path $PSScriptRoot 'AcceptanceEvidence.cs'
$chinaEyeballSource = Join-Path $PSScriptRoot 'ChinaEyeballEvidence.cs'
$runnerSource = Join-Path $PSScriptRoot 'EdgeFastAuditRunner.cs'
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
$webViewLoader = Join-Path $dependencyRoot 'x64\WebView2Loader.dll'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runDirectory = Join-Path $env:TEMP ('LinkCheckerBrowserSupplement_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null

$oldNumbers = $env:EDGE_AUDIT_NUMBERS
$oldWorkers = $env:EDGE_AUDIT_WORKERS
try {
    Copy-Item -LiteralPath $webViewCore -Destination $runDirectory
    Copy-Item -LiteralPath $webViewForms -Destination $runDirectory
    Copy-Item -LiteralPath $webViewLoader -Destination $runDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runDirectory
    $runner = Join-Path $runDirectory 'BrowserSupplement.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$runner /main:EdgeFastAuditRunner `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll `
        /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms `
        $source $aiSource $logSource $acceptanceSource $chinaEyeballSource $runnerSource
    if ($LASTEXITCODE -ne 0) { throw 'Browser supplement runner compilation failed.' }

    $env:EDGE_AUDIT_NUMBERS = ($candidates | ForEach-Object { [string]$_.PSObject.Properties[$numberName].Value }) -join ','
    $env:EDGE_AUDIT_WORKERS = [string][Math]::Max(1, $Workers)
    $arguments = '"' + [IO.Path]::GetFullPath($InputCsv) + '" "' + $SupplementResultCsv + '"'
    $process = Start-Process -FilePath $runner -ArgumentList $arguments -WorkingDirectory $runDirectory -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) { throw "Browser supplement failed with exit code $($process.ExitCode)." }

    $supplementRows = @(Import-Csv -LiteralPath $SupplementResultCsv)
    $byNumber = @{}
    foreach ($row in $supplementRows) { $byNumber[[string]$row.PSObject.Properties[$numberName].Value] = $row }
    foreach ($row in $baselineRows) {
        $key = [string]$row.PSObject.Properties[$numberName].Value
        $replacement = $byNumber[$key]
        if ($null -eq $replacement) { continue }
        foreach ($property in $replacement.PSObject.Properties) {
            if ($property.Name -eq $numberName) { continue }
            $target = $row.PSObject.Properties[$property.Name]
            if ($null -ne $target) { $target.Value = $property.Value }
        }
    }
    $baselineRows | Export-Csv -LiteralPath $MergedResultCsv -NoTypeInformation -Encoding UTF8
    $mergedResolved = @($baselineRows | Where-Object { [string]$_.PSObject.Properties[$verdictName].Value -in @($removedLabel,$aliveLabel) }).Count
    Write-Host "BROWSER_CANDIDATES=$($candidates.Count)"
    Write-Host "BROWSER_RESULTS=$($supplementRows.Count)"
    Write-Host "MERGED_TOTAL=$($baselineRows.Count)"
    Write-Host "MERGED_RESOLVED=$mergedResolved"
    Write-Host "MERGED_UNRESOLVED=$($baselineRows.Count - $mergedResolved)"
    Write-Host "MERGED_OUTPUT=$MergedResultCsv"
}
finally {
    $env:EDGE_AUDIT_NUMBERS = $oldNumbers
    $env:EDGE_AUDIT_WORKERS = $oldWorkers
    if (Test-Path -LiteralPath $runDirectory) { Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
