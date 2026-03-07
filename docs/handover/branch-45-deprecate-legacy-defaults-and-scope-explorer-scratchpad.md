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

## Verification Plan
- `dotnet build NetDocsImporter.sln -nologo`
- Run targeted tests around NetDocuments target browsing and target default resolution.
- Smoke-check the review step in import mode and export mode.

## Recommended Next Steps
- Build and run targeted tests to confirm the active target-default fallbacks still behave correctly.
- Smoke-check the review step in import mode and export mode to confirm layout remains intact after legacy UI removal.
- If the team still wants historical context, copy a brief note about the removed legacy explorer into a changelog or ADR rather than retaining dormant code.
