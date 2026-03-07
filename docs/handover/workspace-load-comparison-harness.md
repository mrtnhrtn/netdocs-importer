# Workspace Load Comparison Harness

## Purpose
`NetDocumentsSyncService.CompareWorkspaceLoadingStrategiesAsync(...)` runs multiple strategies for the same workspace and returns timing + REST-call diagnostics:

1. Current implementation (`GetContainerChildrenAsync` path).
2. UI-like metadata only (`/v2/container/{workspace}/info` -> `/summary`).
3. UI-like sequential (`/v2/container/{workspace}/info` -> `/summary` -> per-summary `/v2/container/{id}/?...`).
4. UI-like parallel (same as sequential, but per-summary `/v2/container/{id}/?...` calls run in parallel).

## Location
- Service method: `src/NetDocsImporter.NetDocs/NetDocumentsSyncService.Targets.cs`
- Models: `src/NetDocsImporter.NetDocs/NetDocumentsDiagnosticsModels.cs`
- API call tracing hook: `src/NetDocsImporter.NetDocs/NetDocumentsApiClient.cs`

## Usage
Call from any existing sync-service entry point with authenticated context:

```csharp
var comparison = await sync.CompareWorkspaceLoadingStrategiesAsync(cabinetId, workspaceId, cancellationToken);
```

In the app UI (`Select Folder` step), use:
- `Compare + Export JSON` button in the target browser toolbar.
- It opens a Save dialog, runs both strategies, and writes the full JSON report.

## Returned Data
- Strategy-level:
  - `Succeeded`
  - `ErrorMessage`
  - `DurationMs`
  - `ContainerCount`
  - `SummaryRowCount`
  - `DocumentCount`
- Per REST call (`ApiCalls`):
  - `Sequence`
  - `Method`
  - `RelativePath`
  - `Url`
  - `StatusCode`
  - `Succeeded`
  - `DurationMs`
  - `ResponseLength`
  - `ResponsePreview`
  - `ErrorMessage`

## Notes
- Per-call tracing is scoped to the comparison run and does not alter existing runtime flow.
- `ResponsePreview` is truncated/sanitized for diagnostics safety.
- For target-browser speed decisions, compare `CurrentStrategy` vs `UiLikeMetadataOnlyStrategy` first (enumeration-only).
- `UiLikeStrategy` and `UiLikeParallelStrategy` include per-summary document list calls and are intended to show full UI-style loading cost.

## Browser Path Rollout
- Workspace child enumeration now tries `info + summary` first in `GetContainerChildrenAsync`.
- Legacy browse-query/endpoints are still retained as a temporary fallback with explicit `TEMP-FALLBACK` comments and trace logs.
- Monitor logs for summary failures before removing fallback paths.
