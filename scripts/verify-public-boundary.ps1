$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$scanRoots = @(
    (Join-Path $root "src"),
    (Join-Path $root "tests"),
    (Join-Path $root "docs"),
    (Join-Path $root "build.ps1")
)

# These signatures identify the removed privileged GPU-setting subsystem.
# Read-only NVIDIA queries used by telemetry are intentionally permitted.
$forbiddenPatterns = @(
    'SaladWslManager[.]ClockHelper',
    'GpuClockController',
    'GpuClockHelperController',
    'GpuClockSettings',
    'GpuClockWorkLatch',
    'gpu-clock-(lock|command|heartbeat|helper)',
    'gpu-mem-clock-lock',
    'TrayActionDomain[.]Clock',
    '(^|["'' ])-(lgc|lmc|rgc)([= "'']|$)'
)

$files = foreach ($item in $scanRoots) {
    if (Test-Path -LiteralPath $item -PathType Container) {
        Get-ChildItem -LiteralPath $item -Recurse -File |
            Where-Object { $_.Extension -in @('.cs', '.ps1', '.md', '.csproj') }
    } elseif (Test-Path -LiteralPath $item -PathType Leaf) {
        Get-Item -LiteralPath $item
    }
}

$violations = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($root.Length).TrimStart('\')
    if ($relative -eq 'scripts\verify-public-boundary.ps1' -or
        $relative -eq 'docs\public-release-design.md') {
        continue
    }

    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $lineNumber++
        foreach ($pattern in $forbiddenPatterns) {
            if ($line -match $pattern) {
                $violations += "$relative`:$lineNumber`: $line"
                break
            }
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    throw "Public boundary verification failed."
}

Write-Host "PASS: public build contains no privileged GPU-setting subsystem"
