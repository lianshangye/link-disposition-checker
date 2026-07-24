param(
    [string]$CsvPath = 'C:\Users\MQ\Desktop\_长城汽车半年度业绩预告舆情20日10时.csv',
    [string]$ExcelPath = 'C:\Users\MQ\Desktop\魏总相关负面链接.xlsx',
    [switch]$RegressionOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
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
    '/reference:Microsoft.CSharp.dll',
    '/reference:System.Xml.Linq.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
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
    ) + $references + @($source, (Join-Path $PSScriptRoot $TestSource))
    & $Compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "$Name compilation failed." }
    foreach ($argument in $Arguments) {
        if ([String]::IsNullOrWhiteSpace($argument)) { continue }
        if (-not (Test-Path -LiteralPath $argument)) { throw "$Name input disappeared before launch: $argument" }
    }
    $argumentLine = ($Arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' '
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
    if (-not $RegressionOnly) {
        Invoke-Test -Name 'CsvImport-x64' -TestSource 'CsvImportTests.cs' -MainClass 'CsvImportTests' -Platform 'x64' -Compiler $compiler64 -Arguments @($CsvPath)
        Invoke-Test -Name 'ExcelImport-x64' -TestSource 'ExcelImportTests.cs' -MainClass 'ExcelImportTests' -Platform 'x64' -Compiler $compiler64 -Arguments @($ExcelPath)
    }
    Invoke-Test -Name 'NetworkFallback-x64' -TestSource 'NetworkFallbackTests.cs' -MainClass 'NetworkFallbackTests' -Platform 'x64' -Compiler $compiler64 -Arguments @()
    Write-Host 'All core tests passed.'
}
finally {
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
