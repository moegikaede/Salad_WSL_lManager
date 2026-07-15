# Salad WSL Manager

Salad WSL Manager is a small Windows tray utility for controlling Salad's WSL
workload environment. It watches the Salad desktop app, keeps the
`salad-enterprise-linux` WSL distro aligned with Salad's state, and starts GPU
logging only while Salad appears to be running a GPU workload.

## What It Does

- Runs as a tray application.
- Starts and restores the `SaladBowl` service when `Salad.exe` is running.
- Stops `SaladBowl`, disables its recovery actions, and terminates Salad's WSL
  distro when `Salad.exe` is not running.
- Exposes tray menu actions for `Chop now` and `Pause chopping`.
- Retries `Chop now` when SaladBowl accepts the request but the WSL distro does
  not reach `RUNNING`.
- Uses SaladBowl's local `\\.\pipe\salad-port` pipe to discover the current
  local gRPC port, then calls SaladBowl over local HTTP/2 gRPC.
- Shows a tray tooltip with Salad, SaladBowl, WSL, workload, logging, and
  estimated hourly earning status.
- Starts `scripts/SaladGpuAutoLogger.ps1` while a Salad GPU workload appears to
  be active, then stops logging after the workload goes quiet.

## Requirements

- Windows 10 or Windows 11.
- WSL2.
- Salad for Windows installed.
- NVIDIA GPU and `nvidia-smi.exe` for GPU metrics.
- Administrator rights.
- .NET Framework 4.x compiler for the included `build.ps1` path, or Visual
  Studio Build Tools / .NET SDK if you prefer your own build flow.

The app must run as Administrator because it changes Windows service
configuration, starts/stops `SaladBowl`, and terminates WSL distributions.

## Build

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The build output is written to:

```text
bin\SaladWslManager.exe
```

To run it, copy `scripts/SaladGpuAutoLogger.ps1` next to the executable or keep
the repository layout and run from `bin` after copying the script there.

## Install / Run

1. Build `bin\SaladWslManager.exe`.
2. Place `SaladGpuAutoLogger.ps1` in the same directory as the executable.
3. Start `SaladWslManager.exe` as Administrator.
4. Use the tray icon menu to request `Chop now`, `Pause chopping`, open the log,
   or exit the manager.

`Start-SaladWslManager.bat` is a simple helper for launching an executable named
`SaladWslManager.exe` from the same directory as the batch file.

## Logs

Manager log:

```text
C:\ProgramData\SaladWslManager\salad-wsl-manager.log
```

GPU log default location:

```text
%USERPROFILE%\Documents\salad-gpu-logs
```

The GPU logger can capture process names, process IDs, command lines, GPU
utilization, GPU memory use, power draw, temperatures, WSL process snapshots,
and recent log hints from inside the Salad WSL distro. Review logs before
sharing them publicly.

The auto logger starts detailed `auto_run_*` logs when it detects Salad GPU
activity from the WSL distro probe or when host GPU utilization is at least
50% while the Salad distro is running. If the Salad distro does not include
`ps`, the probe falls back to reading `/proc/*/comm` and `/proc/*/cmdline`.

## Privacy Notes

This repository intentionally excludes:

- Built executables.
- Local runtime logs.
- Salad app extraction output.
- User-specific test data.
- Salad bearer tokens, cookies, or machine IDs.

The manager reads Salad's local app logs at runtime to obtain the current
machine ID and bearer token for the optional `Est/hr` display. Those values are
not stored in this repository and should not be committed.

## Important Behavior

When Salad is absent, this app can run `wsl --shutdown`. That stops all running
WSL distributions, not only Salad's distro. This behavior is intentional for
clearing the WSL VM after Salad is stopped, but it may interrupt unrelated WSL
work.

`Chop now` is not treated as complete until `salad-enterprise-linux` reaches
`RUNNING`. If SaladBowl accepts the gRPC request but WSL is still stopped, the
manager re-applies the SaladBowl start configuration and retries the request.

## Repository Layout

```text
src/SaladWslManager/Program.cs       Tray manager source
src/SaladWslManager/app.manifest     Administrator elevation manifest
scripts/SaladGpuAutoLogger.ps1       Auto-start GPU workload logger
scripts/SaladGpuLogger.ps1           Manual GPU logger
build.ps1                            csc.exe build helper
Start-SaladWslManager.bat            Simple launch helper
```

