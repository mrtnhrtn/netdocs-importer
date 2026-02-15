# Architecture Overview

## Solution Layout

- `src/NetDocsImporter.App`
  - WPF shell, views, and MVVM view models.
  - Orchestrates user flow (target selection, scan/preflight, run/upload, history).
- `src/NetDocsImporter.Core`
  - Core domain contracts and workflows (scan, export options, upload planning contracts).
- `src/NetDocsImporter.NetDocs`
  - NetDocuments API integrations (auth, sync, target browser, direct upload service).
- `src/NetDocsImporter.Data`
  - SQLite-backed persistence for jobs, files/folders, transfer states, and cached metadata.
- `tests/NetDocsImporter.Tests`
  - Unit/integration-style tests for core workflows and NetDocuments behaviors.

## MVVM Composition

- `MainWindow.xaml` hosts step content.
- `MainViewModel` is split across partial files by concern:
  - `MainViewModel.NetDocuments.cs`
  - `MainViewModel.TargetBrowser.cs`
  - `MainViewModel.DirectUpload.cs`
  - base `MainViewModel.cs`
- Views raise UI events to `MainWindow.xaml.cs`, which forwards to `MainViewModel` commands/methods.

## Runtime Data Flow

### 1) Target Selection

1. User connects via OAuth context.
2. `NetDocumentsSyncService` synchronizes cabinets/attributes/lookups into `JobStore`.
3. Target browser resolves selected destination and profile snapshot.

### 1a) Folder NEV Hydration + Default Inference

1. Workspace/folder expansion can return container IDs (`.nev`, `^F`, `^C`, `^W`) without a usable display name.
2. `NetDocumentsSyncService.GetContainerChildrenAsync` now treats ID-like names as incomplete and hydrates each row with `GET /v2/container/{id}/info`.
3. Hydrated metadata is used for:
   - display name normalization in the target tree (human label over raw NEV),
   - profile default extraction from container attributes for later upload planning.
4. Default resolution priority is:
   - v1 profile-default endpoints (when available),
   - workspace lookup context (client/matter lookup keys),
   - v2 container info attribute payloads (including numeric attribute tokens such as `"2"` / `"3"` mapped to synced attribute metadata).
5. When selecting a folder from workspace-lookup flow, lookup-context defaults are carried into the profile snapshot if endpoint defaults are sparse.

### 1b) Workspace/Container Invariants

1. Child expansion under workspace/folder tree nodes must surface `ndfld`, `ndflt`, and `ndcs` when present.
2. Only folders/collabspaces (`ndfld`, `ndcs`) are expandable; workspace filters (`ndflt`) are terminal targets (`HasChildren=false`).
3. Saved searches (`ndsq`) are intentionally unsupported in this app and are excluded from browse targets.
4. Collabspaces are modeled as folder targets in this codebase for browse/upload behavior.
5. API calls used for target browsing/upload planning are restricted to documented REST API manual/v2 Swagger endpoint/parameter shapes. The only known exception retained is `indexpriority`.

### 2) Local Scan + Preflight

1. `ScanJobRunner` writes scanned folders/files to `JobStore`.
2. Direct-upload preflight (`IDirectUploadService.BuildPlanAsync`) resolves destination folders and validates upload readiness.
3. UI displays issues and actionable blockers/warnings.

### 3) Direct Upload Execution

1. View model rebuilds execution plan with runtime creation enabled.
2. `NetDocumentsDirectUploadService.UploadAsync` performs upload using adaptive concurrency and retry logic.
3. Transfer state is persisted per file for resumability/reporting.
4. Final summary is written to reports and surfaced in Recent Jobs.

## Key Design Constraints

- Preflight is read-only and must not create folders.
- Folder creation/materialization occurs only during execution.
- Skip-worthy file issues (for example 0-byte and missing files) are reported without blocking valid uploads.
- Target/profile selection state is cached and reused to minimize unnecessary API calls.

## Observability

- API traces include method/path/status and throttle-related headers where available.
- Upload plans and run results produce CSV reports for user review.
- App-level crash and trace logging are centralized under local app data.
