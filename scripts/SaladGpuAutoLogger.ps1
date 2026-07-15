param(
    [string]$Distro = "salad-enterprise-linux",
    [int]$MonitorIntervalSeconds = 10,
    [int]$LogIntervalSeconds = 5,
    [int]$QuietSeconds = 120,
    [int]$HostGpuActivityThreshold = 50,
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

function Get-DistroState {
    $lines = Run-CommandText -FilePath "wsl.exe" -Arguments @("-l", "-v")
    foreach ($rawLine in $lines) {
        $line = ([string]$rawLine).Replace("`0", "").Trim()
        if ($line -like "*$Distro*") {
            if ($line -match "Running") {
                return "RUNNING"
            }
            if ($line -match "Stopped") {
                return "STOPPED"
            }
            return $line
        }
    }
    return "UNKNOWN"
}

function Invoke-DistroProbe {
    $probe = @'
set +e
echo "__PROBE_TIME__=$(date -Is 2>/dev/null)"
echo "__PS_MATCHES_BEGIN__"
if command -v ps >/dev/null 2>&1; then
  ps -eo pid,ppid,pcpu,pmem,etime,cmd --sort=-pcpu |
    awk 'NR == 1 || tolower($0) ~ /(salad|spinner|containerd|runc|cuda|nvidia|miner|bzminer|lolminer|xmrig|hashrate)/ { print }' |
    head -80
else
  echo "ps_missing_using_proc_fallback"
  echo "pid comm cmdline"
  for p in /proc/[0-9]*; do
    [ -r "$p/comm" ] || continue
    pid="${p##*/}"
    comm="$(cat "$p/comm" 2>/dev/null)"
    cmdline=""
    if [ -r "$p/cmdline" ]; then
      cmdline="$(tr '\000' ' ' < "$p/cmdline" 2>/dev/null)"
    fi
    combined="$(printf '%s %s' "$comm" "$cmdline" | tr '[:upper:]' '[:lower:]')"
    case "$combined" in
      *salad*|*spinner*|*containerd*|*runc*|*cuda*|*nvidia*|*miner*|*bzminer*|*lolminer*|*xmrig*|*hashrate*)
        printf '%s %s %s\n' "$pid" "$comm" "$cmdline"
        ;;
    esac
  done | head -120
fi
echo "__PS_MATCHES_END__"
echo "__NVIDIA_COMPUTE_BEGIN__"
if [ -x /usr/lib/wsl/lib/nvidia-smi ]; then
  /usr/lib/wsl/lib/nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits 2>&1
elif command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits 2>&1
else
  echo "linux_nvidia_smi_missing"
fi
echo "__NVIDIA_COMPUTE_END__"
echo "__RECENT_LOG_HINTS_BEGIN__"
for d in /var/log /tmp /opt /root /home; do
  [ -d "$d" ] || continue
  find "$d" -maxdepth 5 -type f -mmin -15 \
    \( -iname '*salad*' -o -iname '*spinner*' -o -iname '*workload*' -o -iname '*container*' -o -iname '*.log' \) \
    -size -50M 2>/dev/null |
    head -80 |
    while IFS= read -r f; do
      echo "--- $f ---"
      tail -80 "$f" 2>/dev/null |
        grep -Eai 'gpu|cuda|nvidia|miner|hashrate|workload|container|spinner|started|running|assigned|job|task' |
        tail -20
    done
done
echo "__RECENT_LOG_HINTS_END__"
'@

    return Run-CommandText -FilePath "wsl.exe" -Arguments @("--distribution", $Distro, "--user", "root", "--exec", "/bin/sh", "-lc", $probe)
}

function Get-HostGpuUtilPercent {
    if (-not $script:NvidiaSmi) {
        return $null
    }

    $rows = Run-CommandText -FilePath $script:NvidiaSmi -Arguments @(
        "--query-gpu=utilization.gpu",
        "--format=csv,noheader,nounits"
    )

    $max = $null
    foreach ($row in $rows) {
        $text = ([string]$row).Trim()
        $value = 0
        if ([int]::TryParse($text, [ref]$value)) {
            if ($null -eq $max -or $value -gt $max) {
                $max = $value
            }
        }
    }

    return $max
}

function Test-SaladGpuActivity {
    param([string[]]$ProbeLines)

    $text = ($ProbeLines -join "`n")
    $computeBlock = [regex]::Match($text, "__NVIDIA_COMPUTE_BEGIN__(?<body>.*?)__NVIDIA_COMPUTE_END__", "Singleline").Groups["body"].Value
    $hasComputeApp = $false
    foreach ($line in ($computeBlock -split "`n")) {
        $clean = $line.Trim()
        if ($clean -and
            $clean -notmatch "No running processes found" -and
            $clean -notmatch "linux_nvidia_smi_missing" -and
            $clean -notmatch "NVIDIA-SMI has failed" -and
            $clean -notmatch "^exit_code=") {
            $hasComputeApp = $true
        }
    }

    $hasGpuProcess = $text -match "(?i)\b(bzminer|lolminer|xmrig|cuda|hashrate|miner|spinner|containerd|runc)\b"
    $hasWorkloadStartLog = $text -match "(?i)(workload|container|spinner).*(running|started|assigned)|((running|started|assigned).*(workload|container|spinner))"

    return ($hasComputeApp -or $hasGpuProcess -or $hasWorkloadStartLog)
}

function New-LoggingSession {
    $runId = (Get-Date).ToString("yyyyMMdd_HHmmss")
    $logDir = Join-Path $LogRoot "auto_run_$runId"
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null

    $session = [ordered]@{
        LogDir = $logDir
        GpuMetricsCsv = Join-Path $logDir "gpu_metrics.csv"
        GpuComputeAppsCsv = Join-Path $logDir "gpu_compute_apps.csv"
        GpuEngineCsv = Join-Path $logDir "windows_gpu_engine.csv"
        WslActivityLog = Join-Path $logDir "wsl_salad_activity.log"
        HostProcessCsv = Join-Path $logDir "host_process_snapshot.csv"
        RunInfoLog = Join-Path $logDir "run_info.log"
        EventsLog = Join-Path $logDir "monitor_events.log"
    }

    Append-CsvRow -Path $session.GpuMetricsCsv -Fields @("host_time","gpu_name","gpu_util_pct","mem_util_pct","mem_used_mib","mem_total_mib","power_w","temp_c")
    Append-CsvRow -Path $session.GpuComputeAppsCsv -Fields @("host_time","pid","process_name","used_memory_mib_or_raw")
    Append-CsvRow -Path $session.GpuEngineCsv -Fields @("host_time","counter_path","utilization_pct")
    Append-CsvRow -Path $session.HostProcessCsv -Fields @("host_time","process_id","process_name","cpu_seconds","working_set_mb","command_line")

    Append-Line -Path $session.RunInfoLog -Line "start_time=$(Get-IsoTime)"
    Append-Line -Path $session.RunInfoLog -Line "distro=$Distro"
    Append-Line -Path $session.RunInfoLog -Line "monitor_interval_seconds=$MonitorIntervalSeconds"
    Append-Line -Path $session.RunInfoLog -Line "log_interval_seconds=$LogIntervalSeconds"
    Append-Line -Path $session.RunInfoLog -Line "quiet_seconds=$QuietSeconds"
    Append-Line -Path $session.RunInfoLog -Line "host_gpu_activity_threshold=$HostGpuActivityThreshold"
    Append-Line -Path $session.RunInfoLog -Line "log_dir=$logDir"
    Append-Line -Path $session.RunInfoLog -Line "nvidia_smi=$script:NvidiaSmi"

    return $session
}

function Write-ActiveSample {
    param(
        [Parameter(Mandatory = $true)]$Session,
        [Parameter(Mandatory = $true)][string[]]$ProbeLines
    )

    $now = Get-IsoTime

    if ($script:NvidiaSmi) {
        $gpuRows = Run-CommandText -FilePath $script:NvidiaSmi -Arguments @(
            "--query-gpu=name,utilization.gpu,utilization.memory,memory.used,memory.total,power.draw,temperature.gpu",
            "--format=csv,noheader,nounits"
        )

        foreach ($row in $gpuRows) {
            if (-not [string]::IsNullOrWhiteSpace($row)) {
                Append-Line -Path $Session.GpuMetricsCsv -Line "$now,$row"
            }
        }

        $computeRows = Run-CommandText -FilePath $script:NvidiaSmi -Arguments @(
            "--query-compute-apps=pid,process_name,used_memory",
            "--format=csv,noheader,nounits"
        )

        foreach ($row in $computeRows) {
            if (-not [string]::IsNullOrWhiteSpace($row)) {
                Append-Line -Path $Session.GpuComputeAppsCsv -Line "$now,$row"
            }
        }
    }
    else {
        Append-CsvRow -Path $Session.GpuMetricsCsv -Fields @($now, "nvidia-smi.exe missing", "", "", "", "", "", "")
    }

    try {
        $counter = Get-Counter "\GPU Engine(*)\Utilization Percentage" -ErrorAction Stop
        foreach ($sample in $counter.CounterSamples) {
            if ($sample.CookedValue -ge 0.1) {
                Append-CsvRow -Path $Session.GpuEngineCsv -Fields @($now, $sample.Path, [Math]::Round($sample.CookedValue, 3))
            }
        }
    }
    catch {
        Append-CsvRow -Path $Session.GpuEngineCsv -Fields @($now, "counter_error", $_.Exception.Message)
    }

    try {
        $commandLines = @{}
        try {
            Get-CimInstance Win32_Process -ErrorAction Stop |
                Where-Object { $_.Name -match "wsl|vmmem|salad|docker|com\.docker|nvidia" } |
                ForEach-Object { $commandLines[[int]$_.ProcessId] = $_.CommandLine }
        }
        catch {
            $commandLines = @{}
        }

        Get-Process |
            Where-Object { $_.ProcessName -match "wsl|vmmem|salad|docker|com\.docker|nvidia" } |
            ForEach-Object {
                $cmdLine = ""
                if ($commandLines.ContainsKey([int]$_.Id)) {
                    $cmdLine = $commandLines[[int]$_.Id]
                }

                Append-CsvRow -Path $Session.HostProcessCsv -Fields @($now, $_.Id, $_.ProcessName, $_.CPU, [Math]::Round($_.WorkingSet64 / 1MB, 2), $cmdLine)
            }
    }
    catch {
        Append-CsvRow -Path $Session.HostProcessCsv -Fields @($now, "", "process_snapshot_error", "", "", $_.Exception.Message)
    }

    Append-Line -Path $Session.WslActivityLog -Line ""
    Append-Line -Path $Session.WslActivityLog -Line "===== $now $Distro ACTIVITY PROBE ====="
    foreach ($line in $ProbeLines) {
        Append-Line -Path $Session.WslActivityLog -Line ([string]$line)
    }
}

$script:NvidiaSmi = Get-NvidiaSmiPath
$monitorRunId = (Get-Date).ToString("yyyyMMdd_HHmmss")
$MonitorDir = Join-Path $LogRoot "monitor_$monitorRunId"
New-Item -ItemType Directory -Force -Path $MonitorDir | Out-Null
$MonitorLog = Join-Path $MonitorDir "monitor.log"

Append-Line -Path $MonitorLog -Line "start_time=$(Get-IsoTime)"
Append-Line -Path $MonitorLog -Line "distro=$Distro"
Append-Line -Path $MonitorLog -Line "monitor_interval_seconds=$MonitorIntervalSeconds"
Append-Line -Path $MonitorLog -Line "log_interval_seconds=$LogIntervalSeconds"
Append-Line -Path $MonitorLog -Line "quiet_seconds=$QuietSeconds"
Append-Line -Path $MonitorLog -Line "host_gpu_activity_threshold=$HostGpuActivityThreshold"
Append-Line -Path $MonitorLog -Line "nvidia_smi=$script:NvidiaSmi"

Write-Host "Monitor dir: $MonitorDir"
Write-Host "Distro: $Distro"
Write-Host "Monitor interval: $MonitorIntervalSeconds sec"
Write-Host "Log interval: $LogIntervalSeconds sec"
Write-Host "Quiet stop: $QuietSeconds sec"
Write-Host "Host GPU activity threshold: $HostGpuActivityThreshold%"
Write-Host "Stop: Ctrl+C"

if (-not $script:NvidiaSmi) {
    Write-Host "nvidia-smi.exe missing. GPU metrics will contain errors during active sessions."
}

$activeSession = $null
$lastActiveAt = $null
$lastSampleAt = [DateTimeOffset]::MinValue

while ($true) {
    $now = [DateTimeOffset]::Now
    $isoNow = Get-IsoTime
    $distroState = Get-DistroState

    if ($distroState -ne "RUNNING") {
        if ($activeSession) {
            Append-Line -Path $activeSession.EventsLog -Line "$isoNow stop reason=distro_$distroState"
            Append-Line -Path $MonitorLog -Line "$isoNow active_stop reason=distro_$distroState log_dir=$($activeSession.LogDir)"
            Write-Host "$isoNow active logging stopped: distro $distroState"
            $activeSession = $null
        }

        Append-Line -Path $MonitorLog -Line "$isoNow idle distro=$distroState"
        Start-Sleep -Seconds $MonitorIntervalSeconds
        continue
    }

    $probeLines = Invoke-DistroProbe
    $probeActivity = Test-SaladGpuActivity -ProbeLines $probeLines
    $hostGpuUtil = Get-HostGpuUtilPercent
    $hostGpuActivity = $false
    if ($null -ne $hostGpuUtil -and $hostGpuUtil -ge $HostGpuActivityThreshold) {
        $hostGpuActivity = $true
    }

    $activity = $probeActivity -or $hostGpuActivity

    if ($activity) {
        $lastActiveAt = $now
        if (-not $activeSession) {
            $activeSession = New-LoggingSession
            $reason = if ($probeActivity) { "salad_gpu_activity_detected" } else { "host_gpu_util_${hostGpuUtil}_pct" }
            Append-Line -Path $activeSession.EventsLog -Line "$isoNow start reason=$reason"
            Append-Line -Path $MonitorLog -Line "$isoNow active_start reason=$reason host_gpu_util=$hostGpuUtil log_dir=$($activeSession.LogDir)"
            Write-Host "$isoNow active logging started: $($activeSession.LogDir)"
        }
    }

    if ($activeSession) {
        if ($activity -or ($now - $lastSampleAt).TotalSeconds -ge $LogIntervalSeconds) {
            Write-ActiveSample -Session $activeSession -ProbeLines $probeLines
            $lastSampleAt = $now
        }

        if ($lastActiveAt -and ($now - $lastActiveAt).TotalSeconds -ge $QuietSeconds) {
            Append-Line -Path $activeSession.EventsLog -Line "$isoNow stop reason=quiet_${QuietSeconds}s"
            Append-Line -Path $MonitorLog -Line "$isoNow active_stop reason=quiet_${QuietSeconds}s log_dir=$($activeSession.LogDir)"
            Write-Host "$isoNow active logging stopped: quiet for $QuietSeconds sec"
            $activeSession = $null
        }
    }
    else {
        Append-Line -Path $MonitorLog -Line "$isoNow idle distro=$distroState activity=$activity probe_activity=$probeActivity host_gpu_util=$hostGpuUtil"
    }

    Start-Sleep -Seconds $MonitorIntervalSeconds
}
