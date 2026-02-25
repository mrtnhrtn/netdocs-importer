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
   - For each scope, enumerate child containers and documents.
   - Pull standard attributes + cabinet custom attributes for each document/version.
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
- Current implementation wires UI + preflight container topology only.
- Binary document download execution and full document/version enumeration remain to be implemented.
- Cancel support for export execution is still pending.
