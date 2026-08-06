param(
    [string]$CsvPath = '',
    [string]$ExcelPath = '',
    [switch]$RegressionOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$aiSource = Join-Path $PSScriptRoot 'AiReview.cs'
$logSource = Join-Path $PSScriptRoot 'RunLogging.cs'
$acceptanceSource = Join-Path $PSScriptRoot 'AcceptanceEvidence.cs'
$chinaEyeballSource = Join-Path $PSScriptRoot 'ChinaEyeballEvidence.cs'
$checkpointSource = Join-Path $PSScriptRoot 'AuditCheckpointStore.cs'
$fastAuditSource = Join-Path $PSScriptRoot 'FastAuditRunner.cs'
$shardedAuditSource = Join-Path $PSScriptRoot 'ShardedFastAudit.cs'
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
$compiler64 = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$compiler32 = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$runDirectory = Join-Path $env:TEMP ('LinkCheckerCoreTests_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
Copy-Item -LiteralPath $webViewCore -Destination $runDirectory
Copy-Item -LiteralPath $webViewForms -Destination $runDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runDirectory

$references = @(
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Net.Http.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Security.dll',
    '/reference:Microsoft.CSharp.dll',
    '/reference:System.Xml.Linq.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
    '/reference:Microsoft.VisualBasic.dll',
    ('/reference:' + $webViewCore),
    ('/reference:' + $webViewForms)
)

function Invoke-Test([string]$Name, [string]$TestSource, [string]$MainClass,
    [string]$Platform, [string]$Compiler, [string[]]$Arguments) {
    $executable = Join-Path $runDirectory ($Name + '.exe')
    $compilerArguments = @(
        '/nologo',
        '/target:exe',
        ('/platform:' + $Platform),
        '/optimize+',
        ('/out:' + $executable),
        ('/main:' + $MainClass)
    ) + $references + @($source, $aiSource, $logSource, $acceptanceSource, $chinaEyeballSource, $checkpointSource, $fastAuditSource, $shardedAuditSource, (Join-Path $PSScriptRoot $TestSource))
    & $Compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "$Name compilation failed." }
    $resolvedArguments = @()
    foreach ($argument in $Arguments) {
        if ([String]::IsNullOrWhiteSpace($argument)) { continue }
        if (-not (Test-Path -LiteralPath $argument)) { throw "$Name input disappeared before launch: $argument" }
        $resolvedArguments += [IO.Path]::GetFullPath($argument)
    }
    $argumentLine = ($resolvedArguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' '
    $startParameters = @{
        FilePath = $executable
        WorkingDirectory = $runDirectory
        Wait = $true
        PassThru = $true
        NoNewWindow = $true
    }
    if (-not [String]::IsNullOrWhiteSpace($argumentLine)) { $startParameters.ArgumentList = $argumentLine }
    $process = Start-Process @startParameters
    $global:LASTEXITCODE = $process.ExitCode
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

try {
    Invoke-Test -Name 'Regression-x64' -TestSource 'RegressionTests.cs' -MainClass 'RegressionTests' -Platform 'x64' -Compiler $compiler64 -Arguments @()
    Invoke-Test -Name 'Regression-x86' -TestSource 'RegressionTests.cs' -MainClass 'RegressionTests' -Platform 'x86' -Compiler $compiler32 -Arguments @()
    Invoke-Test -Name 'Reliability-x64' -TestSource 'ReliabilityTests.cs' -MainClass 'ReliabilityTests' -Platform 'x64' -Compiler $compiler64 -Arguments @()
    Invoke-Test -Name 'Reliability-x86' -TestSource 'ReliabilityTests.cs' -MainClass 'ReliabilityTests' -Platform 'x86' -Compiler $compiler32 -Arguments @()
    if (-not $RegressionOnly) {
        if (-not [String]::IsNullOrWhiteSpace($CsvPath) -and (Test-Path -LiteralPath $CsvPath)) {
            Invoke-Test -Name 'CsvImport-x64' -TestSource 'CsvImportTests.cs' -MainClass 'CsvImportTests' -Platform 'x64' -Compiler $compiler64 -Arguments @($CsvPath)
        }
        else { Write-Warning 'CSV import fixture not supplied; skipping optional CSV import test.' }
        if (-not [String]::IsNullOrWhiteSpace($ExcelPath) -and (Test-Path -LiteralPath $ExcelPath)) {
            Invoke-Test -Name 'ExcelImport-x64' -TestSource 'ExcelImportTests.cs' -MainClass 'ExcelImportTests' -Platform 'x64' -Compiler $compiler64 -Arguments @($ExcelPath)
            Invoke-Test -Name 'ExcelWriteback-x64' -TestSource 'ExcelWritebackTests.cs' -MainClass 'ExcelWritebackTests' -Platform 'x64' -Compiler $compiler64 -Arguments @($ExcelPath)
        }
        else { Write-Warning 'Excel import fixture not supplied; skipping optional Excel import test.' }
    }
    Invoke-Test -Name 'NetworkFallback-x64' -TestSource 'NetworkFallbackTests.cs' -MainClass 'NetworkFallbackTests' -Platform 'x64' -Compiler $compiler64 -Arguments @()
    Invoke-Test -Name 'ShardedFastAudit-x64' -TestSource 'ShardedFastAuditTests.cs' -MainClass 'LinkDispositionChecker.ShardedFastAuditTests' -Platform 'x64' -Compiler $compiler64 -Arguments @()
    Write-Host 'All core tests passed.'
}
finally {
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
