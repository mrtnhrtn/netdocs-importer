# Export Mode Design

## Goals
- Add an 'Export' mode as an alternative second step, activated by a toggle button with 'Export mode' text near the gears icon but when in export mode it says "Import"
- Reuse the current NetDocuments target browser/location resolution flow (Recent, Favorites, Go to Workspace, browse expansion).
- In Export mode, treat the selected NetDocuments node as source and download to local disk using GET v1/Document with standardattributes param.
- Preserve source structure where possible and provide deterministic fallbacks for Windows path limits.

Do not break import code or change query or order of existing netdocuments calls.

## Reuse Points
- `MainViewModel.TargetBrowser.cs` remains the single source of truth for ND target selection and workspace lookup.
- `NetDocumentsSyncService.Targets.cs` remains the primary source for ND container discovery/path resolution.
- Existing settings persistence (`AppSettings` + `NetDocumentsConnectionSettings`) is extended instead of replaced.

## New Export Architecture
- New DTOs in Core:
  - `ExportConfig`
  - `ExportPlan`
  - `ExportItem`
  - `ExportResult`
  - `MetadataDump`
- New Core helpers:
  - deterministic Windows-safe naming/path resolver
  - export manifest + metadata writers
- New NetDocuments export operations:
  - enumerate export source subtree (folders/docs)
  - enumerate document versions when `AllVersions` is selected
  - stream document/version content to disk (temp file + atomic rename)
  Expect thousands 

## Execution Model
- Plan phase:
  - Traverse the selected ND subtree.
  - Build `ExportPlan` with counts (docs/versions), byte estimate, preflight warnings, and deterministic local paths.
  - Keep preflight lightweight; use document-list data for planning only (ids, paths, versions-lite, estimated size).
  - Pre-calculate local output paths and collision handling.
- Run phase:
  - Default `Concurrency = 8` workers.
  - Worker downloads are streamed directly to disk.
  - Retrieve richer per-item standard attributes via `v1/Document` during execution (content + metadata phase).
  - Current MVP wiring performs best-effort `v1/Document` standard-attribute enrichment while writing metadata artifacts, even before binary streaming is fully enabled.
  - Cancellation token is honored across traversal and download loops.
  - Manifest and metadata dump are written to destination root at completion.

## Full Enumeration Design
- Enumeration must include:
  - all child containers discoverable from the selected root (`ndfld`, `ndflt`, `ndsq`, `ndcs`)
  - documents directly under each container
  - root-level files that exist directly under the selected target (not only nested folders)
  - document versions when `AllVersions=true`
- Container traversal reuses existing target-browser primitives:
  - `GetContainerChildrenAsync(...)` for recursive container discovery
  - existing id normalization (`ResolveContainerIdForBrowseAsync`) and cross-tenant container-id candidate logic
- Document list retrieval uses list responses with:
  - `select=StandardAttributes,VersionsLite,ByteSize` for MVP preflight.
  - `listflags=Documents,ByteSize` (without `ValidateWorkspaces`) for export document-list queries.
  - custom attribute field ids appended to `select` only when `Include custom attributes` is enabled.
  - pagination until exhaustion (`top/skip` or `skiptoken`, depending on endpoint shape)
- Version expansion rules:
  - `AllVersions=false`: emit one `ExportItem` for the official/latest version
  - `AllVersions=true`: emit one `ExportItem` per version with stable `VersionId`
- Every `ExportItem` carries:
  - source identity: cabinet id, container id/path, document id, optional version id
  - metadata snapshot: lightweight preflight metadata; richer standard attributes are intended for run-phase retrieval
  - deterministic local path from `ExportPathResolver`
- Preflight counters must reflect:
  - `ContainerCount`, `DocumentCount`, `VersionCount`, `EstimatedBytes`
  - warning counts for unsupported/failed scopes and metadata fallback paths

## Workspace Coverage Confidence
When the user selects a workspace as the export root, the current search/enumeration behavior is intended to give confidence that all reachable items under that workspace were planned and exported.

For customer-facing wording, see `docs/export-workspace-coverage-assurance.md`.

What the exporter does today:
- Normalize the selected root id before traversal so Recent/Favorite workspace ids and browse ids converge on the same container identity.
- Traverse the selected root breadth-first across all supported child container types:
  - folders (`ndfld`)
  - workspace filters (`ndflt`) when the option is enabled
  - saved searches (`ndsq`) even when filters are otherwise excluded
  - collabspaces (`ndcs`)
- Enumerate documents directly under every discovered container, including files that live at the selected workspace root and not only inside child folders.
- Continue paging document queries until exhaustion, using `skiptoken` or offset progression depending on the endpoint shape.
- Deduplicate repeated container hits by `(target type, container id)` so the same branch is not traversed twice under alternate ids.
- Deduplicate repeated document/version hits by `(document id, version id)` across overlapping scopes, while keeping the preferred folder/workspace surface as the canonical export path.

What gives the user evidence after the run:
- `manifest-<run>.json` records the canonical exported path for each document/version.
- Each manifest item also keeps `SourceReferences` for every overlapping search surface that matched the same item, marking them as `Exported` or `SkippedDuplicate`.
- `metadata-<run>.json` or `.xml` records per-item status so the user can distinguish planned-but-failed downloads from items that were never discovered.
- The export run log records aggregate counts and per-item outcomes, which is the audit trail for a specific run.

What this means in practice:
- If a document appears through multiple workspace surfaces, it should still produce one exported file, not duplicates.
- The retained manifest and metadata artifacts are the proof set for that run: one file per canonical item, plus the alternate source references showing where duplicates were suppressed.
- A clean run with expected counts, no preflight warnings, and no per-item failures is the strongest evidence we currently provide that the workspace content was retained.

Current known confidence limits:
- Scope traversal failures in `EnumerateExportScopesAsync(...)` are now surfaced as export preflight issues and mark the result incomplete, so export should not proceed until those coverage issues are resolved.
- `All versions=true` is only reliable when version data is present in `VersionsLite`. If version hints are absent, the current fallback path does not prove full historical version coverage.
- The confidence claim is therefore accurate for reachable, successfully enumerated containers and for the version data actually returned by the supported APIs used in preflight.

## Custom Attributes Option
- `Include custom attributes` is an export option and defaults to `false`.
- With default settings, preflight does not expand synced custom-attribute ids into document-list `select`.
- If enabled, preflight can request custom-attribute ids for planning metadata enrichment.
- Run-phase metadata/content retrieval remains the primary enrichment path target through `v1/Document`.

## Rate Limiting + 429 Handling
- A shared global throttle is applied across all workers.
- On HTTP `429`:
  - respect `Retry-After` when present
  - otherwise use exponential backoff with jitter
  - update global throttle window so all workers delay together
- Goal: avoid worker stampede and reduce repeated throttle failures.

### Shared Backoff Contract
- Introduce a process-wide `ExportThrottleState` used by all export workers in a run:
  - `DelayUntilUtc` (volatile timestamp gate)
  - rolling backoff attempt count
  - last `429` metadata (status, endpoint, retry-after, request id)
- Worker request flow:
  1. Await global throttle gate (`DelayUntilUtc`) before issuing request.
  2. Execute request with stream response headers-first.
  3. On `429`, compute delay:
     - if `Retry-After` header present: use it
     - otherwise: `min(base * 2^attempt + jitter, maxBackoff)`
  4. Atomically push shared `DelayUntilUtc` forward if computed delay is later.
  5. Retry request until per-item max attempts is reached.
- Cancellation behavior:
  - one linked `CancellationTokenSource` per run
  - UI `Cancel` flips token; workers exit promptly between retries and during stream copy
  - partially downloaded temp files are deleted on cancel/fail

## Path + Naming Rules
- Invalid Windows characters are sanitized: `<>:"/\\|?*` plus control characters.
- Trailing dots/spaces are removed.
- Reserved names are rewritten (`CON`, `PRN`, `AUX`, `NUL`, `COM1..9`, `LPT1..9`).
- Deterministic collision handling adds stable suffixes.
- Long-path fallback:
  - deterministic shortening based on source identity hash
  - preserve extension when possible
  - write `manifest.json` mapping ND source -> resolved local path.

## Output Files
- `manifest.json` (always): source identifiers/paths mapped to local output.
- Metadata dump:
  - `metadata.json` or `metadata.xml` per user selection
  - includes workspace/doc/version identifiers, profile attributes, timestamps, sizes, source path, local path, status, and errors.

## Run Log + Resume Markers
- Add export run artifacts parallel to direct upload:
  - `completed-jobs/export-<jobId>-<timestamp>-runlog.txt`
  - `completed-jobs/export-<jobId>-<timestamp>.json` (summary)
  - `completed-jobs/export-<jobId>-<timestamp>.active` (in-progress marker)
- Run log content:
  - header: run id, cabinet/target, options, counters, destination root
  - request trace rows with correlation fields:
    - request sequence
    - method/path
    - HTTP status
    - latency
    - request-id/correlation-id headers when present
    - retry attempt and throttle delay
  - per-item outcome rows:
    - document/version id
    - local path
    - bytes written
    - resumed/skipped/succeeded/failed/canceled
    - error snippet
- Resume semantics:
  - manifest and metadata include per-item `Status` + `CompletedUtc` + `ContentLength`
  - rerun in same destination skips items already marked complete and present on disk with expected size
  - active marker is written at run start, removed on normal completion, and converted to interrupted summary at startup recovery

## Deferred Export Job Mode (Server Queue -> Later Download)
- Captured browser flow confirms job submission from workspace:
  - `POST /v2/export/container` returns a string job id.
  - `PUT /v2/user/option/exportpreferences` persists the selected/default export options.
- This supports a deferred model: submit now, fetch/download later.

### Feasibility
- Yes, you can queue export jobs first, then present results later in-app.
- Yes, you can multi-thread downloads of multiple completed job artifacts and repackage into one zip.
- Constraint: the SAZ confirms submit only; status/list/result download endpoints must still be captured and validated before implementation.

### Proposed Pipeline
1. Submit export job (`POST /v2/export/container`) and persist:
   - ND job id
   - cabinet/container ids
   - options payload hash
   - submitted timestamp
2. Poll/list queued jobs using ND export-job status endpoints (to be discovered from capture):
   - states: queued/running/completed/failed/expired
   - expected output metadata: filename, size, checksum/etag when available
3. On completed jobs, download artifacts with bounded concurrency:
   - stream to temp file, validate size/checksum, atomic rename
   - shared 429 throttle/backoff across workers
4. Repack:
   - if one artifact only: keep original zip unless user asked to normalize package naming
   - if multiple artifacts: compose one deterministic bundle zip
5. Emit aggregate manifest:
   - source job id -> local artifact path
   - per-artifact status/error/retry info

### Edge Conditions
- ND export artifacts may have retention/expiry windows; downloader should prioritize oldest completed jobs first.
- Avoid nested zip confusion: if artifacts are already zip files, repack should preserve originals and avoid unzip/rezip unless explicitly requested.
- Deduplicate bundle entries by stable naming (`<jobId>/<artifactName>`) to prevent collisions.
