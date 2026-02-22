# Job Queue MVP Handover Notes

## Scope
- Branch: `feature/jobqueue-mvp`
- Base: `feature/foldersWS`
- Goal: Add MVP upload job queue/scheduling without destabilizing existing direct upload flow.

## Key Constraints
- Keep existing `Run Direct Upload` behavior intact.
- Extend existing Recent Jobs UI, do not replace.
- Queue is FIFO with Option B ordering for next-run selection:
  1. `ScheduledFor` ascending (`null` last)
  2. `CreatedAt` ascending
- Single running job at a time.
- Persist immutable upload config snapshot at job creation.

## Current Architecture Map
- Direct upload execution: `src/NetDocsImporter.App/MainViewModel.DirectUpload.cs`
- Recent jobs history: `src/NetDocsImporter.App/MainViewModel.cs` + `src/NetDocsImporter.App/Views/Steps/RecentJobsStepView.xaml`
- Startup hook: `src/NetDocsImporter.App/MainWindow.xaml.cs` (`OnLoaded`)
- Persistence layer: `src/NetDocsImporter.Data/JobStore.cs`
- Existing clock abstraction: `src/NetDocsImporter.Core/IClock.cs`

## Implementation Plan (File-Level)
1. Core queue contracts + monitor service
2. Data queue persistence/table/methods in `JobStore`
3. Unit tests for transitions/order/promotion/single-runner/restart
4. App integration in ViewModel + startup monitor wiring
5. UI updates (buttons, queue tab, quick view menu)

## Progress Log
- 2026-02-22: Repository scan complete; hotspots identified.

- 2026-02-22: Added queue persistence schema/methods in `JobStore` (`UploadQueueJobs` table + Option B ordering + single-runner acquire semantics).
- 2026-02-22: Added core monitor primitives (`UploadJobMonitor`, `IUploadRunner`) using `IClock`.
- 2026-02-22: Added tests:
  - `JobQueueStoreTests` (state transitions, Option B ordering, due promotion, single-runner enforcement)
  - `UploadJobMonitorTests` (execution + restart behavior)
- 2026-02-22: Added app integration (`MainViewModel.JobQueue.cs`): queue/schedule actions, queue monitor lifecycle hooks, quick-view data, and queued upload runner.
- 2026-02-22: UI changes:
  - Added `Add to Queue` and `Schedule...` buttons near direct upload controls in both direct-upload surfaces.
  - Extended Recent Jobs step with Queue tab + startup notice.
  - Added top-level quick Jobs menu (running + next queued + open full jobs view).
- 2026-02-22: Validation run:
  - `dotnet test tests/NetDocsImporter.Tests/NetDocsImporter.Tests.csproj` (pass)
  - `dotnet build src/NetDocsImporter.App/NetDocsImporter.App.csproj` (pass)
