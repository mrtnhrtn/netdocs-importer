# Branch Scratchpad: 4 and 5 Deprecation

## Branch
- Proposed branch: `codex/deprecate-legacy-defaults-and-scope-explorer`
- Purpose: remove dead v1 target-default endpoint scaffolding and retire the permanently-disabled legacy import scope explorer UI.

## Findings Being Addressed
- Finding 4: dead v1 target-default endpoint family scaffolding.
- Finding 5: legacy scope explorer UI is permanently disabled but still present in view model and XAML.

## Action Log
- 2026-03-07: Reviewed the code path around `BuildDefaultEndpointCandidates(...)` and confirmed it always returns no candidates.
- 2026-03-07: Confirmed `TargetDefaultsSource.V1Endpoints` is effectively unreachable in current code.
- 2026-03-07: Confirmed `_showLegacyScopeExplorer` is initialized to `false` and never enabled later.
- 2026-03-07: Confirmed large `ReviewScopeStepView.xaml` sections remain behind `ShowLegacyImportScopeExplorer`, making them dead UI rather than active fallback UI.
- 2026-03-07: Created this branch and scratchpad to track the deprecation work.
- 2026-03-07: Removed the dead v1 target-default endpoint branch from `TryFetchTargetDefaultsAsync(...)`.
- 2026-03-07: Removed unreachable target-default helpers and session flags used only by the dead v1 branch.
- 2026-03-07: Removed `_showLegacyScopeExplorer`, `ShowLegacyScopeExplorer`, and `ShowLegacyImportScopeExplorer` from `MainViewModel`.
- 2026-03-07: Removed the permanently-hidden legacy folder tree, splitter, and file grid from `ReviewScopeStepView.xaml`.
- 2026-03-07: Removed leftover hidden file-selection controls and code-behind handlers that depended on the deleted legacy grid.
- 2026-03-08: Branch scope expanded beyond the original deprecation thread to close export-mode UX and correctness gaps discovered during live review.
- 2026-03-08: Added export workspace coverage-assurance notes and updated export design wording to reflect current retained-artifact and confidence behavior.
- 2026-03-08: Changed export scope traversal to surface child-enumeration failures as explicit preflight issues instead of silently dropping branches.
- 2026-03-08: Marked export preflight incomplete when traversal coverage fails and blocked `Run Export` on that incomplete state.
- 2026-03-08: Fixed export preflight warning visibility so warning counts now appear in the actual preflight issues grid rather than in a disconnected panel.
- 2026-03-08: Fixed the follow-up WPF crash caused by the temporary warning-panel converter reference.

## Removed In This Branch
- `TargetDefaultsSource.V1Endpoints`
- `_defaultsEndpointFamilyUnavailableForSession`
- `_defaultsEndpointFamilySkipLogged`
- `BuildDefaultEndpointCandidates(...)`
- `IsClientError400Or404(...)`
- `ParseEffectiveDefaults(...)`
- `AddDefaultsFromNode(...)`
- `ResolveAttribute(...)`
- `_showLegacyScopeExplorer`
- `ShowLegacyScopeExplorer`
- `ShowLegacyImportScopeExplorer`
- Legacy review-step folder tree/file grid UI that was permanently hidden

## Recommended Change Scope
- Remove `BuildDefaultEndpointCandidates(...)` and the unreachable `TargetDefaultsSource.V1Endpoints` branch if no supported v1 endpoint family is intended to return.
- Simplify the target-default resolution flow so it explicitly uses:
  - workspace lookup context
  - v2 container info
  - empty defaults
- Remove `_showLegacyScopeExplorer`, `ShowLegacyScopeExplorer`, and `ShowLegacyImportScopeExplorer` if no product decision exists to re-enable them.
- Remove the dead legacy explorer sections from `ReviewScopeStepView.xaml`.
- Keep the active direct upload and export review surfaces intact.

## Risks
- The target-default resolution code is sensitive and should retain existing fallback behavior after dead-path removal.
- The review screen XAML mixes active and dead UI in one file, so cleanup should be done carefully to avoid removing export or direct-upload sections.
- This branch now mixes the original deprecation cleanup with export-mode improvements, so merge reviewers should assess it as a widened branch rather than a narrow dead-code removal.

## Verification Plan
- `dotnet build NetDocsImporter.sln -nologo`
- Run targeted tests around NetDocuments target browsing and target default resolution.
- Smoke-check the review step in import mode and export mode.

## Recommended Next Steps
- Build and run targeted tests to confirm the active target-default fallbacks still behave correctly.
- Smoke-check the review step in import mode and export mode to confirm layout remains intact after legacy UI removal.
- If the team still wants historical context, copy a brief note about the removed legacy explorer into a changelog or ADR rather than retaining dormant code.

## Current Merge Readiness
- Current state has been reflected into `main`; treat this note as historical context for the deprecation and export-preflight cleanup thread.
- The branch should now be described as:
  - deprecation of dead legacy defaults and scope-explorer paths
  - export preflight coverage surfacing and run blocking for incomplete traversal
  - export preflight warning visibility cleanup
- Remaining known runtime note:
  - a tenant/API-side `500 Internal Server Error` still occurs for document `3459-7537-1065` during `GET /v1/Document/...`; this is not caused by the preflight changes in this branch.
