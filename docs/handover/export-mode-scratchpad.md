# Export Mode Scratch Pad

## Branch
- `export-mode` (based on `feature/foldersWS`)

## Current Status
- Phase 1 scaffolding complete without altering importer runtime behavior.
- Added export domain models in Core.
- Added persisted export-mode settings fields on `NetDocumentsConnectionSettings`.
- Added UI toggle next to settings gear:
  - Shows `Export mode` when import mode is active.
  - Shows `Import` when export mode is active.
- Phase 2 core helpers complete:
  - deterministic export path resolver (`ExportPathResolver`)
  - manifest + metadata writers (`ExportOutputWriter`)
  - unit tests for resolver/output writer
- Phase 3 mode flow wiring complete:
  - import-only review context hidden when export mode is active
  - direct-upload panel replaced with export panel in Review Scope step
  - export preflight traverses NetDocuments container topology and reports:
    - folders
    - filters
    - saved searches
    - collabspaces
  - export preflight options include:
    - destination folder
    - all versions
    - metadata format
    - download filters as folders
  - run export now downloads binaries to disk and writes manifest/metadata artifacts
  - export run supports:
    - bounded worker concurrency
    - temp-file download + atomic rename
    - cancel during run
    - shared 429/retry backoff
    - per-run export log output
    - active-run marker creation/deletion

## Files Updated
- `src/NetDocsImporter.Core/ExportModels.cs` (new)
- `src/NetDocsImporter.Core/NetDocumentsConnectionSettings.cs`
- `src/NetDocsImporter.App/MainViewModel.cs`
- `src/NetDocsImporter.App/MainWindow.xaml`
- `src/NetDocsImporter.App/MainWindow.xaml.cs`
- `src/NetDocsImporter.Core/ExportPathResolver.cs` (new)
- `src/NetDocsImporter.Core/ExportOutputWriter.cs` (new)
- `tests/NetDocsImporter.Tests/ExportPathResolverTests.cs` (new)
- `tests/NetDocsImporter.Tests/ExportOutputWriterTests.cs` (new)
- `src/NetDocsImporter.App/MainViewModel.Export.cs` (new)
- `src/NetDocsImporter.App/Views/Steps/ReviewScopeStepView.xaml`
- `src/NetDocsImporter.App/Views/Steps/ReviewScopeStepView.xaml.cs`
- `docs/handover/export-mode-implementation-plan.md` (new)

## Design Decisions
- Kept all changes additive to avoid importer regressions.
- Did not modify direct upload or ndImport execution paths.
- Persisted export mode state under `NetDocumentsConnectionSettings` to keep mode and ND target context together.

## Next Steps (for next agent)
1. Close the `All versions` truthfulness gap:
   - keep using `VersionsLite` when exact version ids are present.
   - do not silently imply full historical coverage when only the official version is known.
   - decide whether the UX should warn, block, or capability-gate `All versions` when coverage cannot be proven.
2. Finish export metadata enrichment:
   - the run-phase helper for `v1/Document` standard attributes exists.
   - wire that helper into the actual export run so metadata output reflects fetched run-phase attributes rather than only preflight fields.
3. Decide and implement the product behavior for `Include custom attributes`:
   - selected subset vs all synced custom attributes
   - run-phase enrichment vs explicit disable/remove of the toggle
4. Finish export resumability:
   - active-run marker creation/deletion is implemented
   - startup recovery summary and deterministic skip-on-rerun are still pending
5. Add targeted tests for:
   - binary export success/failure and retry behavior
   - shared backoff coordination
   - export cancellation cleanup
   - export active-marker recovery and skip-on-rerun semantics

## Risks / Notes
- `LoadSettingsAsync` still forces `ImportExecutionMode.DirectApi` in current codebase; export toggle currently only controls new mode flag and UI text.
- Existing target-browser flows are unchanged; this is intentional per "do not break importer code."
- `Run Export` now downloads binaries, but resumability is still partial: active-run markers exist, while startup recovery and skip-on-rerun are not fully wired.
- `All versions` is only as reliable as the `VersionsLite` data returned by the tenant/query shape; the undocumented fallback version enumeration path remains disabled.
- `Include custom attributes` still does not enrich export output end-to-end.

## Design Notes Added This Iteration
- `docs/exportmode.md` now includes:
  - full enumeration contract (containers + root files + versions)
  - shared worker throttle/backoff contract for 429 handling
  - export run log and resumability marker format
- `docs/handover/export-mode-implementation-plan.md` now includes an implementation sequence with acceptance criteria.
- Handover updated to prioritize MVP-first preflight slimming and ND API param minimization before full metadata enrichment.

## 2026-02-25 - Follow-up fix after version probe circuit-breaker
- Fixed a correctness gap in export preflight version probe breaker semantics:
  - failure streak now resets on any successful version fetch.
  - failure streak now resets on any non-all-expected-failure outcome.
  - breaker only trips after 3 consecutive documents where all endpoint probes fail with expected 400/405.
- Added regression test coverage for true consecutiveness:
  - `EnumerateDocumentVersionsAsync_OnlyTripsBreakerAfterConsecutiveClientErrorDocuments`
- Files updated:
  - `src/NetDocsImporter.NetDocs/NetDocumentsSyncService.Export.cs`
  - `tests/NetDocsImporter.Tests/NetDocumentsSyncServiceExportTests.cs`
- Validation:
  - `dotnet test tests/NetDocsImporter.Tests/NetDocsImporter.Tests.csproj --filter NetDocumentsSyncServiceExportTests --nologo`
  - Passed: 10
  - Failed: 0
- Notes for next agent:
  - verify in-app UX still advances quickly when breaker trips on large `ExportAllVersions=true` scopes.
  - if needed, add explicit UI status text when version probing is disabled (currently trace-only).

## 2026-02-25 - Phase validation run (artifacts-only export)
- Runtime status observed:
  - `Export artifacts written. Manifest: manifest.json, metadata: metadata.json.`
  - `Run-phase standard attribute enrichment applied to 0 of 601 planned item(s).`
  - `Document binary download execution will be wired in the next phase.`
- Artifact verification:
  - output folder contained only `manifest.json` and `metadata.json` (no downloaded document binaries yet).
  - `manifest.json` size: 127,098 bytes.
  - `metadata.json` size: 700,739 bytes.
- Planning counters verified from emitted metadata:
  - planned items: 601.
  - summed `custom.Size`: 410,313,768 bytes (`391.31 MiB`, `410.31 MB` decimal).
  - interpretation: the reported ~391 MB is planned payload size for eventual binary download, not proof of bytes transferred to local disk in this phase.
- Performance observation:
  - user reported slow feedback in this run; this is consistent with planning/enumeration-heavy REST traversal and best-effort per-item run-phase attribute fetches.
  - no binary transfer occurred in this phase.
- Readiness decision:
  - phase objective for artifacts-only export is met.
  - project is ready to proceed to next phase (binary streaming download runner + cancel + shared 429 backoff + run logs/resume markers).
  - recommended immediate acceptance gate for next phase kickoff: first end-to-end run must produce actual files on disk and per-item success/failure in run logs.

## 2026-03-07 - Export preflight query cleanup and current runtime state
- Fixed the remaining noisy export preflight request variants that were still producing tenant-specific `400 Bad Request` responses:
  - export document enumeration no longer appends synced custom attribute ids into preflight `select`.
  - export document enumeration no longer falls back to `/v2/container/<id>/search` endpoints that return `Value cannot be null. (Parameter 'source')` on this tenant.
  - export child-expansion backfill no longer uses filtered `/v2/container/<id>?filter=extension...` variants that return `Nothing was provided to iterate over.` on this tenant.
  - extension-search backfill now prefers `/v2/search/<cabinet>?container=...`.
- Validation:
  - `dotnet test tests/NetDocsImporter.Tests/NetDocsImporter.Tests.csproj --filter "FullyQualifiedName~NetDocumentsSyncServiceExportTests|FullyQualifiedName~NetDocumentsSyncServiceTargetsTests" --nologo`
  - Passed: 60
  - Failed: 0
- Runtime notes from the latest live export run on 2026-03-07:
  - the recurring `Value cannot be null. (Parameter 'source')` errors were traced to `/v2/container/.../search` fallback requests and are addressed by this fix for the next app run.
  - a separate `500 Internal Server Error` still occurs for document `3459-7537-1065` on `GET /v1/Document/3459-7537-1065`; this appears tenant/API-side and is not caused by preflight query shape.
- Files updated in this iteration:
  - `src/NetDocsImporter.App/MainViewModel.Export.cs`
  - `src/NetDocsImporter.Core/ExportModels.cs`
  - `src/NetDocsImporter.Core/ExportPathResolver.cs`
  - `src/NetDocsImporter.NetDocs/NetDocumentsSyncService.Export.cs`
  - `src/NetDocsImporter.NetDocs/NetDocumentsSyncService.Targets.cs`
  - `tests/NetDocsImporter.Tests/ExportPathResolverTests.cs`
  - `tests/NetDocsImporter.Tests/NetDocumentsSyncServiceExportTests.cs`
  - `tests/NetDocsImporter.Tests/NetDocumentsSyncServiceTargetsTests.cs`

## 2026-03-08 - Export preflight coverage surfacing and warning UX cleanup
- Export scope traversal no longer silently hides child-enumeration failures during preflight:
  - `EnumerateExportScopesAsync(...)` now returns surfaced traversal issues.
  - export preflight adds those failures into the visible preflight issues list as `SCOPE_ENUMERATION_FAILED`.
  - export plans with traversal failures are marked as having blocking coverage issues.
  - `Run Export` is blocked when preflight is incomplete for coverage reasons.
- Export warning visibility is now aligned with the existing UI surface:
  - preflight warnings are shown inside the actual `Preflight Issues` grid as warning rows.
  - the temporary separate warnings panel was removed.
  - the transient WPF crash from the bad converter reference in that temporary panel was fixed.
- Current practical state:
  - the branch is in a mergeable state.
  - workspace export confidence messaging, surfaced coverage failures, and visible preflight warnings are now internally aligned.
  - remaining known runtime issue is still the tenant/API-side `500` for document `3459-7537-1065` during `GET /v1/Document/...`, which is outside this preflight UX fix.

## 2026-03-08 - Export run pipeline landed in main
- Export execution is no longer artifacts-only:
  - `Run Export` now downloads document binaries with bounded parallel workers.
  - files stream to `*.part` and are atomically renamed on success.
  - export honors cancel requests during the run.
  - retriable failures use shared backoff state, including `429 Retry-After` handling.
  - per-run export logs and completed-job summaries are written.
  - active export run markers are created and deleted through `CompletedJobLogStore`.
- Remaining gaps after this landing:
  - run-phase standard-attribute enrichment helper exists but is not yet wired into the export metadata path.
  - startup recovery for interrupted export markers is not yet implemented.
  - deterministic skip-on-rerun/resume semantics are not yet implemented.
  - `All versions` still depends on `VersionsLite` coverage; the undocumented fallback enumeration path remains intentionally disabled.
  - `Include custom attributes` still warns as deferred rather than exporting end-to-end.

## Next Branch Preparation
- Created branch: `wand`
- Intended next stream of work:
  - rebrand `NetDocsImporter` to `Wand`
  - update app name, window title, installer/user-facing strings, and associated docs
  - choose a simple black-and-white wand mark for the app icon/branding direction before touching assets
