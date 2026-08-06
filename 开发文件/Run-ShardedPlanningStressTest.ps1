param(
    [int]$TotalRows = 300000,
    [int]$RowsPerShard = 10000
)

$ErrorActionPreference = 'Stop'
if ($TotalRows -lt 1000) { throw 'TotalRows must be at least 1000.' }
if ($RowsPerShard -lt 100) { throw 'RowsPerShard must be at least 100.' }

function ConvertTo-CsvRecord([string[]]$Fields) {
    $escaped = New-Object 'string[]' $Fields.Count
    for ($index = 0; $index -lt $Fields.Count; $index++) {
        $escaped[$index] = '"' + ([string]$Fields[$index]).Replace('"', '""') + '"'
    }
    return [String]::Join(',', $escaped)
}

$root = Join-Path $env:TEMP ('LinkCheckerShardScale_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$input = Join-Path $root 'scale-input.csv'
$output = Join-Path $root 'scale-result.csv'
$specialRow = [Math]::Min($TotalRows, $RowsPerShard)
$invalidRows = [Math]::Floor($TotalRows / 1000)
$expectedValid = $TotalRows - $invalidRows
$expectedShards = [Math]::Ceiling($TotalRows / [double]$RowsPerShard)
$watch = [Diagnostics.Stopwatch]::StartNew()

try {
    $writer = New-Object IO.StreamWriter($input, $false, (New-Object Text.UTF8Encoding($true)))
    try {
        $writer.WriteLine((ConvertTo-CsvRecord @('url', 'title', 'excerpt', 'platform')))
        for ($index = 1; $index -le $TotalRows; $index++) {
            $url = if (($index % 1000) -eq 0) { 'not-a-url-' + $index } else { 'https://example.test/article/' + $index }
            $title = if ($index -eq $specialRow) { 'title with, comma and "quotes"' } else { 'stress title ' + $index }
            $excerpt = if ($index -eq $specialRow) { "first line`r`nsecond line in the same record" } else { 'excerpt ' + $index }
            $writer.WriteLine((ConvertTo-CsvRecord @($url, $title, $excerpt, 'local-stress')))
        }
    }
    finally { $writer.Dispose() }

    $runner = Join-Path $PSScriptRoot 'Run-ShardedFastAudit.ps1'
    $planOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner `
        -InputCsv $input -OutputCsv $output -RowsPerShard $RowsPerShard -PlanOnly 2>&1
    $planOutput | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Sharded planner exited with code $LASTEXITCODE." }

    $planPath = [string](@($planOutput | ForEach-Object { [string]$_ } | Where-Object { $_ -like 'SHARD_PLAN=*' } | Select-Object -Last 1) -replace '^SHARD_PLAN=', '')
    if ([String]::IsNullOrWhiteSpace($planPath) -or -not (Test-Path -LiteralPath $planPath)) { throw 'Planner did not produce a readable shard plan.' }
    $plan = Get-Content -Raw -LiteralPath $planPath | ConvertFrom-Json
    if ([int]$plan.TotalRecords -ne $TotalRows) { throw "Record count mismatch: $($plan.TotalRecords) != $TotalRows" }
    if ([int]$plan.TotalValidUrls -ne $expectedValid) { throw "Valid URL count mismatch: $($plan.TotalValidUrls) != $expectedValid" }
    if (@($plan.Shards).Count -ne $expectedShards) { throw "Shard count mismatch: $(@($plan.Shards).Count) != $expectedShards" }
    if ((@($plan.Shards | Measure-Object -Property RecordCount -Sum)[0].Sum) -ne $TotalRows) { throw 'Shard record totals do not add up.' }
    if ((@($plan.Shards | Measure-Object -Property ValidCount -Sum)[0].Sum) -ne $expectedValid) { throw 'Shard valid URL totals do not add up.' }

    $specialShard = @($plan.Shards | Where-Object { [int]$_.Index -eq [Math]::Floor(($specialRow - 1) / $RowsPerShard) })[0]
    $specialPath = Join-Path (Split-Path -Parent $planPath) $specialShard.FileName
    $special = @(Import-Csv -LiteralPath $specialPath | Where-Object { $_.title -like 'title with*' })
    if ($special.Count -ne 1 -or $special[0].title -ne 'title with, comma and "quotes"' -or
        $special[0].excerpt -ne "first line`r`nsecond line in the same record") {
        throw 'Quoted comma, quote, or multiline field was corrupted during sharding.'
    }

    $planTimestamp = (Get-Item -LiteralPath $planPath).LastWriteTimeUtc
    $reuseWatch = [Diagnostics.Stopwatch]::StartNew()
    $reuseOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner `
        -InputCsv $input -OutputCsv $output -RowsPerShard $RowsPerShard -PlanOnly 2>&1
    $reuseWatch.Stop()
    if ($LASTEXITCODE -ne 0) { throw "Second planning pass exited with code $LASTEXITCODE." }
    $reusePlanPath = [string](@($reuseOutput | ForEach-Object { [string]$_ } | Where-Object { $_ -like 'SHARD_PLAN=*' } | Select-Object -Last 1) -replace '^SHARD_PLAN=', '')
    if ($reusePlanPath -ne $planPath -or (Get-Item -LiteralPath $planPath).LastWriteTimeUtc -ne $planTimestamp) {
        throw 'Unchanged input did not reuse the completed shard plan.'
    }

    $watch.Stop()
    Write-Host "STRESS_TOTAL=$TotalRows"
    Write-Host "STRESS_VALID_URLS=$expectedValid"
    Write-Host "STRESS_SHARDS=$expectedShards"
    Write-Host "PLAN_REUSE_SECONDS=$($reuseWatch.Elapsed.TotalSeconds.ToString('0.00'))"
    Write-Host 'PLAN_REUSED=1'
    Write-Host "STRESS_SECONDS=$($watch.Elapsed.TotalSeconds.ToString('0.00'))"
    Write-Host 'SHARDED_PLANNING_STRESS_PASSED=1'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
