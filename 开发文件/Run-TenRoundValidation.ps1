param(
    [int]$Rounds = 10,
    [int]$SampleRows = 240,
    [int]$RowsPerPlatformOrDomain = 3,
    [int]$MinimumNetMediaRows = 80,
    [int]$MinimumPlatforms = 50,
    [int]$MinimumHosts = 100,
    [string]$SourceList = '',
    [string]$HistoryCsv = '',
    [string]$OutputDirectory = '',
    [string]$SeedPrefix = '',
    [int]$Workers = 6,
    [double]$MaximumUnresolvedRate = 0.05,
    [int]$MaximumNetworkAttempts = 3,
    [switch]$Resume
)

$ErrorActionPreference = 'Stop'
if ($Rounds -lt 1) { throw 'Rounds must be at least one.' }
if ($SampleRows -lt 1) { throw 'SampleRows must be at least one.' }
if ($MaximumUnresolvedRate -le 0 -or $MaximumUnresolvedRate -ge 1) {
    throw 'MaximumUnresolvedRate must be between zero and one.'
}
if ($MaximumNetworkAttempts -lt 1) { throw 'MaximumNetworkAttempts must be at least one.' }
if ($MinimumPlatforms -lt 1 -or $MinimumHosts -lt 1) { throw 'Coverage minimums must be positive.' }
. (Join-Path $PSScriptRoot 'ValidationNetworkGate.ps1')

$testData = Join-Path $PSScriptRoot 'test-data'
if ([String]::IsNullOrWhiteSpace($SourceList)) {
    $SourceList = Join-Path $testData 'rotating-validation-sources.txt'
}
if ([String]::IsNullOrWhiteSpace($HistoryCsv)) {
    $HistoryCsv = Join-Path $testData 'rotating-validation-history.csv'
}
if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $testData ('ten-round-validation-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if ([String]::IsNullOrWhiteSpace($SeedPrefix)) {
    $SeedPrefix = 'ten-round-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
}

$SourceList = [IO.Path]::GetFullPath($SourceList)
$HistoryCsv = [IO.Path]::GetFullPath($HistoryCsv)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $SourceList -PathType Leaf)) {
    throw "Validation source list was not found: $SourceList"
}
$inputPaths = @(Get-Content -LiteralPath $SourceList -Encoding UTF8 | ForEach-Object { $_.Trim() } |
    Where-Object { -not [String]::IsNullOrWhiteSpace($_) -and -not $_.StartsWith('#') })
if ($inputPaths.Count -eq 0) { throw 'The validation source list is empty.' }
$missingInputs = @($inputPaths | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
if ($missingInputs.Count -gt 0) {
    throw ('Validation inputs were not found: ' + ($missingInputs -join '; '))
}

if ((Test-Path -LiteralPath $OutputDirectory) -and -not $Resume -and
    @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
    throw 'OutputDirectory already contains files. Use -Resume or choose a new directory.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$summaryCsv = Join-Path $OutputDirectory 'round-summary.csv'
$breakdownCsv = Join-Path $OutputDirectory 'unresolved-breakdown.csv'
$manifest = Join-Path $OutputDirectory 'run-manifest.txt'
$startedAt = Get-Date
$manifestLines = @(
    'STARTED_AT=' + $startedAt.ToString('yyyy-MM-dd HH:mm:ss'),
    'ROUNDS=' + $Rounds,
    'SAMPLE_ROWS=' + $SampleRows,
    'MINIMUM_NETMEDIA_ROWS=' + $MinimumNetMediaRows,
    'MINIMUM_PLATFORMS=' + $MinimumPlatforms,
    'MINIMUM_HOSTS=' + $MinimumHosts,
    'MAXIMUM_UNRESOLVED_RATE=' + $MaximumUnresolvedRate.ToString('0.0000', [Globalization.CultureInfo]::InvariantCulture),
    'MAXIMUM_NETWORK_ATTEMPTS=' + $MaximumNetworkAttempts,
    'SEED_PREFIX=' + $SeedPrefix,
    'SOURCE_LIST=' + $SourceList,
    'HISTORY=' + $HistoryCsv
)
if ($Resume -and (Test-Path -LiteralPath $manifest)) {
    @('RESUMED_AT=' + $startedAt.ToString('yyyy-MM-dd HH:mm:ss')) |
        Add-Content -LiteralPath $manifest -Encoding UTF8
}
else {
    $manifestLines | Set-Content -LiteralPath $manifest -Encoding UTF8
}

$oldWorkers = $env:FAST_AUDIT_WORKERS
$roundSummaries = New-Object Collections.Generic.List[object]
$breakdowns = New-Object Collections.Generic.List[object]
$removedLabel = -join @([char]0x5DF2, [char]0x5931, [char]0x6548)
$aliveLabel = -join @([char]0x4ECD, [char]0x53EF, [char]0x8BBF, [char]0x95EE)
$platformHeader = -join @([char]0x5E73, [char]0x53F0, [char]0x540D, [char]0x79F0)
$urlHeader = -join @([char]0x94FE, [char]0x63A5)
$netMediaLabel = -join @([char]0x7F51, [char]0x5A92)
function Write-AttemptLine([string]$Message, [string]$AttemptLog, [string]$RoundLog) {
    Write-Host $Message
    $Message | Add-Content -LiteralPath $AttemptLog -Encoding UTF8
    $Message | Add-Content -LiteralPath $RoundLog -Encoding UTF8
}
try {
    $env:FAST_AUDIT_WORKERS = [string][Math]::Max(1, $Workers)
    for ($round = 1; $round -le $Rounds; $round++) {
        $roundName = 'round-{0:00}' -f $round
        $sampleCsv = Join-Path $OutputDirectory ($roundName + '-sample.csv')
        $resultCsv = Join-Path $OutputDirectory ($roundName + '-result.csv')
        $logPath = Join-Path $OutputDirectory ($roundName + '.log')
        $seed = $SeedPrefix + '-' + ('{0:00}' -f $round)
        $roundStartedAt = Get-Date
        $executionError = ''

        Write-Host "TEN_ROUND_START=$round/$Rounds"
        try {
            $existingResults = if (Test-Path -LiteralPath $resultCsv) {
                @(Import-Csv -LiteralPath $resultCsv)
            }
            else { @() }
            if ($Resume -and $existingResults.Count -eq $SampleRows) {
                "TEN_ROUND_REUSED_RESULT=$round" | Tee-Object -LiteralPath $logPath
            }
            elseif (-not (Test-Path -LiteralPath $sampleCsv)) {
                & (Join-Path $PSScriptRoot 'Run-RotatingValidation.ps1') -InputPath $inputPaths `
                    -SampleRows $SampleRows -RowsPerPlatformOrDomain $RowsPerPlatformOrDomain `
                    -MinimumNetMediaRows $MinimumNetMediaRows -Seed $seed -SampleCsv $sampleCsv `
                    -ResultCsv $resultCsv -HistoryCsv $HistoryCsv -MinimumResolvedRate 0 -BuildOnly `
                    *>&1 | Tee-Object -LiteralPath $logPath
            }
            if (-not ($Resume -and $existingResults.Count -eq $SampleRows)) {
                $acceptedAttempt = $false
                for ($attempt = 1; $attempt -le $MaximumNetworkAttempts; $attempt++) {
                    $attemptName = $roundName + '-attempt-{0:00}' -f $attempt
                    $attemptResult = Join-Path $OutputDirectory ($attemptName + '-result.csv')
                    $attemptLog = Join-Path $OutputDirectory ($attemptName + '.log')
                    $networkCsv = Join-Path $OutputDirectory ($attemptName + '-network.csv')
                    $network = Invoke-ValidationNetworkGate -EvidenceCsv $networkCsv
                    $networkLine = "NETWORK_GATE_ATTEMPT={0}|PASS={1}|SNAPSHOTS={2}/{3}|EVIDENCE={4}" -f `
                        $attempt, $network.Passed, $network.PassingSnapshots, $network.RequiredSnapshots, $networkCsv
                    Write-AttemptLine $networkLine $attemptLog $logPath
                    if (-not $network.Passed) { continue }
                    $attemptOutput = & (Join-Path $PSScriptRoot 'Run-ShardedFastAudit.ps1') `
                        -InputCsv $sampleCsv -OutputCsv $attemptResult `
                        -RowsPerShard ([Math]::Max(100, $SampleRows)) -Workers $Workers `
                        *>&1
                    $attemptOutput | ForEach-Object { Write-Host $_ }
                    $attemptOutput | Out-File -FilePath $attemptLog -Append -Encoding utf8
                    $attemptOutput | Out-File -FilePath $logPath -Append -Encoding utf8
                    $attemptRows = if (Test-Path -LiteralPath $attemptResult) { @(Import-Csv -LiteralPath $attemptResult) } else { @() }
                    if (Test-ValidationResultNetworkInvalid -Rows $attemptRows) {
                        Write-AttemptLine "NETWORK_INVALID_ATTEMPT=$attempt|RESULT=$attemptResult" $attemptLog $logPath
                        continue
                    }
                    Copy-Item -LiteralPath $attemptResult -Destination $resultCsv -Force
                    Write-AttemptLine "NETWORK_VALID_ATTEMPT=$attempt|RESULT=$attemptResult" $attemptLog $logPath
                    $acceptedAttempt = $true
                    break
                }
                if (-not $acceptedAttempt) {
                    throw "No network-valid result after $MaximumNetworkAttempts attempts; invalid attempts were preserved."
                }
            }
        }
        catch {
            $executionError = $_.Exception.Message
            $_ | Out-String | Add-Content -LiteralPath $logPath -Encoding UTF8
        }

        $sample = if (Test-Path -LiteralPath $sampleCsv) { @(Import-Csv -LiteralPath $sampleCsv) } else { @() }
        $results = if (Test-Path -LiteralPath $resultCsv) { @(Import-Csv -LiteralPath $resultCsv) } else { @() }
        $resultColumn = if ($results.Count -gt 0) { @($results[0].PSObject.Properties)[1].Name } else { '' }
        $removed = @($results | Where-Object { [string]$_.$resultColumn -eq $removedLabel }).Count
        $alive = @($results | Where-Object { [string]$_.$resultColumn -eq $aliveLabel }).Count
        $unresolved = $results.Count - $removed - $alive
        $unresolvedRate = if ($results.Count -eq 0) { [decimal]1 } else {
            [decimal]$unresolved / [decimal]$results.Count
        }
        $complete = $sample.Count -eq $SampleRows -and $results.Count -eq $SampleRows -and
            [String]::IsNullOrWhiteSpace($executionError)
        $samplePlatforms = @($sample | ForEach-Object { $_.$platformHeader } |
            Where-Object { -not [String]::IsNullOrWhiteSpace($_) } | Select-Object -Unique).Count
        $sampleHosts = @($sample | ForEach-Object {
            try { ([Uri]$_.$urlHeader).DnsSafeHost.ToLowerInvariant() } catch { '' }
        } | Where-Object { $_ } | Select-Object -Unique).Count
        $netMediaRows = @($sample | Where-Object { $_.$platformHeader -eq $netMediaLabel }).Count
        $strictlyBelowTarget = $complete -and $unresolvedRate -lt [decimal]$MaximumUnresolvedRate
        $coveragePassed = $samplePlatforms -ge $MinimumPlatforms -and
            $sampleHosts -ge $MinimumHosts -and $netMediaRows -ge $MinimumNetMediaRows
        $passed = $strictlyBelowTarget -and $coveragePassed

        $roundSummaries.Add([pscustomobject]@{
            Round = $round
            Seed = $seed
            SampleRows = $sample.Count
            ResultRows = $results.Count
            Platforms = $samplePlatforms
            Hosts = $sampleHosts
            NetMediaRows = $netMediaRows
            Alive = $alive
            Removed = $removed
            Unresolved = $unresolved
            UnresolvedRate = [Math]::Round([double]$unresolvedRate, 6)
            StrictlyBelowTarget = $strictlyBelowTarget
            CoveragePassed = $coveragePassed
            Passed = $passed
            StartedAt = $roundStartedAt.ToString('yyyy-MM-dd HH:mm:ss')
            CompletedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            Error = $executionError
            SampleCsv = $sampleCsv
            ResultCsv = $resultCsv
        })

        if ($results.Count -gt 0) {
            $platformColumn = @($results[0].PSObject.Properties)[11].Name
            $infrastructureColumn = @($results[0].PSObject.Properties)[21].Name
            $results | Where-Object { [string]$_.$resultColumn -notin @($aliveLabel, $removedLabel) } |
                Group-Object -Property $platformColumn, $infrastructureColumn, $resultColumn | ForEach-Object {
                    $breakdowns.Add([pscustomobject]@{
                        Round = $round
                        Platform = [string]$_.Group[0].$platformColumn
                        Infrastructure = [string]$_.Group[0].$infrastructureColumn
                        Verdict = [string]$_.Group[0].$resultColumn
                        Count = $_.Count
                    })
                }
        }

        $roundSummaries | Export-Csv -LiteralPath $summaryCsv -NoTypeInformation -Encoding UTF8
        $breakdowns | Export-Csv -LiteralPath $breakdownCsv -NoTypeInformation -Encoding UTF8
        Write-Host ("TEN_ROUND_RESULT={0}|UNRESOLVED={1}|RATE={2:0.00}%|COVERAGE={3}|PASS={4}" -f `
            $round, $unresolved, (100 * $unresolvedRate), $coveragePassed, $passed)
    }
}
finally {
    $env:FAST_AUDIT_WORKERS = $oldWorkers
}

$totalRows = ($roundSummaries | Measure-Object -Property ResultRows -Sum).Sum
$totalUnresolved = ($roundSummaries | Measure-Object -Property Unresolved -Sum).Sum
$cumulativeRate = if ($totalRows -eq 0) { [decimal]1 } else {
    [decimal]$totalUnresolved / [decimal]$totalRows
}
$passedRounds = @($roundSummaries | Where-Object Passed).Count
$allPassed = $roundSummaries.Count -eq $Rounds -and $passedRounds -eq $Rounds -and
    $totalRows -eq ($Rounds * $SampleRows) -and $cumulativeRate -lt [decimal]$MaximumUnresolvedRate
$completedAt = Get-Date
@(
    'COMPLETED_AT=' + $completedAt.ToString('yyyy-MM-dd HH:mm:ss'),
    'ELAPSED=' + ($completedAt - $startedAt).ToString(),
    'PASSED_ROUNDS=' + $passedRounds,
    'TOTAL_RESULT_ROWS=' + $totalRows,
    'TOTAL_UNRESOLVED=' + $totalUnresolved,
    'CUMULATIVE_UNRESOLVED_RATE=' + $cumulativeRate.ToString('0.000000', [Globalization.CultureInfo]::InvariantCulture),
    'ALL_ROUNDS_PASSED=' + $allPassed,
    'SUMMARY=' + $summaryCsv,
    'BREAKDOWN=' + $breakdownCsv
) | Add-Content -LiteralPath $manifest -Encoding UTF8

$gate = if ($allPassed) { 'PASSED' } else { 'FAILED' }
Write-Host "TEN_ROUND_PASSED_ROUNDS=$passedRounds/$Rounds"
Write-Host "TEN_ROUND_TOTAL_ROWS=$totalRows"
Write-Host "TEN_ROUND_TOTAL_UNRESOLVED=$totalUnresolved"
Write-Host ("TEN_ROUND_CUMULATIVE_RATE={0:0.00}%" -f (100 * $cumulativeRate))
Write-Host "TEN_ROUND_GATE=$gate"
Write-Host "TEN_ROUND_OUTPUT=$OutputDirectory"
if (-not $allPassed) { exit 2 }
