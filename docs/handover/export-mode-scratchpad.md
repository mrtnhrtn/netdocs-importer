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

## Files Updated
- `src/NetDocsImporter.Core/ExportModels.cs` (new)
- `src/NetDocsImporter.Core/NetDocumentsConnectionSettings.cs`
- `src/NetDocsImporter.App/MainViewModel.cs`
- `src/NetDocsImporter.App/MainWindow.xaml`
- `src/NetDocsImporter.App/MainWindow.xaml.cs`

## Design Decisions
- Kept all changes additive to avoid importer regressions.
- Did not modify direct upload or ndImport execution paths.
- Persisted export mode state under `NetDocumentsConnectionSettings` to keep mode and ND target context together.

## Next Steps (for next agent)
1. Implement export planner service in Core:
   - Traverse ND subtree from selected target.
   - Build `ExportPlan` with counts/size estimates/warnings.
2. Add NetDocuments API client methods for export traversal and document stream download.
3. Add deterministic Windows-safe path resolver + collision strategy.
4. Implement export runner with shared 429 throttle handling.
5. Write `manifest.json` and metadata dump (`json`/`xml`) at completion.
6. Add UI in step 2 for export destination, metadata format, and all-versions option.

## Risks / Notes
- `LoadSettingsAsync` still forces `ImportExecutionMode.DirectApi` in current codebase; export toggle currently only controls new mode flag and UI text.
- Existing target-browser flows are unchanged; this is intentional per "do not break importer code."
