$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')

$projectRoot = Split-Path -Parent $PSScriptRoot
$portableFolderName = ([char[]](0x4FBF,0x643A,0x7248)) -join ''
$packageName = (([char[]](0x94FE,0x63A5,0x5931,0x6548,0x6838,0x9A8C,0x5DE5,0x5177,0x005F,0x4FBF,0x643A,0x7248)) -join '')
$publishRoot = Join-Path $projectRoot $portableFolderName
$package = Join-Path $publishRoot $packageName
$zipPath = Join-Path $publishRoot ($packageName + '.zip')
$stagingRoot = Join-Path $env:TEMP ('LinkCheckerRelease_' + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $stagingRoot $packageName
$temporaryZip = Join-Path $env:TEMP ('LinkCheckerPackage_' + [Guid]::NewGuid().ToString('N') + '.zip')
$sourceExeName = (([char[]](0x4FB5,0x6743,0x94FE,0x63A5,0x5904,0x7F6E,0x6838,0x9A8C,0x5DE5,0x5177)) -join '') + '.exe'
$readmeName = (([char[]](0x4F7F,0x7528,0x8BF4,0x660E)) -join '') + '.txt'
$logicName = (([char[]](0x6838,0x9A8C,0x903B,0x8F91,0x8BF4,0x660E)) -join '') + '.txt'
$startName = (([char[]](0x542F,0x52A8,0x5DE5,0x5177)) -join '') + '.cmd'
$diagnosticLauncherName = (([char[]](0x8FD0,0x884C,0x7F51,0x7EDC,0x8BCA,0x65AD)) -join '') + '.cmd'
$publishedDiagnosticLauncherName = (([char[]](0x8FD0,0x884C,0x73AF,0x5883,0x4E0E,0x7F51,0x7EDC,0x8BCA,0x65AD)) -join '') + '.cmd'
$diagnosticLinksName = (([char[]](0x8BCA,0x65AD,0x94FE,0x63A5)) -join '') + '.txt'
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$buildOutputDirectory = Join-Path $PSScriptRoot 'build-output'
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

New-Item -ItemType Directory -Path $staging -Force | Out-Null

foreach ($architecture in @('x64', 'x86')) { New-Item -ItemType Directory -Path (Join-Path $staging $architecture) -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $buildOutputDirectory 'LinkChecker.latest.exe') -Destination (Join-Path $staging 'x64\LinkChecker.exe') -Force
Copy-Item -LiteralPath (Join-Path $buildOutputDirectory 'LinkChecker.x86.latest.exe') -Destination (Join-Path $staging 'x86\LinkChecker.exe') -Force
foreach ($architecture in @('x64', 'x86')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Microsoft.Web.WebView2.Core.dll') -Destination (Join-Path $staging ($architecture + '\Microsoft.Web.WebView2.Core.dll')) -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Microsoft.Web.WebView2.WinForms.dll') -Destination (Join-Path $staging ($architecture + '\Microsoft.Web.WebView2.WinForms.dll')) -Force
}
Copy-Item -LiteralPath (Join-Path $dependencyRoot 'x64\WebView2Loader.dll') -Destination (Join-Path $staging 'x64\WebView2Loader.dll') -Force
Copy-Item -LiteralPath (Join-Path $dependencyRoot 'x86\WebView2Loader.dll') -Destination (Join-Path $staging 'x86\WebView2Loader.dll') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination (Join-Path $staging 'x64\platform-rules.json') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination (Join-Path $staging 'x86\platform-rules.json') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot $readmeName) -Destination (Join-Path $staging $readmeName) -Force
Copy-Item -LiteralPath (Join-Path $projectRoot $logicName) -Destination (Join-Path $staging $logicName) -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'StartTool.cmd') -Destination (Join-Path $staging $startName) -Force
Copy-Item -LiteralPath (Join-Path $buildOutputDirectory 'StartupCheck.exe') -Destination $staging -Force
Copy-Item -LiteralPath (Join-Path $buildOutputDirectory 'NetworkDiagnostics.exe') -Destination $staging -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot $diagnosticLauncherName) -Destination (Join-Path $staging $publishedDiagnosticLauncherName) -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot $diagnosticLinksName) -Destination $staging -Force

try {
    $compressed = $false
    for ($attempt = 1; $attempt -le 4 -and -not $compressed; $attempt++) {
        try {
            if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip -Force }
            Compress-Archive -LiteralPath $staging -DestinationPath $temporaryZip -CompressionLevel Optimal -ErrorAction Stop
            $compressed = $true
        }
        catch {
            if ($attempt -ge 4) { throw }
            Start-Sleep -Milliseconds (750 * $attempt)
        }
    }
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Recurse -Force }
Write-Host "Portable package: $zipPath"
