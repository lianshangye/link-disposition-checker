$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildOutputDirectory = Join-Path $PSScriptRoot 'build-output'
New-Item -ItemType Directory -Path $buildOutputDirectory -Force | Out-Null
$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$aiSource = Join-Path $PSScriptRoot 'AiReview.cs'
$logSource = Join-Path $PSScriptRoot 'RunLogging.cs'
$outputName = (([char[]](0x4FB5,0x6743,0x94FE,0x63A5,0x5904,0x7F6E,0x6838,0x9A8C,0x5DE5,0x5177)) -join '') + '.exe'
$output = Join-Path $projectRoot $outputName
$latestOutput = Join-Path $buildOutputDirectory 'LinkChecker.latest.exe'
$latestOutputX86 = Join-Path $buildOutputDirectory 'LinkChecker.x86.latest.exe'
$buildOutput = Join-Path $env:TEMP 'LinkChecker.build.exe'
$buildOutputX86 = Join-Path $env:TEMP 'LinkChecker.x86.build.exe'
$diagnosticSource = Join-Path $PSScriptRoot 'NetworkDiagnostics.cs'
$diagnosticOutput = Join-Path $buildOutputDirectory 'NetworkDiagnostics.exe'
$diagnosticBuildOutput = Join-Path $env:TEMP 'NetworkDiagnostics.build.exe'
$launcherSource = Join-Path $PSScriptRoot 'PortableLauncher.cs'
$launcherOutput = Join-Path $buildOutputDirectory 'StartupCheck.exe'
$launcherBuildOutput = Join-Path $env:TEMP 'StartupCheck.build.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}

$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
$webViewLoader = Join-Path $dependencyRoot 'x64\WebView2Loader.dll'
$webViewLoaderX86 = Join-Path $dependencyRoot 'x86\WebView2Loader.dll'
foreach ($dependency in @($webViewCore, $webViewForms, $webViewLoader, $webViewLoaderX86)) {
    if (-not (Test-Path -LiteralPath $dependency)) { throw "Missing WebView2 dependency: $dependency" }
}

& $compiler /nologo /target:winexe /platform:x64 /optimize+ /out:$buildOutput /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms $source $aiSource $logSource
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $compiler /nologo /target:winexe /platform:x86 /optimize+ /out:$buildOutputX86 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms $source $aiSource $logSource
if ($LASTEXITCODE -ne 0) { throw 'x86 build failed' }
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /out:$diagnosticBuildOutput /reference:System.dll /reference:System.Core.dll /reference:System.Net.Http.dll $diagnosticSource
if ($LASTEXITCODE -ne 0) { throw 'Network diagnostics build failed' }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /out:$launcherBuildOutput /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll $launcherSource
if ($LASTEXITCODE -ne 0) { throw 'Portable launcher build failed' }
Copy-Item -LiteralPath $buildOutput -Destination $latestOutput -Force
Copy-Item -LiteralPath $buildOutputX86 -Destination $latestOutputX86 -Force
Copy-Item -LiteralPath $diagnosticBuildOutput -Destination $diagnosticOutput -Force
Copy-Item -LiteralPath $launcherBuildOutput -Destination $launcherOutput -Force
try { Copy-Item -LiteralPath $buildOutput -Destination $output -Force }
catch { Write-Warning 'The main EXE is currently running. LinkChecker.latest.exe and the portable package contain the newest build.' }
Copy-Item -LiteralPath $launcherBuildOutput -Destination (Join-Path $projectRoot 'StartupCheck.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'StartTool.cmd') -Destination (Join-Path $projectRoot '启动工具.cmd') -Force
foreach ($pair in @(
    @($webViewCore, (Join-Path $projectRoot 'Microsoft.Web.WebView2.Core.dll')),
    @($webViewForms, (Join-Path $projectRoot 'Microsoft.Web.WebView2.WinForms.dll')),
    @($webViewLoader, (Join-Path $projectRoot 'WebView2Loader.dll'))
)) {
    $sourceDependency = $pair[0]
    $targetDependency = $pair[1]
    $needsCopy = -not (Test-Path -LiteralPath $targetDependency)
    if (-not $needsCopy) {
        $needsCopy = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceDependency).Hash -ne
            (Get-FileHash -Algorithm SHA256 -LiteralPath $targetDependency).Hash
    }
    if ($needsCopy) { Copy-Item -LiteralPath $sourceDependency -Destination $targetDependency -Force }
}
Write-Host "Built latest: $latestOutput"
