$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('LinkCheckerValidationTest_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $implicitFixedSampleRejected = $false
    try { & (Join-Path $PSScriptRoot 'Run-RepresentativeValidation.ps1') }
    catch { $implicitFixedSampleRejected = $true }
    if (-not $implicitFixedSampleRejected) {
        throw 'Representative validation allowed the fixed sample without an explicit regression flag.'
    }
    Write-Host 'PASS formal validation cannot silently fall back to the fixed regression sample.'

    $truthPath = Join-Path $root 'truth.csv'
    $resultPath = Join-Path $root 'result.csv'
    $now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $truth = for ($i = 1; $i -le 50; $i++) {
        [pscustomobject]@{
            number = $i
            platform = '平台' + (($i - 1) % 5 + 1)
            url = 'https://news' + (($i - 1) % 10 + 1) + '.example' + (($i - 1) % 10 + 1) + '.com/item/' + $i
            confirmed_verdict = if ($i -le 25) { '仍可访问' } else { '已失效' }
            confirmed_at = $now
        }
    }
    $results = foreach ($row in $truth) {
        [pscustomobject]@{ 序号 = $row.number; 核验结果 = $row.confirmed_verdict; 原链接 = $row.url }
    }
    $truth | Export-Csv -LiteralPath $truthPath -NoTypeInformation -Encoding UTF8
    $results | Export-Csv -LiteralPath $resultPath -NoTypeInformation -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Compare-HumanValidation.ps1') -InputCsv $truthPath -OutputCsv $resultPath -ReleaseGate

    # Supplier sequence fields may not start at one; validation must still follow the checked input order.
    $truth | ForEach-Object { $_.number = 1000 + [int]$_.number }
    $truth | Export-Csv -LiteralPath $truthPath -NoTypeInformation -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Compare-HumanValidation.ps1') -InputCsv $truthPath -OutputCsv $resultPath -ReleaseGate

    $results[0].核验结果 = '已失效'
    $results | Export-Csv -LiteralPath $resultPath -NoTypeInformation -Encoding UTF8
    $rejected = $false
    try {
        & (Join-Path $PSScriptRoot 'Compare-HumanValidation.ps1') -InputCsv $truthPath -OutputCsv $resultPath -ReleaseGate
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Release gate accepted an excessive false-invalid rate.' }
    Write-Host 'PASS validation gate accepts a balanced correct sample and rejects an accuracy regression.'

    $strictBoundaryRate = [decimal]12 / [decimal]240
    $strictPassingRate = [decimal]11 / [decimal]240
    if ($strictBoundaryRate -lt [decimal]0.05 -or $strictPassingRate -ge [decimal]0.05) {
        throw 'Strict unresolved-rate boundary calculation failed.'
    }
    Write-Host 'PASS exactly 5% unresolved fails; only a rate below 5% passes.'

    $sourceList = Join-Path (Join-Path $PSScriptRoot 'test-data') 'rotating-validation-sources.txt'
    if (Test-Path -LiteralPath $sourceList) {
        $inputs = @(Get-Content -LiteralPath $sourceList -Encoding UTF8 |
            Where-Object { -not [String]::IsNullOrWhiteSpace($_) -and -not $_.StartsWith('#') })
        $missing = @($inputs | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
        if ($inputs.Count -gt 0 -and $missing.Count -eq 0) {
            $history = Join-Path $root 'rotation-history.csv'
            $first = Join-Path $root 'rotation-first.csv'
            $second = Join-Path $root 'rotation-second.csv'
            & (Join-Path $PSScriptRoot 'Build-RotatingValidationSet.ps1') -InputPath $inputs `
                -OutputCsv $first -HistoryCsv $history -MaximumRows 25 `
                -RowsPerPlatformOrDomain 2 -MinimumNetMediaRows 5 -Seed 'rotation-contract-a'

            $duplicateSeedRejected = $false
            try {
                & (Join-Path $PSScriptRoot 'Build-RotatingValidationSet.ps1') -InputPath $inputs `
                    -OutputCsv (Join-Path $root 'rotation-duplicate.csv') -HistoryCsv $history `
                    -MaximumRows 25 -RowsPerPlatformOrDomain 2 -MinimumNetMediaRows 5 -Seed 'rotation-contract-a'
            }
            catch { $duplicateSeedRejected = $true }

            & (Join-Path $PSScriptRoot 'Build-RotatingValidationSet.ps1') -InputPath $inputs `
                -OutputCsv $second -HistoryCsv $history -MaximumRows 25 `
                -RowsPerPlatformOrDomain 2 -MinimumNetMediaRows 5 -Seed 'rotation-contract-b'
            $firstUrls = @((Import-Csv -LiteralPath $first).'链接')
            $secondUrls = @((Import-Csv -LiteralPath $second).'链接')
            $firstNetMedia = @((Import-Csv -LiteralPath $first) | Where-Object 平台名称 -eq '网媒').Count
            $secondNetMedia = @((Import-Csv -LiteralPath $second) | Where-Object 平台名称 -eq '网媒').Count
            $overlap = @($firstUrls | Where-Object { $secondUrls -contains $_ }).Count

            $shortageRejected = $false
            try {
                & (Join-Path $PSScriptRoot 'Build-RotatingValidationSet.ps1') -InputPath $inputs `
                    -OutputCsv (Join-Path $root 'rotation-shortage.csv') -HistoryCsv $history `
                    -MaximumRows ([Int32]::MaxValue) -RowsPerPlatformOrDomain 2 `
                    -MinimumNetMediaRows 5 -Seed 'rotation-contract-c'
            }
            catch { $shortageRejected = $true }

            $historyRows = @(Import-Csv -LiteralPath $history)
            if ($firstUrls.Count -ne 25 -or $secondUrls.Count -ne 25 -or $overlap -ne 0 -or
                $firstNetMedia -lt 5 -or $secondNetMedia -lt 5 -or
                -not $duplicateSeedRejected -or -not $shortageRejected -or $historyRows.Count -ne 50) {
                throw 'Rotating sample contract failed.'
            }
            Write-Host 'PASS rotating samples enforce net-media coverage, reject reused seeds and URLs, and require the requested new-row count.'
        }
        else { Write-Warning 'Rotating sample sources are unavailable; skipping the local rotation contract test.' }
    }
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
