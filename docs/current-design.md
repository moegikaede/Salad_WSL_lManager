# Current Design

## Ownership

`Program.cs` owns process-wide state and the polling orchestration. Partial
class files separate the major responsibilities:

- `SaladStateProbe.cs`: Salad, service, and WSL observation with short caches.
- `SaladLogReader.cs` and `SaladLogStateAnalyzer.cs`: one recent-log snapshot and
  deterministic state projection.
- `IncrementalWorkloadTracker.cs`: append-only log tailing and transition events.
- `PendingQuitController.cs`: persisted pending-stop phase machine and final
  lifecycle checks.
- `EarningsLogger.cs`: read-only GPU telemetry, earnings, and wallet samples.
- `WorkloadHistoryController.cs` and `WorkloadRewardAttribution.cs`: workload
  lifecycle persistence and reward attribution.
- `TrayUiController.cs`: command menu, tray state, and action serialization.
- `StatusWindowController.cs` and `StatusWindowHistoryController.cs`: window
  lifecycle, status rendering, history navigation, and chart rendering.
- `StalePullRepairController.cs`: bounded SaladBowl repair through the separate
  service-repair executable.
- `GpuTelemetryProbe.cs`: read-only NVIDIA executable discovery and parsing.

The single partial class preserves existing behavior while keeping policy
ownership searchable and testable.

## Polling Pipeline

Each normal tick performs the lightweight lifecycle work first:

1. Consume new Salad log lines from the previous offset.
2. Observe Salad, SaladBowl, and WSL state.
3. Project workload and pull state from one log snapshot.
4. Evaluate pending-stop transitions and tray state.
5. Publish an immutable status snapshot.
6. Queue wallet, estimate, history, and other slower reads asynchronously.

This ordering makes workload transitions visible before slow external reads.
The UI continues showing the previous successful value while a slow refresh is
in flight.

## Pending Stop

Pending stop is an explicit phase machine. It records workload and instance
identity, watches appended log lines, and requires a stopped lifecycle record
for the tracked instance before Salad is closed. A final revalidation runs
immediately before process termination. File-system notifications are hints;
the normal tick also requests evaluation because notifications can coalesce.

## Read-Only Telemetry

GPU utilization, power, and memory utilization are queried for earnings
correlation. NVIDIA access in the Manager is read-only. No public component
changes GPU hardware state, persists GPU settings, or launches a privileged GPU
process.

## UI Threading

Windows Forms controls are owned by the UI thread. External process and network
reads run asynchronously and post completed values back through the captured
UI synchronization context. Status rendering consumes `AppStateSnapshot`
instead of issuing probes itself.

## Verification

Replay tests cover pending-stop log decisions, pending-stop state transitions,
action-domain exclusion, and workload navigation. A public release must also
pass the public-boundary scan and compile the Manager plus service-repair
executable.

