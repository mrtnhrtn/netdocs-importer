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
  - run export currently writes manifest/metadata plan artifacts (download execution pending)

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
1. Implement full document/version enumeration in NetDocuments layer (not just container topology).
2. Add binary download runner with cancellation + shared throttle + 429 global backoff.
3. Write export run log format (parallel to direct upload run logs) and include REST trace correlation.
4. Implement resume/restart semantics for interrupted export runs.
5. Add tests for export preflight topology traversal and path mapping determinism.

## Risks / Notes
- `LoadSettingsAsync` still forces `ImportExecutionMode.DirectApi` in current codebase; export toggle currently only controls new mode flag and UI text.
- Existing target-browser flows are unchanged; this is intentional per "do not break importer code."
- `Run Export` currently emits plan artifacts (`manifest.json` + metadata) and does not yet download document binaries.
