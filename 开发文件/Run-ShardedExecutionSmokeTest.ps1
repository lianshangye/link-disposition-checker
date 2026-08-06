$ErrorActionPreference = 'Stop'

$portProbe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$port = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
$portProbe.Stop()

$server = Start-Job -ArgumentList $port -ScriptBlock {
    param($ServerPort)
    $listener = New-Object Net.HttpListener
    $listener.Prefixes.Add("http://127.0.0.1:$ServerPort/")
    $listener.Start()
    $retryTargetsAvailable = $false
    try {
        while ($true) {
            $context = $listener.GetContext()
            if ($context.Request.Url.AbsolutePath -eq '/stop') {
                $context.Response.StatusCode = 204
                $context.Response.Close()
                break
            }
            $path = $context.Request.Url.AbsolutePath
            if ($path -eq '/enable-retry-targets') {
                $retryTargetsAvailable = $true
                $context.Response.StatusCode = 204
                $context.Response.Close()
                continue
            }
            if ($path.StartsWith('/retry/') -and -not $retryTargetsAvailable) {
                $body = [Text.Encoding]::UTF8.GetBytes('<html><head><title>Temporary</title></head><body>temporary upstream failure</body></html>')
                $context.Response.StatusCode = 503
                $context.Response.ContentType = 'text/html; charset=utf-8'
                $context.Response.ContentLength64 = $body.Length
                $context.Response.OutputStream.Write($body, 0, $body.Length)
                $context.Response.Close()
                continue
            }
            $body = [Text.Encoding]::UTF8.GetBytes('<html><head><title>Shard Smoke</title></head><body><article>Shard Smoke target content is present.</article></body></html>')
            $context.Response.StatusCode = 200
            $context.Response.ContentType = 'text/html; charset=utf-8'
            $context.Response.ContentLength64 = $body.Length
            $context.Response.OutputStream.Write($body, 0, $body.Length)
            $context.Response.Close()
        }
    }
    finally { $listener.Stop() }
}

$root = Join-Path $env:TEMP ('LinkCheckerShardSmoke_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 30 -and -not $ready; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$port/ready" -TimeoutSec 1 | Out-Null
            $ready = $true
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Local smoke-test server did not start.' }

    $input = Join-Path $root 'input.csv'
    $output = Join-Path $root 'output.csv'
    $writer = New-Object IO.StreamWriter($input, $false, (New-Object Text.UTF8Encoding($true)))
    try {
        $genericPlatform = -join @([char]0x7F51, [char]0x5A92)
        $writer.WriteLine('"url","title","excerpt","platform"')
        for ($index = 1; $index -le 205; $index++) {
            $excerpt = if ($index -eq 100) { '"line one' + "`r`n" + 'line two"' } else { '"target content"' }
            $path = if ($index -le 101) { 'retry/' + $index } else { 'item/' + $index }
            $writer.WriteLine(('"http://127.0.0.1:{0}/{1}","Shard Smoke",{2},"{3}"' -f $port, $path, $excerpt, $genericPlatform))
        }
        $writer.WriteLine('"invalid-url","ignored","ignored","local"')
    }
    finally { $writer.Dispose() }

    $runner = Join-Path $PSScriptRoot 'Run-ShardedFastAudit.ps1'
    $firstOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner `
        -InputCsv $input -OutputCsv $output -RowsPerShard 100 -Workers 8 -Resume 2>&1
    $firstOutput | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "First sharded run exited with code $LASTEXITCODE." }

    $rows = @(Import-Csv -LiteralPath $output)
    $manifest = Get-Content -Raw -LiteralPath ($output + '.shards.json') | ConvertFrom-Json
    $verdictHeader = -join @([char]0x6838, [char]0x9A8C, [char]0x7ED3, [char]0x679C)
    $statusHeader = -join @([char]0x0048, [char]0x0054, [char]0x0054, [char]0x0050, [char]0x72B6, [char]0x6001)
    $infrastructureStatus = -join @([char]0x57FA, [char]0x7840, [char]0x8BBE, [char]0x65BD, [char]0x5F02, [char]0x5E38)
    $aliveLabel = -join @([char]0x4ECD, [char]0x53EF, [char]0x8BBF, [char]0x95EE)
    $removedLabel = -join @([char]0x5DF2, [char]0x5931, [char]0x6548)
    if ($rows.Count -ne 205) { throw "Result count mismatch: $($rows.Count) != 205" }
    if (@($manifest.Shards).Count -ne 3) { throw "Shard count mismatch: $(@($manifest.Shards).Count) != 3" }
    if (@($manifest.Shards | Where-Object { $_.Status -ne 'completed' }).Count -ne 0) { throw 'Not all execution shards completed.' }
    $firstPending = @($rows | Where-Object { $_.$verdictHeader -notin @($aliveLabel, $removedLabel) })
    if ($firstPending.Count -ne 101) { throw "Expected 101 retryable rows after the first pass, found $($firstPending.Count)." }
    $deferred = @($rows | Where-Object { $_.$statusHeader -eq $infrastructureStatus })
    if ($deferred.Count -lt 1 -or $deferred.Count -ge 100) {
        throw "Expected bounded real probes followed by deferred infrastructure rows, found $($deferred.Count) deferred."
    }

    $resumeWatch = [Diagnostics.Stopwatch]::StartNew()
    $resumeOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner `
        -InputCsv $input -OutputCsv $output -RowsPerShard 100 -Workers 8 -Resume 2>&1
    $resumeWatch.Stop()
    if ($LASTEXITCODE -ne 0) { throw "Resume run exited with code $LASTEXITCODE." }
    if (@($resumeOutput | ForEach-Object { [string]$_ } | Where-Object { $_ -like 'SHARD_COMPLETED=*' }).Count -ne 0) {
        throw 'Resume run unexpectedly rechecked a completed shard.'
    }
    Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$port/enable-retry-targets" -TimeoutSec 2 | Out-Null
    $retryOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner `
        -InputCsv $input -OutputCsv $output -RowsPerShard 100 -Workers 8 -Resume -RetryUnresolved 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Retry-unresolved run exited with code $LASTEXITCODE." }
    if (@($retryOutput | ForEach-Object { [string]$_ } | Where-Object { $_ -eq 'CHECKPOINT_RETRY_UNRESOLVED=1' }).Count -ne 3 -or
        @($retryOutput | ForEach-Object { [string]$_ } | Where-Object { $_ -like 'SHARD_COMPLETED=*' }).Count -ne 3) {
        throw 'RetryUnresolved did not enter each completed shard checkpoint.'
    }
    $retryText = @($retryOutput | ForEach-Object { [string]$_ })
    if (@($retryText | Where-Object { $_ -eq 'RETRY_UNRESOLVED_NUMBERS=100' }).Count -ne 1 -or
        @($retryText | Where-Object { $_ -eq 'RETRY_UNRESOLVED_NUMBERS=1' }).Count -ne 1 -or
        @($retryText | Where-Object { $_ -eq 'RETRY_UNRESOLVED_NUMBERS=0' }).Count -ne 1 -or
        @($retryText | Where-Object { $_ -eq 'SELECTED_NUMBERS=100' }).Count -ne 1 -or
        @($retryText | Where-Object { $_ -eq 'SELECTED_NUMBERS=1' }).Count -ne 1 -or
        @($retryText | Where-Object { $_ -like 'PHASE=check,*PENDING=100' }).Count -ne 1 -or
        @($retryText | Where-Object { $_ -like 'PHASE=check,*PENDING=1' }).Count -ne 1) {
        throw 'RetryUnresolved did not limit network work to the 101 unresolved rows across shards.'
    }
    $retriedRows = @(Import-Csv -LiteralPath $output)
    if (@($retriedRows | Where-Object { $_.$verdictHeader -ne $aliveLabel }).Count -ne 0) {
        throw 'The two targeted retry rows did not resolve without rechecking completed rows.'
    }

    Write-Host "SMOKE_ROWS=$($rows.Count)"
    Write-Host "SMOKE_SHARDS=$(@($manifest.Shards).Count)"
    Write-Host 'SMOKE_FIRST_PASS_PENDING=101'
    Write-Host "SMOKE_INFRASTRUCTURE_DEFERRED=$($deferred.Count)"
    Write-Host 'SMOKE_RETRY_REQUESTS=101'
    Write-Host 'SMOKE_ALIVE_AFTER_RETRY=205'
    Write-Host "RESUME_SECONDS=$($resumeWatch.Elapsed.TotalSeconds.ToString('0.00'))"
    Write-Host 'SHARDED_EXECUTION_SMOKE_PASSED=1'
}
finally {
    try { Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$port/stop" -TimeoutSec 1 | Out-Null } catch { }
    Stop-Job $server -ErrorAction SilentlyContinue
    Remove-Job $server -Force -ErrorAction SilentlyContinue
    $temporaryRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    if ($resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedRoot)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
