param(
    [string]$InputCsv = '',
    [string]$OutputCsv = '',
    [double]$MinimumResolvedRate = 0.95,
    [switch]$FixedRegression,
    [switch]$Resume,
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'
$oldFastAuditNumbers = $env:FAST_AUDIT_NUMBERS
$oldFastAuditWorkers = $env:FAST_AUDIT_WORKERS
$oldFastAuditResume = $env:FAST_AUDIT_RESUME
if ($Resume) { $env:FAST_AUDIT_RESUME = '1' }
$validationMutex = New-Object Threading.Mutex($false, 'Local\LinkDispositionCheckerRepresentativeValidation')
$validationLockTaken = $false
try {
    $validationLockTaken = $validationMutex.WaitOne(0)
} catch [Threading.AbandonedMutexException] {
    $validationLockTaken = $true
}
if (-not $validationLockTaken) {
    $validationMutex.Dispose()
    throw 'Another representative validation is already running. Wait for it to finish before starting a new real-data experiment.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$testData = Join-Path $PSScriptRoot 'test-data'
if ([String]::IsNullOrWhiteSpace($InputCsv)) {
    if (-not $FixedRegression) {
        throw 'Formal experiments require an explicit fresh input. Use Run-RotatingValidation.ps1, or pass -FixedRegression only for the fixed regression set.'
    }
    $InputCsv = Join-Path $testData 'representative-validation-samples.csv'
}
if (-not (Test-Path -LiteralPath $InputCsv -PathType Leaf)) {
    throw "Representative validation input was not found: $InputCsv"
}
$InputCsv = [IO.Path]::GetFullPath($InputCsv)
if ([String]::IsNullOrWhiteSpace($OutputCsv)) {
    $OutputCsv = Join-Path $testData 'representative-validation-result-4.5.5-report.csv'
}
$OutputCsv = [IO.Path]::GetFullPath($OutputCsv)
$outputDirectory = Split-Path -Parent $OutputCsv
if (-not [String]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$aiSource = Join-Path $PSScriptRoot 'AiReview.cs'
$logSource = Join-Path $PSScriptRoot 'RunLogging.cs'
$acceptanceSource = Join-Path $PSScriptRoot 'AcceptanceEvidence.cs'
$chinaEyeballSource = Join-Path $PSScriptRoot 'ChinaEyeballEvidence.cs'
$runnerSource = Join-Path $PSScriptRoot 'FastAuditRunner.cs'
$checkpointSource = Join-Path $PSScriptRoot 'AuditCheckpointStore.cs'
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
$webViewLoader = Join-Path $dependencyRoot 'x64\WebView2Loader.dll'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runDirectory = Join-Path $env:TEMP ('LinkCheckerRepresentative_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null

try {
    Copy-Item -LiteralPath $webViewCore -Destination $runDirectory
    Copy-Item -LiteralPath $webViewForms -Destination $runDirectory
    Copy-Item -LiteralPath $webViewLoader -Destination $runDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runDirectory
    $runner = Join-Path $runDirectory 'Representative.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$runner /main:FastAuditRunner `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll `
        /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms $source $aiSource $logSource $acceptanceSource $chinaEyeballSource $checkpointSource $runnerSource
    if ($LASTEXITCODE -ne 0) { throw 'Representative runner compilation failed.' }

    $arguments = '"' + $InputCsv + '" "' + $OutputCsv + '"'
$runnerEnvironment = @{}
foreach ($name in @('FAST_AUDIT_NUMBERS','FAST_AUDIT_WORKERS','FAST_AUDIT_RESUME')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not [String]::IsNullOrWhiteSpace($value)) { $runnerEnvironment[$name] = $value }
}
# Use the current process environment for the child so targeted reproductions
# and worker settings follow the same runner path as the desktop batch.
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
    $unresolved = $review + $temporary + $unavailable
    $minimumResolvedRateDecimal = [decimal]$MinimumResolvedRate
    $rate = if ($rows.Count -eq 0) { [decimal]0 } else { [decimal]100 * [decimal]$unresolved / [decimal]$rows.Count }
    $resolvedRate = if ($rows.Count -eq 0) { [decimal]0 } else { [decimal]($removed + $alive) / [decimal]$rows.Count }
    $unresolvedRate = if ($rows.Count -eq 0) { [decimal]1 } else { [decimal]$unresolved / [decimal]$rows.Count }
    Write-Host "TOTAL=$($rows.Count)"
    Write-Host "REMOVED=$removed"
    Write-Host "ALIVE=$alive"
    Write-Host "PUBLIC_UNAVAILABLE=$unavailable"
    Write-Host "TEMPORARY=$temporary"
    Write-Host "REVIEW=$review"
    Write-Host "UNRESOLVED=$unresolved"
    Write-Host ("UNRESOLVED_RATE={0:0.00}%" -f $rate)
    Write-Host ("RESOLVED_RATE={0:0.00}%" -f (100 * $resolvedRate))
    Write-Host ("RESOLVED_RATE_TARGET={0:0.00}%" -f (100 * $MinimumResolvedRate))
    Write-Host "OUTPUT=$OutputCsv"
    # Historical disposition is useful for finding conflicts, but it is not current release truth.
    & (Join-Path $PSScriptRoot 'Compare-HumanValidation.ps1') -InputCsv $InputCsv -OutputCsv $OutputCsv
    if ($ReportOnly) {
        Write-Host 'COVERAGE_GATE=NOT_RUN'
    }
    else {
        $strictUnresolvedGate = $minimumResolvedRateDecimal -gt 0
        if ($resolvedRate -lt $minimumResolvedRateDecimal -or
            ($strictUnresolvedGate -and $unresolvedRate -ge ([decimal]1 - $minimumResolvedRateDecimal))) {
            throw ("Coverage failed: resolved {0:0.00}% (target >= {1:0.00}%), unresolved {2:0.00}% (target < {3:0.00}%)." -f `
                (100 * $resolvedRate), (100 * $MinimumResolvedRate), (100 * $unresolvedRate), (100 * (1.0 - $MinimumResolvedRate)))
        }
        Write-Host 'COVERAGE_GATE=PASSED'
    }
}
finally {
    $env:FAST_AUDIT_NUMBERS = $oldFastAuditNumbers
    $env:FAST_AUDIT_WORKERS = $oldFastAuditWorkers
    $env:FAST_AUDIT_RESUME = $oldFastAuditResume
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($validationLockTaken) { try { $validationMutex.ReleaseMutex() } catch {} }
    $validationMutex.Dispose()
}
