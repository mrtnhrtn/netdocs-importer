# Direct Upload Scaling and Resume

## Overview
Direct upload now runs through an adaptive worker pool with persisted transfer state so uploads can recover after interruption.

## Key behaviors
- Upload starts with bounded concurrency and adapts down on `429`/transient server failures.
- Successful responses gradually restore concurrency.
- Transfer status is persisted per file in `Transfers` so restarts can resume already completed files.
- Preflight remains dry-run and non-mutating.

## Throttle model
- Initial worker window: `min(4, configured_max)`.
- Maximum worker window: configured `MaxConcurrency` (capped at 8).
- On `429`:
  - worker window is reduced (down to 1)
  - backoff increases with jitter
- On sustained success:
  - backoff decreases
  - worker window scales back up gradually

## Resume model
- Before run, existing transfer states are loaded for the current job.
- Files already marked `Succeeded` are treated as resumed and skipped from network upload.
- Remaining files are queued and processed normally.

## Progress model
- UI reports:
  - `Completed/Total`
  - percentage
  - current relative path
- Final summary reports uploaded, skipped, resumed, failed, and created folder counts.

## Retention
- System trace log is pruned to 7 days on startup.
- Per-run direct-upload run logs are written to `completed-jobs` and pruned after 30 days.
- CSV reports remain under `reports`.
