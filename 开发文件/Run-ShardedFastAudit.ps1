param(
    [Parameter(Mandatory = $true)][string]$InputCsv,
    [Parameter(Mandatory = $true)][string]$OutputCsv,
    [int]$RowsPerShard = 10000,
    [int]$Workers = 6,
    [int]$PlatformIntervalMs = 800,
    [int]$GenericIntervalMs = 250,
    [switch]$LoginFirst,
    [switch]$UseSavedLogin,
    [switch]$Resume,
    [switch]$RetryUnresolved,
    [switch]$QuickIndependentEvidence,
    [ValidateSet('off','shadow','assist')][string]$AiMode = 'off',
    [int]$AiMaxCandidates = 50,
    [int]$AiWorkers = 3,
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
$planningWatch = [Diagnostics.Stopwatch]::StartNew()
if ($RowsPerShard -lt 100) { throw 'RowsPerShard must be at least 100.' }
if ($PlatformIntervalMs -lt 400 -or $PlatformIntervalMs -gt 5000) { throw 'PlatformIntervalMs must be between 400 and 5000.' }
if ($GenericIntervalMs -lt 50 -or $GenericIntervalMs -gt 2000) { throw 'GenericIntervalMs must be between 50 and 2000.' }
if ($AiMaxCandidates -lt 1 -or $AiMaxCandidates -gt 200) { throw 'AiMaxCandidates must be between 1 and 200.' }
if ($AiWorkers -lt 1 -or $AiWorkers -gt 8) { throw 'AiWorkers must be between 1 and 8.' }
if (-not (Test-Path -LiteralPath $InputCsv -PathType Leaf)) { throw "Input CSV not found: $InputCsv" }
$InputCsv = [IO.Path]::GetFullPath($InputCsv)
$OutputCsv = [IO.Path]::GetFullPath($OutputCsv)
$outputDirectory = Split-Path -Parent $OutputCsv
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$inputHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $InputCsv).Hash
$manifestPath = $OutputCsv + '.shards.json'
$shardStem = [IO.Path]::GetFileNameWithoutExtension($OutputCsv)
$shardDirectory = Join-Path $outputDirectory ($shardStem + '.shards.' + $inputHash.Substring(0, 12).ToLowerInvariant() + '.r' + $RowsPerShard)
$planPath = Join-Path $shardDirectory 'shard-plan.json'

function Save-Manifest($manifest) {
    $temporary = $manifestPath + '.tmp'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $manifestPath -Force
}

function Resolve-CsvEncoding([string]$Path) {
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = New-Object IO.StreamReader($stream, $strictUtf8, $true, 65536, $true)
        try {
            $buffer = New-Object char[] 65536
            while ($reader.Read($buffer, 0, $buffer.Length) -gt 0) { }
            return $reader.CurrentEncoding
        }
        finally { $reader.Dispose() }
    }
    catch [Text.DecoderFallbackException] {
        return [Text.Encoding]::Default
    }
    finally { $stream.Dispose() }
}

function New-CsvParser([string]$Path, [Text.Encoding]$Encoding) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($Path, $Encoding, $true)
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    $parser.TrimWhiteSpace = $false
    return $parser
}

function ConvertTo-CsvRecord([string[]]$Fields) {
    $escaped = New-Object 'string[]' $Fields.Count
    for ($index = 0; $index -lt $Fields.Count; $index++) {
        $escaped[$index] = '"' + ([string]$Fields[$index]).Replace('"', '""') + '"'
    }
    return [String]::Join(',', $escaped)
}

function Find-UrlColumn([string[]]$Headers) {
    $names = @('链接', 'url', '网址', '文章链接', '原链接', '发布链接')
    for ($index = 0; $index -lt $Headers.Count; $index++) {
        $header = ([string]$Headers[$index]).Trim().TrimStart([char]0xFEFF).Replace(' ', '').ToLowerInvariant()
        if ($names -contains $header) { return $index }
    }
    return -1
}

function Test-ValidUrl([string]$Value) {
    $uri = $null
    return [Uri]::TryCreate(([string]$Value).Trim(), [UriKind]::Absolute, [ref]$uri) -and
        ($uri.Scheme -eq 'http' -or $uri.Scheme -eq 'https')
}

function Get-ExistingShardPlan {
    if (-not (Test-Path -LiteralPath $planPath -PathType Leaf)) { return $null }
    $plan = Get-Content -Raw -LiteralPath $planPath | ConvertFrom-Json
    if ([int]$plan.FormatVersion -ne 1 -or $plan.InputSha256 -ne $inputHash -or
        [int]$plan.RowsPerShard -ne $RowsPerShard) { return $null }
    foreach ($shard in @($plan.Shards)) {
        if (-not (Test-Path -LiteralPath (Join-Path $shardDirectory $shard.FileName) -PathType Leaf)) { return $null }
    }
    return $plan
}

function New-ShardInputs {
    $existing = Get-ExistingShardPlan
    if ($null -ne $existing) { return $existing }

    if (Test-Path -LiteralPath $shardDirectory) {
        # This path is deterministic and owned by this input hash/shard size.
        # A missing plan marker means an earlier planning pass was interrupted.
        Remove-Item -LiteralPath $shardDirectory -Recurse -Force
    }
    $temporaryDirectory = $shardDirectory + '.planning-' + [Guid]::NewGuid().ToString('N')
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    $parser = $null
    $writer = $null
    try {
        $encoding = Resolve-CsvEncoding $InputCsv
        $parser = New-CsvParser $InputCsv $encoding
        if ($parser.EndOfData) { throw 'Input CSV has no header.' }
        [string[]]$headers = $parser.ReadFields()
        $urlColumn = Find-UrlColumn $headers
        if ($urlColumn -lt 0) { throw 'Input CSV has no URL column.' }

        $shards = @()
        $shardIndex = -1
        $recordCount = 0
        $validCount = 0
        $totalRecords = 0
        $totalValid = 0
        while (-not $parser.EndOfData) {
            [string[]]$fields = $parser.ReadFields()
            if ($null -eq $fields) { continue }
            $hasValue = $false
            foreach ($field in $fields) {
                if (-not [String]::IsNullOrWhiteSpace($field)) { $hasValue = $true; break }
            }
            if (-not $hasValue) { continue }
            if ($null -eq $writer -or $recordCount -ge $RowsPerShard) {
                if ($null -ne $writer) {
                    $writer.Dispose(); $writer = $null
                    $shards += [pscustomobject]@{ Index = $shardIndex; FileName = ('part-{0:D5}.input.csv' -f $shardIndex); RecordCount = $recordCount; ValidCount = $validCount }
                }
                $shardIndex++
                $recordCount = 0
                $validCount = 0
                $path = Join-Path $temporaryDirectory ('part-{0:D5}.input.csv' -f $shardIndex)
                $writer = New-Object IO.StreamWriter($path, $false, (New-Object Text.UTF8Encoding($true)))
                $writer.WriteLine((ConvertTo-CsvRecord $headers))
            }
            $writer.WriteLine((ConvertTo-CsvRecord $fields))
            $recordCount++
            $totalRecords++
            if ($urlColumn -lt $fields.Count -and (Test-ValidUrl $fields[$urlColumn])) { $validCount++; $totalValid++ }
        }
        if ($null -ne $writer) {
            $writer.Dispose(); $writer = $null
            $shards += [pscustomobject]@{ Index = $shardIndex; FileName = ('part-{0:D5}.input.csv' -f $shardIndex); RecordCount = $recordCount; ValidCount = $validCount }
        }
        if ($shards.Count -eq 0) { throw 'Input CSV contains no data rows.' }
        $plan = [pscustomobject]@{
            FormatVersion = 1; InputCsv = $InputCsv; InputSha256 = $inputHash
            RowsPerShard = $RowsPerShard; TotalRecords = $totalRecords; TotalValidUrls = $totalValid
            Shards = $shards; PlannedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        }
        $plan | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $temporaryDirectory 'shard-plan.json') -Encoding UTF8
        Move-Item -LiteralPath $temporaryDirectory -Destination $shardDirectory
        return $plan
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $parser) { $parser.Dispose() }
        if (Test-Path -LiteralPath $temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Get-Runner {
    $root = Split-Path -Parent $PSScriptRoot
    $source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
    $ai = Join-Path $PSScriptRoot 'AiReview.cs'
    $log = Join-Path $PSScriptRoot 'RunLogging.cs'
    $acceptance = Join-Path $PSScriptRoot 'AcceptanceEvidence.cs'
    $china = Join-Path $PSScriptRoot 'ChinaEyeballEvidence.cs'
    $checkpoint = Join-Path $PSScriptRoot 'AuditCheckpointStore.cs'
    $runnerSource = Join-Path $PSScriptRoot 'FastAuditRunner.cs'
    $dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
    $webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
    $webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
    $compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $runDirectory = Join-Path $env:TEMP ('LinkCheckerSharded_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    Copy-Item $webViewCore,$webViewForms,(Join-Path $root 'platform-rules.json') -Destination $runDirectory
    $runner = Join-Path $runDirectory 'FastAuditRunner.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$runner /main:FastAuditRunner `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
        /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll /reference:Microsoft.CSharp.dll `
        /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
        /reference:$webViewCore /reference:$webViewForms $source $ai $log $acceptance $china $checkpoint $runnerSource
    if ($LASTEXITCODE -ne 0) { throw 'Sharded fast runner compilation failed.' }
    return [pscustomobject]@{ Path = $runner; Directory = $runDirectory }
}

$shardPlan = New-ShardInputs
$shardInputs = @($shardPlan.Shards | Sort-Object Index | ForEach-Object { Get-Item -LiteralPath (Join-Path $shardDirectory $_.FileName) })
if ($shardInputs.Count -eq 0) { throw 'Input CSV contains no data rows.' }
if ($PlanOnly) {
    $planningWatch.Stop()
    Write-Host "PLANNED_RECORDS=$($shardPlan.TotalRecords)"
    Write-Host "VALID_URLS=$($shardPlan.TotalValidUrls)"
    Write-Host "SHARDS=$($shardInputs.Count)"
    Write-Host "PLANNING_SECONDS=$($planningWatch.Elapsed.TotalSeconds.ToString('0.00'))"
    Write-Host "PEAK_WORKING_SET_MB=$([Math]::Round([Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64 / 1MB, 1))"
    Write-Host "INPUT_SHA256=$inputHash"
    Write-Host "SHARD_PLAN=$planPath"
    Write-Host 'SHARDED_PLAN_COMPLETED=1'
    return
}
$manifest = $null
if ($Resume -and (Test-Path -LiteralPath $manifestPath)) {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.InputSha256 -ne $inputHash -or [int]$manifest.RowsPerShard -ne $RowsPerShard -or
        [int]$manifest.FormatVersion -lt 3 -or $manifest.ShardDirectory -ne $shardDirectory) {
        throw 'Existing shard manifest belongs to different input or shard size.'
    }
}
if ($null -eq $manifest) {
    $manifest = [pscustomobject]@{ FormatVersion = 3; InputCsv = $InputCsv; InputSha256 = $inputHash; RowsPerShard = $RowsPerShard; ShardDirectory = $shardDirectory; Shards = @(); Status = 'planning'; OutputCsv = ''; CompletedAt = '' }
}
foreach ($property in @(@{Name='Status';Value='planning'}, @{Name='OutputCsv';Value=''}, @{Name='CompletedAt';Value=''})) {
    if (-not $manifest.PSObject.Properties[$property.Name]) { $manifest | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value }
}
$entries = @($manifest.Shards)
$globalOffset = 0
$shardOrdinal = 0
foreach ($inputPart in $shardInputs) {
    $entry = @($entries | Where-Object { $_.InputPath -eq $inputPart.FullName }) | Select-Object -First 1
    $plannedShard = @($shardPlan.Shards | Where-Object { [int]$_.Index -eq $shardOrdinal }) | Select-Object -First 1
    if ($null -eq $plannedShard) { throw "Shard plan entry is missing: $($inputPart.Name)" }
    $validCount = [int]$plannedShard.ValidCount
    if ($null -eq $entry) {
        $resultPart = Join-Path $shardDirectory (($inputPart.BaseName -replace '\.input$','') + '.result.csv')
        $entry = [pscustomobject]@{ Index = $shardOrdinal; InputPath = $inputPart.FullName; OutputPath = $resultPart; Status = 'pending'; StartedAt = ''; CompletedAt = ''; Error = ''; ValidCount = $validCount; NumberOffset = $globalOffset }
        $entries += $entry
    }
    else {
        $entry.Index = $shardOrdinal
        foreach ($property in @(@{Name='ValidCount';Value=$validCount}, @{Name='NumberOffset';Value=$globalOffset})) {
            if ($entry.PSObject.Properties[$property.Name]) { $entry.($property.Name) = $property.Value }
            else { $entry | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value }
        }
    }
    $globalOffset += $validCount
    $shardOrdinal++
}
$manifest.Shards = $entries
Save-Manifest $manifest
$runnerInfo = Get-Runner
$oldWorkers = $env:FAST_AUDIT_WORKERS
$oldResume = $env:FAST_AUDIT_RESUME
$oldOffset = $env:FAST_AUDIT_NUMBER_OFFSET
$oldRetry = $env:FAST_AUDIT_RETRY_UNRESOLVED
$oldCache = $env:FAST_AUDIT_RESULT_CACHE
$oldPlatformInterval = $env:FAST_AUDIT_PLATFORM_INTERVAL_MS
$oldGenericInterval = $env:FAST_AUDIT_GENERIC_INTERVAL_MS
$oldPublicRetry = $env:FAST_AUDIT_PUBLIC_RETRY
$oldSavedLogin = $env:FAST_AUDIT_USE_SAVED_LOGIN
$oldInteractiveLogin = $env:FAST_AUDIT_LOGIN_INTERACTIVE
$oldNumbers = $env:FAST_AUDIT_NUMBERS
$oldQuickIndependent = $env:FAST_AUDIT_QUICK_INDEPENDENT_EVIDENCE
$oldAiMode = $env:FAST_AUDIT_AI_MODE
$oldAiMaxCandidates = $env:FAST_AUDIT_AI_MAX_CANDIDATES
$oldAiWorkers = $env:FAST_AUDIT_AI_WORKERS
$oldCookieHandoff = $env:FAST_AUDIT_COOKIE_HANDOFF
$oldLoginOrigins = $env:FAST_AUDIT_LOGIN_ORIGINS
$cookieHandoffPath = Join-Path $env:TEMP ('LinkCheckerCookieHandoff-' + $inputHash.Substring(0, 16).ToLowerInvariant() + '.bin')
$loginOrigins = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($inputPartForLogin in $shardInputs) {
    foreach ($rowForLogin in @(Import-Csv -LiteralPath $inputPartForLogin.FullName)) {
        $urlForLogin = ''
        foreach ($property in @('链接','url','网址','文章链接','原链接','发布链接')) {
            if ($rowForLogin.PSObject.Properties[$property]) { $urlForLogin = [string]$rowForLogin.$property; if ($urlForLogin) { break } }
        }
        try {
            $uriForLogin = [Uri]$urlForLogin
            if ($uriForLogin.Scheme -in @('http','https')) {
                [void]$loginOrigins.Add($uriForLogin.GetLeftPart([UriPartial]::Authority) + '/')
            }
        } catch { }
    }
}
try {
    $env:FAST_AUDIT_WORKERS = [string][Math]::Max(1, $Workers)
    $env:FAST_AUDIT_PLATFORM_INTERVAL_MS = [string]$PlatformIntervalMs
    $env:FAST_AUDIT_GENERIC_INTERVAL_MS = [string]$GenericIntervalMs
    $env:FAST_AUDIT_PUBLIC_RETRY = '1'
    $env:FAST_AUDIT_QUICK_INDEPENDENT_EVIDENCE = if ($QuickIndependentEvidence) { '1' } else { $null }
    $env:FAST_AUDIT_AI_MODE = if ($AiMode -eq 'off') { $null } else { $AiMode }
    $env:FAST_AUDIT_AI_MAX_CANDIDATES = [string]$AiMaxCandidates
    $env:FAST_AUDIT_AI_WORKERS = [string]$AiWorkers
    $env:FAST_AUDIT_COOKIE_HANDOFF = $cookieHandoffPath
    $env:FAST_AUDIT_LOGIN_ORIGINS = [String]::Join(';', @($loginOrigins | Sort-Object))
    if ($UseSavedLogin -or $LoginFirst) { $env:FAST_AUDIT_USE_SAVED_LOGIN = '1' }
    $interactiveLoginPending = [bool]$LoginFirst
    # A sharded run is resumable by contract.  The manifest and per-shard
    # checkpoint files make an interrupted 300k-row run restart-safe without
    # rechecking completed work.  Use a new output path for a deliberate fresh run.
    $env:FAST_AUDIT_RESUME = '1'
    if ($RetryUnresolved) { $env:FAST_AUDIT_RETRY_UNRESOLVED = '1' }
    foreach ($inputPart in $shardInputs) {
        $resultPart = Join-Path $shardDirectory (($inputPart.BaseName -replace '\.input$','') + '.result.csv')
        $entry = @($entries | Where-Object { $_.InputPath -eq $inputPart.FullName }) | Select-Object -First 1
        if ($null -eq $entry) {
            $entry = [pscustomobject]@{ Index = $entries.Count; InputPath = $inputPart.FullName; OutputPath = $resultPart; Status = 'pending'; StartedAt = ''; CompletedAt = ''; Error = '' }
            $entries += $entry
        }
        if (-not $RetryUnresolved -and $entry.Status -eq 'completed' -and
            (Test-Path -LiteralPath $resultPart)) { continue }
        $entry.Status = 'running'; $entry.StartedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); $entry.Error = ''
        $manifest.Shards = $entries; Save-Manifest $manifest
        try {
            $env:FAST_AUDIT_NUMBER_OFFSET = [string][int]$entry.NumberOffset
            # Keep the durable cache bounded to one shard. A single growing
            # cache would be reparsed by every shard process and becomes
            # quadratic at hundreds of thousands of rows.
            $env:FAST_AUDIT_RESULT_CACHE = $resultPart + '.determinate-cache.jsonl'
            if ($RetryUnresolved -and (Test-Path -LiteralPath $resultPart -PathType Leaf)) {
                $retryNumbers = @(Import-Csv -LiteralPath $resultPart | Where-Object {
                    $_.'核验结果' -notin @('仍可访问','已失效') -and $_.'序号' -match '^\d+$'
                } | ForEach-Object { [string]$_.'序号' })
                $env:FAST_AUDIT_NUMBERS = if ($retryNumbers.Count -gt 0) { [string]::Join(',', $retryNumbers) } else { $null }
                Write-Host "RETRY_UNRESOLVED_NUMBERS=$($retryNumbers.Count)"
            }
            else { $env:FAST_AUDIT_NUMBERS = $null }
            $env:FAST_AUDIT_LOGIN_INTERACTIVE = if ($interactiveLoginPending) { '1' } else { $null }
            $process = Start-Process -FilePath $runnerInfo.Path -ArgumentList ('"' + $inputPart.FullName + '" "' + $resultPart + '"') -WorkingDirectory $runnerInfo.Directory -Wait -PassThru -NoNewWindow
            $interactiveLoginPending = $false
            if ($process.ExitCode -ne 0) { throw "Shard runner exited with code $($process.ExitCode)." }
            if (-not (Test-Path -LiteralPath $resultPart)) { throw 'Shard runner did not produce a result file.' }
            $entry.Status = 'completed'; $entry.CompletedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            Write-Host "SHARD_COMPLETED=$($entry.Index)|OUTPUT=$resultPart"
        }
        catch {
            $entry.Status = 'failed'; $entry.Error = $_.Exception.Message
            $manifest.Shards = $entries; Save-Manifest $manifest
            throw
        }
        $manifest.Shards = $entries; Save-Manifest $manifest
    }
    $first = $true
    $writer = New-Object IO.StreamWriter($OutputCsv, $false, (New-Object Text.UTF8Encoding($true)))
    try {
        foreach ($entry in ($entries | Sort-Object Index)) {
            if (-not (Test-Path -LiteralPath $entry.OutputPath)) { throw "Missing completed shard result: $($entry.OutputPath)" }
            $lines = [IO.File]::ReadLines($entry.OutputPath)
            foreach ($line in $lines) {
                if ($first -or -not $line.StartsWith('序号,')) { $writer.WriteLine($line) }
                $first = $false
            }
        }
    }
    finally { $writer.Dispose() }

    $total = 0
    $alive = 0
    $removed = 0
    $platformStats = @{}
    foreach ($entry in ($entries | Sort-Object Index)) {
        foreach ($row in @(Import-Csv -LiteralPath $entry.OutputPath)) {
            $total++
            $verdict = [string]$row.'核验结果'
            if ($verdict -eq '仍可访问') { $alive++ }
            elseif ($verdict -eq '已失效') { $removed++ }
            $platform = [string]$row.'平台'
            if ([String]::IsNullOrWhiteSpace($platform)) { $platform = 'unknown' }
            if (-not $platformStats.ContainsKey($platform)) {
                $platformStats[$platform] = @{ Total = 0; Resolved = 0 }
            }
            $platformStats[$platform].Total++
            if ($verdict -eq '仍可访问' -or $verdict -eq '已失效') { $platformStats[$platform].Resolved++ }
        }
    }
    $resolved = $alive + $removed
    $pending = $total - $resolved
    $pendingRate = if ($total -eq 0) { 100.0 } else { 100.0 * $pending / $total }
    $strictBelowFive = $total -gt 0 -and ($pending * 20) -lt $total
    $planningWatch.Stop()
    $elapsedSeconds = $planningWatch.Elapsed.TotalSeconds
    $rowsPerSecond = if ($elapsedSeconds -le 0) { 0.0 } else { $total / $elapsedSeconds }
    $summaryPath = $OutputCsv + '.summary.csv'
    $summaryWriter = New-Object IO.StreamWriter($summaryPath, $false, (New-Object Text.UTF8Encoding($true)))
    try {
        $summaryWriter.WriteLine('platform,total,resolved,pending,pending_rate_percent')
        foreach ($platform in @($platformStats.Keys | Sort-Object)) {
            $stats = $platformStats[$platform]
            $platformPending = [int]$stats.Total - [int]$stats.Resolved
            $platformRate = if ([int]$stats.Total -eq 0) { 0 } else { 100.0 * $platformPending / [int]$stats.Total }
            $summaryWriter.WriteLine((ConvertTo-CsvRecord @($platform, [string]$stats.Total, [string]$stats.Resolved,
                [string]$platformPending, $platformRate.ToString('0.00'))))
        }
    }
    finally { $summaryWriter.Dispose() }

    foreach ($property in @(
        @{Name='Total';Value=$total}, @{Name='Resolved';Value=$resolved}, @{Name='Pending';Value=$pending},
        @{Name='PendingRatePercent';Value=$pendingRate}, @{Name='StrictBelowFivePercent';Value=$strictBelowFive},
        @{Name='ElapsedSeconds';Value=$elapsedSeconds}, @{Name='RowsPerSecond';Value=$rowsPerSecond},
        @{Name='SummaryCsv';Value=$summaryPath})) {
        if ($manifest.PSObject.Properties[$property.Name]) { $manifest.($property.Name) = $property.Value }
        else { $manifest | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value }
    }
    $manifest.Status = 'completed'; $manifest.OutputCsv = $OutputCsv; $manifest.CompletedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); Save-Manifest $manifest
    Write-Host "TOTAL=$total"
    Write-Host "ALIVE=$alive"
    Write-Host "REMOVED=$removed"
    Write-Host "CONTRACT_PENDING=$pending"
    Write-Host "UNRESOLVED_RATE=$($pendingRate.ToString('0.00'))%"
    Write-Host "STRICT_BELOW_5_PERCENT=$(if ($strictBelowFive) { '1' } else { '0' })"
    Write-Host "ELAPSED_SECONDS=$($elapsedSeconds.ToString('0.00'))"
    Write-Host "ROWS_PER_SECOND=$($rowsPerSecond.ToString('0.00'))"
    Write-Host "PLATFORM_SUMMARY=$summaryPath"
    Write-Host "SHARDED_AUDIT_COMPLETED=1"
    Write-Host "OUTPUT=$OutputCsv"
}
finally {
    $env:FAST_AUDIT_WORKERS = $oldWorkers
    $env:FAST_AUDIT_RESUME = $oldResume
    $env:FAST_AUDIT_RETRY_UNRESOLVED = $oldRetry
    $env:FAST_AUDIT_NUMBER_OFFSET = $oldOffset
    $env:FAST_AUDIT_RESULT_CACHE = $oldCache
    $env:FAST_AUDIT_PLATFORM_INTERVAL_MS = $oldPlatformInterval
    $env:FAST_AUDIT_GENERIC_INTERVAL_MS = $oldGenericInterval
    $env:FAST_AUDIT_PUBLIC_RETRY = $oldPublicRetry
    $env:FAST_AUDIT_USE_SAVED_LOGIN = $oldSavedLogin
    $env:FAST_AUDIT_LOGIN_INTERACTIVE = $oldInteractiveLogin
    $env:FAST_AUDIT_NUMBERS = $oldNumbers
    $env:FAST_AUDIT_QUICK_INDEPENDENT_EVIDENCE = $oldQuickIndependent
    $env:FAST_AUDIT_AI_MODE = $oldAiMode
    $env:FAST_AUDIT_AI_MAX_CANDIDATES = $oldAiMaxCandidates
    $env:FAST_AUDIT_AI_WORKERS = $oldAiWorkers
    $env:FAST_AUDIT_COOKIE_HANDOFF = $oldCookieHandoff
    $env:FAST_AUDIT_LOGIN_ORIGINS = $oldLoginOrigins
    if (Test-Path -LiteralPath $cookieHandoffPath) {
        Remove-Item -LiteralPath $cookieHandoffPath -Force -ErrorAction SilentlyContinue
    }
    if ($runnerInfo -and (Test-Path -LiteralPath $runnerInfo.Directory)) { Remove-Item -LiteralPath $runnerInfo.Directory -Recurse -Force -ErrorAction SilentlyContinue }
}



