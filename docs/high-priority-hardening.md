# High-Priority Hardening Design

Last updated: 2026-07-15

## Scope

This change applies three high-priority improvements without changing Salad's
service or WSL ownership policy:

1. Deterministic replay tests for log-driven lifecycle decisions.
2. An explicit pending-stop state machine.
3. Independent tray-action exclusion domains.

## Components

### PendingQuitLogAnalyzer

`PendingQuitLogAnalyzer` is a pure analyzer. Its inputs are Salad log lines,
workload ID, and instance ID. It has no filesystem, process, timer, or WinForms
dependency. Production pending-stop evaluation and replay tests call the same
method, preventing test/production parser drift.

The analyzer anchors all terminal evidence to the newest successful
`StopActiveWorkloads` request and returns `SafeToQuit` only when the latest
post-request state remains paused, Matrix desires zero workloads, the reserved
workload emitted a supported stopped signal, and no newer active state block
vetoes closure.

### PendingQuitSession

The session owns phase, workload ID, instance ID, reason, pause ownership,
request time, and failure detail under one lock. Valid phases are:

```text
Idle
PauseRequested
WaitingForWorkloadStop
QuietWindow
Closing
Canceling
Failed
```

The tray pending indicator derives from the session instead of independent
booleans. Persistence stores the phase and request time while retaining backward
compatibility with existing key/value reservation files. The status window
appends a concise stop phase to the existing State value while a session is
active; no extra layout row is introduced.

### TrayActionCoordinator

Action exclusion is divided into `Settings` and `Salad`. Commands in
the same domain are serialized; commands in different domains remain usable.
History navigation, status copying, Open log, and Exit are not action-domain
commands. A pending-stop completion uses the Salad domain.

### WorkloadNavigationPolicy

WL target calculation is pure and returns whether navigation enters live mode
and which rewarded row must align the graph date. This preserves the invariant
that Workload text, blue attribution, and graph day change together.

## Replay Tests

`tests/SaladWslManager.ReplayTests` compiles with the same .NET Framework csc
available to `build.ps1`. Sanitized fixtures preserve the ordering and message
forms observed in real Salad logs.

Required cases:

- Matrix zero before normalized Stopped is safe.
- Stopped before Matrix zero is safe.
- Missing Stopped remains waiting.
- A newer Running true remains waiting.
- A newer nonzero Matrix desired state remains waiting.
- A newer active state block remains waiting.
- Pending-stop phase transitions and clear behavior.
- Action domains block only their own domain.
- Returning to a rewarded live WL requests date alignment.

`scripts/test.ps1` builds and runs the replay executable. Tests write only below
ignored `artifacts/test`.
