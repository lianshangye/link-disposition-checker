$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('LinkCheckerValidationTest_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
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
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
