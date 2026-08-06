function Test-ValidationResultNetworkInvalid {
    param(
        [object[]]$Rows,
        [int]$MinimumAffectedRows = 12,
        [int]$MinimumAffectedHosts = 4,
        [double]$MinimumAffectedRate = 0.08
    )

    $rowsArray = @($Rows)
    if ($rowsArray.Count -eq 0) { return $false }
    $temporaryLabel = -join @([char]0x6682, [char]0x65F6, [char]0x5F02, [char]0x5E38)
    $verdictName = -join @([char]0x6838, [char]0x9A8C, [char]0x7ED3, [char]0x679C)
    $basisName = -join @([char]0x5224, [char]0x5B9A, [char]0x4F9D, [char]0x636E)
    $statusName = 'HTTP' + (-join @([char]0x72B6, [char]0x6001))
    $urlName = -join @([char]0x539F, [char]0x94FE, [char]0x63A5)
    $affected = @($rowsArray | Where-Object {
        $verdict = [string]$_.$verdictName
        $basis = [string]$_.$basisName
        $status = [string]$_.$statusName
        $verdict -eq $temporaryLabel -and
            ($status -eq '502' -or $basis -match 'HTTP\s*502|DNS|NameResolution')
    })
    $hosts = @($affected | ForEach-Object {
        try { ([Uri]([string]$_.$urlName)).DnsSafeHost.ToLowerInvariant() } catch { '' }
    } | Where-Object { $_ } | Select-Object -Unique)
    $requiredByRate = [int][Math]::Ceiling($rowsArray.Count * $MinimumAffectedRate)
    $requiredRows = [Math]::Max($MinimumAffectedRows, $requiredByRate)
    return $affected.Count -ge $requiredRows -and $hosts.Count -ge $MinimumAffectedHosts
}

function Invoke-ValidationNetworkGate {
    param(
        [string]$EvidenceCsv,
        [int]$Snapshots = 3,
        [int]$MinimumHealthyHosts = 6,
        [int]$TimeoutSeconds = 10
    )

    $targets = @(
        'https://www.baidu.com/',
        'https://www.zhihu.com/',
        'https://weibo.com/',
        'https://www.douyin.com/',
        'https://www.toutiao.com/',
        'https://mp.weixin.qq.com/',
        'https://www.163.com/'
    )
    $records = New-Object Collections.Generic.List[object]
    Add-Type -AssemblyName System.Net.Http
    for ($snapshot = 1; $snapshot -le $Snapshots; $snapshot++) {
        foreach ($target in $targets) {
            $uri = [Uri]$target
            $dnsOk = $false
            $httpOk = $false
            $status = ''
            $errorText = ''
            try {
                $addresses = @([Net.Dns]::GetHostAddresses($uri.DnsSafeHost))
                $dnsOk = $addresses.Count -gt 0
            }
            catch { $errorText = 'DNS: ' + $_.Exception.Message }
            if ($dnsOk) {
                $handler = New-Object Net.Http.HttpClientHandler
                $handler.UseProxy = $true
                $handler.Proxy = [Net.WebRequest]::DefaultWebProxy
                $client = New-Object Net.Http.HttpClient($handler)
                $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
                try {
                    $request = New-Object Net.Http.HttpRequestMessage([Net.Http.HttpMethod]::Get, $uri)
                    $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                    $status = [int]$response.StatusCode
                    $httpOk = $status -ge 200 -and $status -lt 500 -and $status -notin @(408, 429)
                    $response.Dispose()
                    $request.Dispose()
                }
                catch { $errorText = 'HTTP: ' + $_.Exception.Message }
                finally { $client.Dispose(); $handler.Dispose() }
            }
            $records.Add([pscustomobject]@{
                Snapshot = $snapshot
                Host = $uri.DnsSafeHost
                DnsOk = $dnsOk
                HttpOk = $httpOk
                Status = $status
                Error = $errorText
                CheckedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            })
        }
        if ($snapshot -lt $Snapshots) { Start-Sleep -Milliseconds 500 }
    }
    if (-not [String]::IsNullOrWhiteSpace($EvidenceCsv)) {
        $records | Export-Csv -LiteralPath $EvidenceCsv -NoTypeInformation -Encoding UTF8
    }
    $passingSnapshots = 0
    for ($snapshot = 1; $snapshot -le $Snapshots; $snapshot++) {
        $healthy = @($records | Where-Object { $_.Snapshot -eq $snapshot -and $_.DnsOk -and $_.HttpOk }).Count
        if ($healthy -ge $MinimumHealthyHosts) { $passingSnapshots++ }
    }
    return [pscustomobject]@{
        Passed = $passingSnapshots -eq $Snapshots
        PassingSnapshots = $passingSnapshots
        RequiredSnapshots = $Snapshots
        EvidenceCsv = $EvidenceCsv
    }
}
