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
    $OutputCsv = Join-Path $testData 'representative-validation-result-3.10.5-final.csv'
}

$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$aiSource = Join-Path $PSScriptRoot 'AiReview.cs'
$logSource = Join-Path $PSScriptRoot 'RunLogging.cs'
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
        /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll `
        /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms $source $aiSource $logSource $runnerSource
    if ($LASTEXITCODE -ne 0) { throw 'Representative runner compilation failed.' }

    $arguments = '"' + $InputCsv + '" "' + $OutputCsv + '"'
    $process = Start-Process -FilePath $runner -ArgumentList $arguments -WorkingDirectory $runDirectory -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) { throw "Representative validation failed with exit code $($process.ExitCode)." }

    $rows = @(Import-Csv -LiteralPath $OutputCsv)
    $removedLabel = -join @([char]0x5DF2, [char]0x5931, [char]0x6548)
    $aliveLabel = -join @([char]0x4ECD, [char]0x53EF, [char]0x8BBF, [char]0x95EE)
    $unavailableLabel = -join @([char]0x516C, [char]0x7F51, [char]0x4E0D, [char]0x53EF, [char]0x8BBF, [char]0x95EE)
    $temporaryLabel = -join @([char]0x6682, [char]0x65F6, [char]0x5F02, [char]0x5E38)
    $removed = @($rows | Where-Object { [string]@($_.PSObject.Properties)[1].Value -eq $removedLabel }).Count
    $alive = @($rows | Where-Object { [string]@($_.PSObject.Properties)[1].Value -eq $aliveLabel }).Count
    $unavailable = @($rows | Where-Object { [string]@($_.PSObject.Properties)[1].Value -eq $unavailableLabel }).Count
    $temporary = @($rows | Where-Object { [string]@($_.PSObject.Properties)[1].Value -eq $temporaryLabel }).Count
    $review = $rows.Count - $removed - $alive - $unavailable - $temporary
    $unresolved = $review + $temporary
    $rate = if ($rows.Count -eq 0) { 0 } else { 100.0 * $unresolved / $rows.Count }
    Write-Host "TOTAL=$($rows.Count)"
    Write-Host "REMOVED=$removed"
    Write-Host "ALIVE=$alive"
    Write-Host "PUBLIC_UNAVAILABLE=$unavailable"
    Write-Host "TEMPORARY=$temporary"
    Write-Host "REVIEW=$review"
    Write-Host "UNRESOLVED=$unresolved"
    Write-Host ("UNRESOLVED_RATE={0:0.00}%" -f $rate)
    Write-Host "OUTPUT=$OutputCsv"
}
finally {
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
