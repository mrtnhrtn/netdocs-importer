# Workspace Export Coverage Assurance

This note is written for customers and project stakeholders who want confidence that a workspace export captured the content they expected.

## What the exporter does

When you export from a workspace, the application walks the selected workspace and its supported child locations, including folders, saved searches, collabspaces, and optional workspace filters. It also checks for files that live directly at the workspace level, not only inside child folders.

If the same document is visible through more than one workspace surface, the exporter keeps one canonical exported file and records the alternate source locations in the export artifacts. This avoids duplicate files while still preserving traceability.

## What evidence is produced

Each export run keeps its own retained artifacts in the destination folder:

- `manifest-<run>.json`
- `metadata-<run>.json` or `metadata-<run>.xml`
- the export run log

Together, these files are the audit trail for that run.

The manifest shows which document or version was exported and where it was written locally.

The metadata file shows the status of each planned item, including whether it succeeded or failed.

The run log shows the overall counts and the per-item outcomes for the run.

## What gives confidence that content was retained

The strongest evidence of a complete export is:

- the preflight counts look reasonable for the workspace
- the export finishes without warnings
- the metadata file shows no failed items
- the manifest and metadata files are retained with the exported content

When those conditions are met, the customer has a durable record of what the exporter planned, what it downloaded, and where each item was written.

## How duplicate search surfaces are handled

Some NetDocuments workspaces expose the same document through multiple locations, such as a folder and a saved search. In those cases, the exporter does not create duplicate files on disk.

Instead:

- one export path is chosen as the canonical retained file
- alternate matching locations are recorded in the manifest as source references

This means a single exported file can still be traced back to all workspace locations that exposed it.

## Current limits to be aware of

This assurance is strong, but it is not yet an absolute completeness guarantee in every edge case.

Current known limits:

- if a child-container enumeration request fails during traversal, preflight is marked incomplete and the export should be corrected and re-run rather than treated as clean
- `All versions` is only reliable when NetDocuments returns version data through the supported preflight payloads

Because of those limits, the product should describe the export as giving high confidence for successfully enumerated content, backed by retained artifacts, rather than claiming perfect completeness in all cases.
