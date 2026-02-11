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
