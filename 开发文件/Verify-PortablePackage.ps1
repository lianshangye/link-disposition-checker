$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$zipPath = Get-ChildItem -LiteralPath $projectRoot -Recurse -Filter '*.zip' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if ([String]::IsNullOrWhiteSpace($zipPath)) { throw 'Portable ZIP was not found.' }
$startName = (([char[]](0x542F,0x52A8,0x5DE5,0x5177)) -join '') + '.cmd'
$readmeName = (([char[]](0x4F7F,0x7528,0x8BF4,0x660E)) -join '') + '.txt'
$logicName = (([char[]](0x6838,0x9A8C,0x903B,0x8F91,0x8BF4,0x660E)) -join '') + '.txt'
$diagnosticName = (([char[]](0x8FD0,0x884C,0x73AF,0x5883,0x4E0E,0x7F51,0x7EDC,0x8BCA,0x65AD)) -join '') + '.cmd'
$diagnosticLinksName = (([char[]](0x8BCA,0x65AD,0x94FE,0x63A5)) -join '') + '.txt'
$startupReportName = (([char[]](0x542F,0x52A8,0x68C0,0x67E5,0x62A5,0x544A)) -join '') + '.txt'
$verifyRoot = Join-Path $env:TEMP ('LinkCheckerPackageVerify_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verifyRoot | Out-Null
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $verifyRoot
    $package = Get-ChildItem -LiteralPath $verifyRoot -Directory | Select-Object -First 1
    if ($null -eq $package) { throw 'ZIP does not contain a package directory.' }
    $root = $package.FullName
    $required = @(
        $startName,
        'StartupCheck.exe',
        $readmeName,
        $logicName,
        $diagnosticName,
        'NetworkDiagnostics.exe',
        $diagnosticLinksName,
        'x64\LinkChecker.exe',
        'x64\Microsoft.Web.WebView2.Core.dll',
        'x64\Microsoft.Web.WebView2.WinForms.dll',
        'x64\WebView2Loader.dll',
        'x64\platform-rules.json',
        'x86\LinkChecker.exe',
        'x86\Microsoft.Web.WebView2.Core.dll',
        'x86\Microsoft.Web.WebView2.WinForms.dll',
        'x86\WebView2Loader.dll',
        'x86\platform-rules.json'
    )
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
    if ($missing.Count -gt 0) { throw ('Missing package files: ' + ($missing -join ', ')) }

    $unexpected = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
        $_.Extension -in '.pdb', '.cs', '.ps1', '.log' -or $_.Name -match 'test|candidate|representative'
    })
    if ($unexpected.Count -gt 0) { throw ('Unexpected development files: ' + (($unexpected.FullName) -join ', ')) }

    $x64 = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'x64\LinkChecker.exe'))
    $x86 = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'x86\LinkChecker.exe'))
    if ($x64.FileVersion -ne '4.4.0.0' -or $x86.FileVersion -ne '4.4.0.0') {
        throw "Unexpected executable versions: x64=$($x64.FileVersion), x86=$($x86.FileVersion)"
    }
    $sourceRules = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $projectRoot 'platform-rules.json')).Hash
    foreach ($architecture in @('x64', 'x86')) {
        $packagedRules = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $root ($architecture + '\platform-rules.json'))).Hash
        if ($sourceRules -ne $packagedRules) { throw "$architecture platform rules do not match the source rules." }
    }

    $launcher = Join-Path $root 'StartupCheck.exe'
    $process = Start-Process -FilePath $launcher -ArgumentList '--check-only' -WorkingDirectory $root -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Startup check failed with exit code $($process.ExitCode)." }
    $report = Join-Path $root $startupReportName
    if (-not (Test-Path -LiteralPath $report)) { throw 'Startup check did not create its report.' }

    $readme = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root $readmeName)
    $logic = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root $logicName)
    if (
        $readme -notmatch '4\.4\.0' -or
        $logic -notmatch '4\.4\.0' -or
        $readme -notmatch 'Globalping' -or
        $logic -notmatch 'HTTP 404' -or
        $readme.Length -lt 500 -or
        $logic.Length -lt 800
    ) {
        throw 'Packaged documentation is incomplete.'
    }

    $fileCount = @(Get-ChildItem -LiteralPath $root -Recurse -File).Count
    $zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash
    Write-Host "PACKAGE_FILES=$fileCount"
    Write-Host "X64_VERSION=$($x64.FileVersion)"
    Write-Host "X86_VERSION=$($x86.FileVersion)"
    Write-Host "ZIP_BYTES=$((Get-Item -LiteralPath $zipPath).Length)"
    Write-Host "ZIP_SHA256=$zipHash"
    Write-Host "STARTUP_CHECK_REPORT=$report"
}
finally {
    if (Test-Path -LiteralPath $verifyRoot) {
        Remove-Item -LiteralPath $verifyRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
