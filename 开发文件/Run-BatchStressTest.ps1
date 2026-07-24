$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $PSScriptRoot 'LinkDispositionChecker.cs'
$aiSource = Join-Path $PSScriptRoot 'AiReview.cs'
$testSource = Join-Path $PSScriptRoot 'BatchStressTest.cs'
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
$webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runDirectory = Join-Path $env:TEMP ('LinkCheckerStress_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
try {
    Copy-Item -LiteralPath $webViewCore -Destination $runDirectory
    Copy-Item -LiteralPath $webViewForms -Destination $runDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runDirectory
    $executable = Join-Path $runDirectory 'Stress.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$executable /main:LinkDispositionChecker.BatchStressTest `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll `
        /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll /reference:$webViewCore /reference:$webViewForms $source $aiSource $testSource
    if ($LASTEXITCODE -ne 0) { throw 'Batch stress test compilation failed.' }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw "Batch stress test failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
