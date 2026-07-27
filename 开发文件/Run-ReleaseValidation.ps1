param(
    [Parameter(Mandatory = $true)][string]$GroundTruthCsv,
    [string]$OutputCsv = '',
    [int]$MinimumLabelledRows = 50,
    [int]$MinimumAliveRows = 15,
    [int]$MinimumRemovedRows = 15,
    [int]$MinimumPlatforms = 5,
    [int]$MinimumDomainFamilies = 10,
    [int]$MaximumLabelAgeDays = 7
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $GroundTruthCsv)) { throw 'Ground-truth CSV was not found.' }
$headers = @((Import-Csv -LiteralPath $GroundTruthCsv | Select-Object -First 1).PSObject.Properties.Name)
foreach ($required in @('confirmed_verdict','confirmed_at')) {
    if ($headers -notcontains $required) { throw "Ground-truth CSV is missing required column: $required" }
}
$truthRows = @(Import-Csv -LiteralPath $GroundTruthCsv)
$currentRows = @($truthRows | Where-Object {
    -not [String]::IsNullOrWhiteSpace([string]$_.confirmed_verdict) -and
    -not [String]::IsNullOrWhiteSpace([string]$_.confirmed_at)
})
if ($currentRows.Count -lt $MinimumLabelledRows) {
    throw "Current human sample is too small to start network validation: $($currentRows.Count) < $MinimumLabelledRows."
}

if ([String]::IsNullOrWhiteSpace($OutputCsv)) {
    $OutputCsv = Join-Path $env:TEMP ('LinkCheckerReleaseValidation_' + [Guid]::NewGuid().ToString('N') + '.csv')
    $deleteOutput = $true
}
else { $deleteOutput = $false }

$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$aiSource = Join-Path $PSScriptRoot 'AiReview.cs'
$logSource = Join-Path $PSScriptRoot 'RunLogging.cs'
$acceptanceSource = Join-Path $PSScriptRoot 'AcceptanceEvidence.cs'
$chinaEyeballSource = Join-Path $PSScriptRoot 'ChinaEyeballEvidence.cs'
$runnerSource = Join-Path $PSScriptRoot 'FastAuditRunner.cs'
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runDirectory = Join-Path $env:TEMP ('LinkCheckerReleaseGate_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null

try {
    Copy-Item -LiteralPath $webViewCore -Destination $runDirectory
    Copy-Item -LiteralPath $webViewForms -Destination $runDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runDirectory
    $runner = Join-Path $runDirectory 'ReleaseValidation.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$runner /main:FastAuditRunner `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll `
        /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms `
        $source $aiSource $logSource $acceptanceSource $chinaEyeballSource $runnerSource
    if ($LASTEXITCODE -ne 0) { throw 'Release validation runner compilation failed.' }

    $process = Start-Process -FilePath $runner -ArgumentList ('"' + $GroundTruthCsv + '" "' + $OutputCsv + '"') `
        -WorkingDirectory $runDirectory -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) { throw "Release validation run failed with exit code $($process.ExitCode)." }
    & (Join-Path $PSScriptRoot 'Compare-HumanValidation.ps1') -InputCsv $GroundTruthCsv -OutputCsv $OutputCsv `
        -ReleaseGate -MinimumLabelledRows $MinimumLabelledRows -MinimumAliveRows $MinimumAliveRows `
        -MinimumRemovedRows $MinimumRemovedRows -MinimumPlatforms $MinimumPlatforms `
        -MinimumDomainFamilies $MinimumDomainFamilies -MaximumLabelAgeDays $MaximumLabelAgeDays
}
finally {
    if ($deleteOutput -and (Test-Path -LiteralPath $OutputCsv)) { Remove-Item -LiteralPath $OutputCsv -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $runDirectory) { Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
