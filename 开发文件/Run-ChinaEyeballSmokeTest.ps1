param(
    [Parameter(Mandatory = $true)]
    [string[]]$Url
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dependencyRoot = Join-Path $PSScriptRoot 'dependencies'
$runDirectory = Join-Path $env:TEMP ('LinkCheckerChinaSmoke_' + [Guid]::NewGuid().ToString('N'))
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Path $runDirectory | Out-Null
try {
    $webViewCore = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.Core.dll'
    $webViewForms = Join-Path $dependencyRoot 'Microsoft.Web.WebView2.WinForms.dll'
    Copy-Item -LiteralPath $webViewCore -Destination $runDirectory
    Copy-Item -LiteralPath $webViewForms -Destination $runDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-rules.json') -Destination $runDirectory

    $executable = Join-Path $runDirectory 'ChinaEyeballSmoke.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /out:$executable /main:ChinaEyeballSmokeTests `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll `
        /reference:System.Security.dll /reference:Microsoft.CSharp.dll /reference:System.Xml.Linq.dll `
        /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
        /reference:$webViewCore /reference:$webViewForms `
        (Join-Path $PSScriptRoot 'LinkDispositionChecker.cs') `
        (Join-Path $PSScriptRoot 'AiReview.cs') `
        (Join-Path $PSScriptRoot 'RunLogging.cs') `
        (Join-Path $PSScriptRoot 'AcceptanceEvidence.cs') `
        (Join-Path $PSScriptRoot 'ChinaEyeballEvidence.cs') `
        (Join-Path $PSScriptRoot 'ChinaEyeballSmokeTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'China eyeball smoke test compilation failed.' }
    & $executable $Url
    exit $LASTEXITCODE
}
finally {
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
