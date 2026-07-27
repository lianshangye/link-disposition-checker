param(
    [string]$InputCsv = '',
    [string]$OutputCsv = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testData = Join-Path $PSScriptRoot 'test-data'
if ([String]::IsNullOrWhiteSpace($InputCsv)) {
    $InputCsv = Join-Path $testData 'representative-validation-samples.csv'
}
if ([String]::IsNullOrWhiteSpace($OutputCsv)) {
    $OutputCsv = Join-Path $testData 'representative-validation-result-3.11.0-report.csv'
}

$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$runnerSource = Join-Path $PSScriptRoot 'FastAuditRunner.cs'
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runDirectory = Join-Path $env:TEMP ('LinkCheckerRepresentative_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null

try {
    Copy-Item -LiteralPath $webViewCore -Destination $runDirectory
    Copy-Item -LiteralPath $webViewForms -Destination $runDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runDirectory
    $runner = Join-Path $runDirectory 'Representative.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$runner /main:FastAuditRunner `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll `
        /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms $source $runnerSource
    if ($LASTEXITCODE -ne 0) { throw 'Representative runner compilation failed.' }

    $arguments = '"' + $InputCsv + '" "' + $OutputCsv + '"'
    $process = Start-Process -FilePath $runner -ArgumentList $arguments -WorkingDirectory $runDirectory -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) { throw "Representative validation failed with exit code $($process.ExitCode)." }

    $rows = @(Import-Csv -LiteralPath $OutputCsv)
    $removedLabel = -join @([char]0x5DF2, [char]0x5931, [char]0x6548)
    $aliveLabel = -join @([char]0x4ECD, [char]0x53EF, [char]0x8BBF, [char]0x95EE)
    $removed = @($rows | Where-Object { [string]@($_.PSObject.Properties)[1].Value -eq $removedLabel }).Count
    $alive = @($rows | Where-Object { [string]@($_.PSObject.Properties)[1].Value -eq $aliveLabel }).Count
    $review = $rows.Count - $removed - $alive
    $rate = if ($rows.Count -eq 0) { 0 } else { 100.0 * $review / $rows.Count }
    Write-Host "TOTAL=$($rows.Count)"
    Write-Host "REMOVED=$removed"
    Write-Host "ALIVE=$alive"
    Write-Host "REVIEW=$review"
    Write-Host ("REVIEW_RATE={0:0.00}%" -f $rate)
    Write-Host "OUTPUT=$OutputCsv"
    # Historical disposition is useful for finding conflicts, but it is not current release truth.
    & (Join-Path $PSScriptRoot 'Compare-HumanValidation.ps1') -InputCsv $InputCsv -OutputCsv $OutputCsv
}
finally {
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
