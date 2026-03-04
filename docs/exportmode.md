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
  - Build `ExportPlan` with counts (docs/versions), byte estimate, and preflight warnings.
  - Pre-calculate local output paths and collision handling.
- Run phase:
  - Default `Concurrency = 8` workers.
  - Worker downloads are streamed directly to disk.
  - Cancellation token is honored across traversal and download loops.
  - Manifest and metadata dump are written to destination root at completion.

## Rate Limiting + 429 Handling
- A shared global throttle is applied across all workers.
- On HTTP `429`:
  - respect `Retry-After` when present
  - otherwise use exponential backoff with jitter
  - update global throttle window so all workers delay together
- Goal: avoid worker stampede and reduce repeated throttle failures.

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
