# Export Mode Implementation Plan

## Objective
- In export mode, hide import-only context and drive a NetDocuments-to-disk workflow:
  1. Select NetDocuments source target.
  2. Preflight/export plan.
  3. Execute download with progress, warnings, logs, and resumable diagnostics.

## Target Semantics
- Workspace selection must discover and include:
  - folders (`ndfld`)
  - filters (`ndflt`)
  - saved searches (`ndsq`)
  - collabspaces (`ndcs`)
- If multiple target types are present, show explicit counts and options.
- `Download Filters as folders` controls whether filter scopes are materialized as folder-like output.

## Hierarchy Rules
- Preserve hierarchy and root-level files where possible.
- Folders, saved searches, and collabspaces should map to equivalent Windows folder structure.
- Saved search results should materialize under deterministic folder paths to preserve traceability.

## API + Data Plan
1. Enumeration layer:
   - Walk container hierarchy from selected target.
   - For each scope, enumerate child containers and documents, including root files at selected target level.
   - Keep preflight enumeration lean: do not fetch full version metadata or all custom attributes.
   - Expand versions when `AllVersions` is enabled.
2. Planning layer:
   - Build `ExportPlan` with:
     - container/type counts
     - document/version counts
     - estimated bytes
     - resolved local paths
     - preflight warnings/errors
3. Execution layer:
   - download workers with shared throttle
   - 429 handling (`Retry-After`, global backoff+jitter)
   - stream to temp + atomic rename
4. Output layer:
   - `manifest.json` mapping ND source to local path
   - metadata dump (`json`/`xml`)

## Implementation Sequence (Next)
1. Preflight slimming (MVP-first)
   - Keep current scope traversal (`ndfld`, `ndflt`, `ndsq`, `ndcs`) but reduce per-document payload.
   - Change document list calls to:
     - remove `ValidateWorkspaces` from export document-list queries
     - reduce `select` to minimal set for planning: `StandardAttributes,VersionsLite,ByteSize`
     - stop appending all synced custom attribute ids by default.
   - For `AllVersions=true`, avoid eager full version metadata expansion during preflight; keep counts/identity only where possible.
   - Acceptance:
     - preflight latency and API volume materially reduced for large containers.
     - no behavior regression in scope discovery and item path planning.
     - no repeated no-progress pagination loops.

2. Metadata strategy shift to run phase
   - Treat preflight as planning-only (counts, identities, paths, byte estimate).
   - During download execution, fetch content and required standard attributes together through `v1/Document` per item/version.
   - Defer custom attributes to an optional enrichment path:
     - add `IncludeCustomAttributes` export option (default false), or
     - postpone custom attributes entirely until post-MVP.
   - Acceptance:
     - preflight no longer requires full metadata retrieval for every document.
     - metadata dump remains valid for MVP with standard attributes.
     - custom attribute behavior is explicit and test-covered.

3. Streaming download runner with cancel + shared 429 backoff
   - Introduce `ExportDownloadService` with bounded worker pool (`Concurrency`).
   - Add stream-to-temp-and-rename download pipeline per item.
   - Add shared throttle state for all workers:
     - honor `Retry-After`
     - otherwise exponential backoff + jitter
     - global delay gate to prevent worker stampede.
   - Wire `CancelExport()` with linked token source and UI state updates.
   - Acceptance:
     - cancel stops active run without process restart.
     - 429 events trigger coordinated delay across workers.

4. Export run logs + trace correlation + resumability markers
   - Extend completed job store to support `RunType=Export` naming and active markers.
   - Persist per-run export log with:
     - request method/path/status/latency
     - request/correlation headers where available
     - retry and backoff events
     - per-item final status.
   - Write `.active` marker at start; recover to interrupted summary on startup if marker persists.
   - Add resume skip logic using manifest/metadata state + file existence/size checks.
   - Acceptance:
     - interrupted run is visible in recent jobs as recovered-interrupted export.
     - rerun skips previously completed files deterministically.

## UI/UX Plan
- Export mode should hide:
  - local source-folder scan controls
  - direct upload panel
  - import include/exclude file-tree controls
- Export mode should show:
  - destination folder
  - all versions
  - metadata format
  - download filters as folders
  - preflight issues grid
  - run/cancel/open last artifact actions

## Logging Requirements
- Keep rich trace logs for:
  - every REST request/response status
  - preflight scope decisions
  - path truncation/sanitization
  - download success/failure and retries
- Add export-specific run logs parallel to direct-upload run logs.

## Current Gap Notes
- Current implementation preflight still does heavy document/version metadata enumeration for planning.
- Export document-list requests currently overfetch fields and include `ValidateWorkspaces` in contexts where it is not needed.
- Binary document download execution remains pending; cancel support for execution is still pending.

## 2026-03-07 Update
- The worst preflight query noise has now been reduced:
  - export document-list calls no longer append synced custom attribute ids by default during preflight.
  - export document-list fallback no longer uses `/v2/container/<id>/search` for tenant paths that return `Value cannot be null. (Parameter 'source')`.
  - child-extension backfill no longer uses filtered `/v2/container/<id>?filter=extension...` variants that return `Nothing was provided to iterate over.` for this tenant.
- Remaining known runtime issue:
  - document `3459-7537-1065` still returns `500 Internal Server Error` from `GET /v1/Document/...` during binary export; treat that as a per-document API/runtime failure path, not a preflight-shape issue.
- Rebrand prep:
  - next branch is `wand`.
  - next non-export task is expected to rebrand the app from `NetDocsImporter` to `Wand`.

## Handover Checklist (Next Agent)
1. Confirm preflight API query shape in `NetDocumentsSyncService.Export.cs`:
   - verify `/v2/search/<cabinet>?container=...` remains the primary path for export document pages.
   - avoid reintroducing `/v2/container/<id>/search` on tenants that reject it with `Parameter 'source'`.
2. Keep `select` limited to MVP fields for preflight unless a concrete enrichment need is proven.
3. Keep preflight custom attribute expansion disabled by default; if reintroduced, gate it explicitly and test against tenant-specific container failures.
4. Keep/extend pagination stall guards and tests for repeated-page/no-progress responses.
5. Update `docs/exportmode.md` to reflect the current MVP split:
   - lightweight preflight
   - metadata/content retrieval in run phase (`v1/Document`).
6. For branch `wand`, plan the rename in this order:
   - app/window/product strings
   - docs and user-facing copy
   - package/installer identity
   - icons/logo assets after a logo direction is chosen

## 2026-02-25 Phase Gate Outcome (Validation Run)
- Observed run status:
  - artifacts written: `manifest.json`, `metadata.json`.
  - run-phase standard-attribute enrichment: `0 / 601` planned items.
  - binary download explicitly not wired in this phase.
- Measured output and counters:
  - output directory contained only artifact JSON files (no binary document files).
  - planned count: 601 items.
  - planned byte estimate (sum of `custom.Size`): 410,313,768 bytes (`391.31 MiB`).
- Assessment:
  - this phase is complete for its current scope (enumeration/planning + artifact emission).
  - reported `391 MB` should be treated as estimated download payload, not confirmed transfer.
  - slow UX feedback is currently expected because execution is still metadata/query-heavy and does not yet stream binaries.
- Go/No-Go:
  - `GO` to next implementation phase.
  - next phase exit criteria should require:
    - actual streamed binary writes to destination (temp + atomic rename),
    - cancellation support during run,
    - shared 429 backoff behavior,
    - run logs/resume markers with deterministic skip-on-rerun.
