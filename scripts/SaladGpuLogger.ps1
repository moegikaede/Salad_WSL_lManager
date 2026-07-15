param(
    [string]$Distro = "salad-enterprise-linux",
    [int]$IntervalSeconds = 5,
    [string]$LogRoot = "$env:USERPROFILE\Documents\salad-gpu-logs"
)

$ErrorActionPreference = "Continue"

function Get-IsoTime {
    return (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss.fffK")
}

function Append-Line {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][AllowNull()][string]$Line
    )
    if ($null -eq $Line) {
        $Line = ""
    }
    $Line = $Line.Replace("`0", "")
    Add-Content -LiteralPath $Path -Encoding UTF8 -Value $Line
}

function Escape-CsvField {
    param([AllowNull()][object]$Value)
    $text = [string]$Value
    if ($text.Contains('"') -or $text.Contains(',') -or $text.Contains("`n") -or $text.Contains("`r")) {
        return '"' + $text.Replace('"', '""') + '"'
    }
    return $text
}

function Append-CsvRow {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowNull()][object[]]$Fields
    )
    if ($null -eq $Fields) {
        $Fields = @("")
    }
    $line = ($Fields | ForEach-Object { Escape-CsvField $_ }) -join ","
    Append-Line -Path $Path -Line $line
}

function Get-NvidiaSmiPath {
    $cmd = Get-Command "nvidia-smi.exe" -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $system32 = Join-Path $env:SystemRoot "System32\nvidia-smi.exe"
    if (Test-Path -LiteralPath $system32) {
        return $system32
    }

    return $null
}

function Run-CommandText {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    try {
        $output = & $FilePath @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            return (@("exit_code=$LASTEXITCODE") + $output) | ForEach-Object { ([string]$_).Replace("`0", "") }
        }
        return $output | ForEach-Object { ([string]$_).Replace("`0", "") }
    }
    catch {
        return @("exception=$($_.Exception.Message)")
    }
}

$runId = (Get-Date).ToString("yyyyMMdd_HHmmss")
$LogDir = Join-Path $LogRoot "run_$runId"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$GpuMetricsCsv = Join-Path $LogDir "gpu_metrics.csv"
$GpuComputeAppsCsv = Join-Path $LogDir "gpu_compute_apps.csv"
$GpuEngineCsv = Join-Path $LogDir "windows_gpu_engine.csv"
$WslStateLog = Join-Path $LogDir "wsl_salad_state.log"
$HostProcessCsv = Join-Path $LogDir "host_process_snapshot.csv"
$RunInfoLog = Join-Path $LogDir "run_info.log"

Append-CsvRow -Path $GpuMetricsCsv -Fields @(
    "host_time",
    "gpu_name",
    "gpu_util_pct",
    "mem_util_pct",
    "mem_used_mib",
    "mem_total_mib",
    "power_w",
    "temp_c"
)

Append-CsvRow -Path $GpuComputeAppsCsv -Fields @(
    "host_time",
    "pid",
    "process_name",
    "used_memory_mib_or_raw"
)

Append-CsvRow -Path $GpuEngineCsv -Fields @(
    "host_time",
    "counter_path",
    "utilization_pct"
)

Append-CsvRow -Path $HostProcessCsv -Fields @(
    "host_time",
    "process_id",
    "process_name",
    "cpu_seconds",
    "working_set_mb",
    "command_line"
)

$nvidiaSmi = Get-NvidiaSmiPath
$startTime = Get-IsoTime

Append-Line -Path $RunInfoLog -Line "start_time=$startTime"
Append-Line -Path $RunInfoLog -Line "distro=$Distro"
Append-Line -Path $RunInfoLog -Line "interval_seconds=$IntervalSeconds"
Append-Line -Path $RunInfoLog -Line "log_dir=$LogDir"
Append-Line -Path $RunInfoLog -Line "nvidia_smi=$nvidiaSmi"

Write-Host "Log dir: $LogDir"
Write-Host "Distro: $Distro"
Write-Host "Interval: $IntervalSeconds sec"
Write-Host "Stop: Ctrl+C"

if (-not $nvidiaSmi) {
    Write-Host "nvidia-smi.exe missing. GPU metrics will contain errors."
}

while ($true) {
    $now = Get-IsoTime

    if ($nvidiaSmi) {
        $gpuRows = Run-CommandText -FilePath $nvidiaSmi -Arguments @(
            "--query-gpu=name,utilization.gpu,utilization.memory,memory.used,memory.total,power.draw,temperature.gpu",
            "--format=csv,noheader,nounits"
        )

        foreach ($row in $gpuRows) {
            if ([string]::IsNullOrWhiteSpace($row)) {
                continue
            }
            Append-Line -Path $GpuMetricsCsv -Line "$now,$row"
        }

        $computeRows = Run-CommandText -FilePath $nvidiaSmi -Arguments @(
            "--query-compute-apps=pid,process_name,used_memory",
            "--format=csv,noheader,nounits"
        )

        foreach ($row in $computeRows) {
            if ([string]::IsNullOrWhiteSpace($row)) {
                continue
            }
            Append-Line -Path $GpuComputeAppsCsv -Line "$now,$row"
        }
    }
    else {
        Append-CsvRow -Path $GpuMetricsCsv -Fields @($now, "nvidia-smi.exe missing", "", "", "", "", "", "")
    }

    try {
        $counter = Get-Counter "\GPU Engine(*)\Utilization Percentage" -ErrorAction Stop
        foreach ($sample in $counter.CounterSamples) {
            if ($sample.CookedValue -ge 0.1) {
                Append-CsvRow -Path $GpuEngineCsv -Fields @(
                    $now,
                    $sample.Path,
                    [Math]::Round($sample.CookedValue, 3)
                )
            }
        }
    }
    catch {
        Append-CsvRow -Path $GpuEngineCsv -Fields @($now, "counter_error", $_.Exception.Message)
    }

    try {
        $commandLines = @{}
        try {
            Get-CimInstance Win32_Process -ErrorAction Stop |
                Where-Object {
                    $_.Name -match "wsl|vmmem|salad|docker|com\.docker|nvidia"
                } |
                ForEach-Object {
                    $commandLines[[int]$_.ProcessId] = $_.CommandLine
                }
        }
        catch {
            $commandLines = @{}
        }

        Get-Process |
            Where-Object {
                $_.ProcessName -match "wsl|vmmem|salad|docker|com\.docker|nvidia"
            } |
            ForEach-Object {
                $cmdLine = ""
                if ($commandLines.ContainsKey([int]$_.Id)) {
                    $cmdLine = $commandLines[[int]$_.Id]
                }

                Append-CsvRow -Path $HostProcessCsv -Fields @(
                    $now,
                    $_.Id,
                    $_.ProcessName,
                    $_.CPU,
                    [Math]::Round($_.WorkingSet64 / 1MB, 2),
                    $cmdLine
                )
            }
    }
    catch {
        Append-CsvRow -Path $HostProcessCsv -Fields @($now, "", "process_snapshot_error", "", "", $_.Exception.Message)
    }

    Append-Line -Path $WslStateLog -Line ""
    Append-Line -Path $WslStateLog -Line "===== $now WSL LIST ====="
    Run-CommandText -FilePath "wsl.exe" -Arguments @("-l", "-v") |
        ForEach-Object { Append-Line -Path $WslStateLog -Line ([string]$_) }

    Append-Line -Path $WslStateLog -Line "===== $now $Distro SNAPSHOT ====="
    $linuxProbe = @'
date -Is
echo '--- uname ---'
uname -a
echo '--- ps top cpu ---'
if command -v ps >/dev/null 2>&1; then
  ps -eo pid,ppid,pcpu,pmem,etime,cmd --sort=-pcpu | head -80
else
  echo 'ps_missing_using_proc_fallback'
  head -n 1 /proc/[0-9]*/comm 2>/dev/null | head -120
fi
echo '--- docker ps ---'
if command -v docker >/dev/null 2>&1; then docker ps --no-trunc; else echo docker_missing; fi
echo '--- linux nvidia-smi ---'
if [ -x /usr/lib/wsl/lib/nvidia-smi ]; then /usr/lib/wsl/lib/nvidia-smi; elif command -v nvidia-smi >/dev/null 2>&1; then nvidia-smi; else echo linux_nvidia_smi_missing; fi
'@

    Run-CommandText -FilePath "wsl.exe" -Arguments @("--distribution", $Distro, "--user", "root", "--exec", "/bin/sh", "-lc", $linuxProbe) |
        ForEach-Object { Append-Line -Path $WslStateLog -Line ([string]$_) }

    Start-Sleep -Seconds $IntervalSeconds
}
