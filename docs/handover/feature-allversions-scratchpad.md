# Feature Scratchpad: AllVersions

## Branch
- `feature/AllVersions`

## Goal
- Allow `All versions` export only when exact version entries are known for every planned export item.
- Treat `VersionsLite` as advisory summary data, not as a complete version enumeration surface.

## 2026-03-08 Update
- `VersionsLite` handling is now split into:
  - exact coverage
  - multi-version count known but exact ids missing
  - unknown coverage
- Export preflight now records:
  - `KnownVersionCount`
  - `OfficialVersionHint`
  - `CoverageReliable`
  - `NeedsExpansion`
- Targeted expansion is now implemented for unresolved `All versions` documents:
  - primary: `GET /v1/Document/{id}/versionList`
  - fallback: `GET /v1/Document/{id}/info?getVersions=true`
- Preflight only expands documents that still need exact version enumeration.
- Exact version entries now flow into export planning and output artifacts with:
  - `VersionId`
  - `VersionNumber`
  - `IsOfficialVersion`
  - `VersionDiscoverySource`

## Files Updated
- `src/NetDocsImporter.NetDocs/NetDocumentsSyncService.Export.cs`
- `src/NetDocsImporter.NetDocs/NetDocumentsExportModels.cs`
- `src/NetDocsImporter.App/MainViewModel.Export.cs`
- `src/NetDocsImporter.Core/ExportModels.cs`
- `src/NetDocsImporter.Core/ExportOutputWriter.cs`
- `src/NetDocsImporter.Core/ExportCoverageEvaluator.cs`
- `tests/NetDocsImporter.Tests/NetDocumentsSyncServiceExportTests.cs`
- `tests/NetDocsImporter.Tests/ExportOutputWriterTests.cs`
- `tests/NetDocsImporter.Tests/ExportCoverageEvaluatorTests.cs`

## Remaining Gaps
- Preflight expansion currently probes each unresolved document serially from the view model path; if tenant rate limits become noticeable, move this into a bounded async expansion service.
- `VersionNumber` currently follows the returned version identifier fields (`verNo`, `versionId`, `ver`, etc.). If a tenant exposes both a stable id and a distinct display number, split them explicitly.
- Export run logs now include per-document coverage diagnostics in trace output, but the completed-job run log format has not yet been expanded with dedicated structured version-enumeration rows.

## Validation
- `dotnet test .\tests\NetDocsImporter.Tests\NetDocsImporter.Tests.csproj -c Debug --no-restore`
- Exit code `0`
