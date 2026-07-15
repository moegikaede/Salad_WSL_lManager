# Current Specification

## Purpose

Salad WSL Manager observes Salad, SaladBowl, and the Salad WSL distribution and
records enough local data to correlate workload activity, GPU utilization,
power, estimated earnings, and wallet movement. The public build is
observation-first and does not alter GPU hardware settings.

## Runtime Behavior

- Poll Salad, SaladBowl, WSL, and recent Salad logs without modifying WSL.
- Start read-only GPU and earnings logging while a GPU workload is active.
- Show current state and hourly earnings history in the tray status window.
- Start Salad on request and switch it to chopping.
- Reserve a safe Salad shutdown by requesting Pause until idle, then close
  Salad only after the tracked workload instance has stopped.
- Preserve a pending stop across Manager restart only when the workload
  identity still matches.
- Offer startup-folder registration without registry writes.
- Keep SaladBowl repair isolated behind the service-repair executable.

## Tray Menu

- Startup registration
- Start Salad app
- Stop Salad app
- Chop now
- Open log
- Exit

The context menu is command-only. Runtime status is refreshed when the status
window is shown.

## Status Window

The compact status grid is arranged as:

```text
Salad / Bowl / WSL
State / Workload / Pull
Runtime / Past avg
Est/hr / Last24h / Balance
```

The window also provides day and workload navigation for the hourly earnings
chart. Selecting a workload highlights its attributed reward in blue.

## Logging

Runtime files are stored under `%ProgramData%\SaladWslManager` when writable,
with user-local fallbacks where implemented. Principal files include:

- `salad-wsl-manager.log`
- `estimated-earnings.csv`
- `workload-observations.csv`
- `workload-history.csv`
- `pending-quit-salad-app.txt`

GPU telemetry is collected with read-only `nvidia-smi` queries. The Manager
does not write to Salad WSL or issue GPU-setting commands.

Workload history uses the base workload ID and instance ID together. Reward
attribution prefers positive estimate samples and supplements them with wallet
deltas where estimates are unavailable, avoiding mixed-source double counting.

## Safety Boundaries

- Do not commit runtime logs, credentials, bearer tokens, cookies, machine IDs,
  or personal analysis artifacts.
- Do not stop an active Salad workload without the pending-stop lifecycle
  confirmation.
- Do not add GPU-setting code, privileged GPU tooling, or related UI controls to
  the public repository.
- Keep external process calls bounded by timeouts and off the UI thread where
  practical.

