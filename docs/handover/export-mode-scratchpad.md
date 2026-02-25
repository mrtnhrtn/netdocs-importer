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

## Design Decisions
- Kept all changes additive to avoid importer regressions.
- Did not modify direct upload or ndImport execution paths.
- Persisted export mode state under `NetDocumentsConnectionSettings` to keep mode and ND target context together.

## Next Steps (for next agent)
1. Implement export planner service in Core:
   - Traverse ND subtree from selected target.
   - Build `ExportPlan` with counts/size estimates/warnings.
2. Add NetDocuments API client methods for export traversal and document stream download.
3. Implement export runner with shared 429 throttle handling.
4. Wire planner + writers into export run completion path.
5. Add UI in step 2 for export destination, metadata format, and all-versions option.

## Risks / Notes
- `LoadSettingsAsync` still forces `ImportExecutionMode.DirectApi` in current codebase; export toggle currently only controls new mode flag and UI text.
- Existing target-browser flows are unchanged; this is intentional per "do not break importer code."
