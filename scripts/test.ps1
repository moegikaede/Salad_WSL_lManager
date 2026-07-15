$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$testDir = Join-Path $root "tests\SaladWslManager.ReplayTests"
$outDir = Join-Path $root "artifacts\test"
$outExe = Join-Path $outDir "SaladWslManager.ReplayTests.exe"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$sources = @(
    (Join-Path $root "src\SaladWslManager\PendingQuitLogAnalyzer.cs"),
    (Join-Path $root "src\SaladWslManager\PendingQuitSession.cs"),
    (Join-Path $root "src\SaladWslManager\TrayActionCoordinator.cs"),
    (Join-Path $root "src\SaladWslManager\WorkloadNavigationPolicy.cs"),
    (Join-Path $testDir "Program.cs")
)

& $csc /nologo /target:exe /optimize+ /out:$outExe $sources
if ($LASTEXITCODE -ne 0) {
    throw "test build failed with exit code $LASTEXITCODE"
}

& $outExe (Join-Path $testDir "fixtures")
if ($LASTEXITCODE -ne 0) {
    throw "replay tests failed with exit code $LASTEXITCODE"
}
