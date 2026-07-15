# Public Release Design

Last updated: 2026-07-15

## Purpose

The public build is an observation-first tray utility. It may read Salad state,
GPU telemetry, workload history, wallet values, and estimated earnings. It must
not change GPU hardware settings or ship a privileged GPU-settings helper.

## Detailed Removal Boundary

- Remove the elevated GPU-settings helper project and build output.
- Remove Manager-side setting selection, persistence, IPC, heartbeat, startup
  application, workload reconciliation, shutdown restoration, and UI commands.
- Remove status fields that exist only to display the setting-control result.
- Remove tests and documentation for those removed paths.
- Keep read-only `nvidia-smi` queries used for utilization, power, memory, and
  compute-process logging.
- Keep the service-repair helper because it is a separate dormant SaladBowl
  diagnostic boundary and does not modify GPU hardware settings.

## History And Release

The public tree is initialized as a new repository after sanitization and is
committed as one root commit. No private `.git` data is copied. All advertised
GitHub branches and tags that reference the old implementation are replaced or
removed before publication.

## Verification

1. Search the complete tracked tree for privileged GPU-setting helpers,
   persistence/IPC identifiers, and setting command arguments.
2. Run replay tests after removing setting-specific tests.
3. Compile the complete Manager and service-repair helper.
4. Verify the tray and status window contain no GPU-setting controls or fields.
5. Scan tracked content for credentials, runtime logs, machine IDs, and local
   personal paths.
6. Run `scripts/verify-public-boundary.ps1`; `build.ps1` invokes the same guard
   before compilation so later private-only code cannot silently re-enter a
   public artifact.
7. Install a local pre-push hook that repeats the boundary scan and tests before
   every public push.
