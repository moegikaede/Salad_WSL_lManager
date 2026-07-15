$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root "scripts\verify-public-boundary.ps1")
$managerSrcDir = Join-Path $root "src\SaladWslManager"
$managerSources = Get-ChildItem -LiteralPath $managerSrcDir -Filter "*.cs" |
    Sort-Object FullName |
    ForEach-Object { $_.FullName }
$serviceRepairHelperSrc = Join-Path $root "src\SaladWslManager.ServiceRepairHelper\Program.cs"
$manifest = Join-Path $root "src\SaladWslManager\app.manifest"
$icon = Join-Path $root "src\SaladWslManager\app.ico"
$outDir = Join-Path $root "bin"
$outExe = Join-Path $outDir "SaladWslManager.exe"
$serviceRepairHelperExe = Join-Path $outDir "SaladWslManager.ServiceRepairHelper.exe"
$autoLogger = Join-Path $root "scripts\SaladGpuAutoLogger.ps1"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    throw "csc.exe not found: $csc"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

& $csc `
    /nologo `
    /target:winexe `
    /optimize+ `
    /reference:System.Windows.Forms.dll `
    /reference:System.Windows.Forms.DataVisualization.dll `
    /reference:System.Drawing.dll `
    /win32manifest:$manifest `
    /win32icon:$icon `
    /out:$outExe `
    $managerSources

if ($LASTEXITCODE -ne 0) {
    throw "build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $serviceRepairHelperExe) -or
    (Get-Item -LiteralPath $serviceRepairHelperSrc).LastWriteTimeUtc -gt (Get-Item -LiteralPath $serviceRepairHelperExe).LastWriteTimeUtc) {
    & $csc `
        /nologo `
        /target:winexe `
        /optimize+ `
        /out:$serviceRepairHelperExe `
        $serviceRepairHelperSrc

    if ($LASTEXITCODE -ne 0) {
        throw "service repair helper build failed with exit code $LASTEXITCODE"
    }

    Write-Host "Built: $serviceRepairHelperExe"
} else {
    Write-Host "Skipped: $serviceRepairHelperExe"
}

Copy-Item -LiteralPath $autoLogger -Destination (Join-Path $outDir "SaladGpuAutoLogger.ps1") -Force
Write-Host "Built: $outExe"

