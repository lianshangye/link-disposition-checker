param(
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$buildScript = Get-ChildItem -LiteralPath $projectRoot -Filter 'build.ps1' -File -Recurse |
    Where-Object { $_.DirectoryName -ne $projectRoot } |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $buildScript) {
    throw 'Unable to locate the project build script.'
}

$developmentRoot = Split-Path -Parent $buildScript
$dependencyRoot = Join-Path $developmentRoot 'dependencies'
$sourcePath = Join-Path $developmentRoot 'LinkDispositionChecker.cs'
$webViewVersion = '1.0.4078.44'
$packageUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$webViewVersion/microsoft.web.webview2.$webViewVersion.nupkg"
$cacheRoot = Join-Path $env:LOCALAPPDATA "LinkDispositionChecker\packages\$webViewVersion"
$packageZip = Join-Path $cacheRoot 'Microsoft.Web.WebView2.zip'
$packageRoot = Join-Path $cacheRoot 'expanded'
$outputName = (([char[]](0x4FB5,0x6743,0x94FE,0x63A5,0x5904,0x7F6E,0x6838,0x9A8C,0x5DE5,0x5177)) -join '') + '.exe'
$outputPath = Join-Path $projectRoot $outputName
$runtimeFiles = @(
    $outputPath,
    (Join-Path $projectRoot 'Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $projectRoot 'Microsoft.Web.WebView2.WinForms.dll'),
    (Join-Path $projectRoot 'WebView2Loader.dll')
)

function Assert-File {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing file: $Path"
    }
}

$expectedVersion = ''
if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
    $versionMatch = [regex]::Match(
        [System.IO.File]::ReadAllText($sourcePath),
        'AssemblyFileVersion\("([0-9.]+)"\)'
    )
    if ($versionMatch.Success) {
        $expectedVersion = $versionMatch.Groups[1].Value
    }
}

$runtimeReady = @($runtimeFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -eq 0
if ($runtimeReady -and -not [String]::IsNullOrWhiteSpace($expectedVersion)) {
    $installedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($outputPath).FileVersion
    $runtimeReady = [String]::Equals($installedVersion, $expectedVersion, [StringComparison]::OrdinalIgnoreCase)
}
if (-not $runtimeReady) {
    $coreSource = Join-Path $packageRoot 'lib\net462\Microsoft.Web.WebView2.Core.dll'
    $formsSource = Join-Path $packageRoot 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'
    $loaderX64Source = Join-Path $packageRoot 'build\native\x64\WebView2Loader.dll'
    $loaderX86Source = Join-Path $packageRoot 'build\native\x86\WebView2Loader.dll'
    $requiredSources = @($coreSource, $formsSource, $loaderX64Source, $loaderX86Source)

    if (@($requiredSources | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -gt 0) {
        New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
        Write-Host "Downloading Microsoft WebView2 SDK $webViewVersion..."
        Invoke-WebRequest -Uri $packageUrl -OutFile $packageZip -UseBasicParsing

        if (Test-Path -LiteralPath $packageRoot) {
            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }
        Expand-Archive -LiteralPath $packageZip -DestinationPath $packageRoot -Force
    }

    foreach ($source in $requiredSources) {
        Assert-File -Path $source
    }

    New-Item -ItemType Directory -Path (Join-Path $dependencyRoot 'x64') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $dependencyRoot 'x86') -Force | Out-Null
    Copy-Item -LiteralPath $coreSource -Destination (Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll') -Force
    Copy-Item -LiteralPath $formsSource -Destination (Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll') -Force
    Copy-Item -LiteralPath $loaderX64Source -Destination (Join-Path $dependencyRoot 'x64\WebView2Loader.dll') -Force
    Copy-Item -LiteralPath $loaderX86Source -Destination (Join-Path $dependencyRoot 'x86\WebView2Loader.dll') -Force

    Write-Host 'Building Link Disposition Checker...'
    & $buildScript

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}

foreach ($file in $runtimeFiles) {
    Assert-File -Path $file
}

if (-not [String]::IsNullOrWhiteSpace($expectedVersion)) {
    $installedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($outputPath).FileVersion
    if (-not [String]::Equals($installedVersion, $expectedVersion, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The installed EXE is version $installedVersion but source requires $expectedVersion. Close the running tool and retry."
    }
}

Write-Host 'Runtime preparation completed.'

if ($Launch) {
    Start-Process -FilePath $outputPath -WorkingDirectory $projectRoot
}
