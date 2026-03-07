# Branch Scratchpad: 1 Export All Versions

## Branch
- Proposed branch: `codex/decide-export-all-versions-behavior`
- Purpose: resolve the gap between the `All versions` setting and the current no-op fallback path.

## Finding Being Addressed
- Finding 1: `All versions` is user-visible, but when `VersionsLite` is absent the fallback version enumeration path is intentionally disabled and silently exports only one file.

## Action Log
- 2026-03-07: Confirmed `EnumerateDocumentVersionsAsync(...)` is called by export preflight when `VersionHints` are absent.
- 2026-03-07: Confirmed `EnumerateDocumentVersionsAsync(...)` currently returns an empty list without making any HTTP calls.
- 2026-03-07: Confirmed tests currently lock in that no-op behavior.
- 2026-03-07: Confirmed `ParseDocumentVersions(...)` remains in the code but is unused.
- 2026-03-07: Created this scratchpad for the later design/feature branch.

## Recommendation
- Do not implement undocumented endpoint probing casually.
- Preferred product-safe behavior:
  - keep using `VersionsLite` when present
  - if version data is unavailable, surface a warning or partial capability state instead of silently pretending all versions were planned
  - consider disabling `All versions` for tenants or targets where version enumeration cannot be proven

## Why This Recommendation
- The current tests indicate previous caution around undocumented or unstable endpoints.
- Silent fallback to single-version export is misleading and breaks user expectation.
- A truthful degraded mode is better than speculative endpoint guessing in a production export pipeline.

## Decision Options
- Option A: make `All versions` conditional on reliable version data and warn otherwise.
- Option B: find and validate a supported endpoint for explicit version enumeration, then implement it with tests.
- Option C: remove the toggle until reliable enumeration exists.

## Verification Plan
- Update tests to match the selected product decision.
- Build solution and run export tests.

## Recommended Next Steps
- Decide whether the product accepts a capability-gated `All versions` mode.
- Only pursue endpoint implementation if a supported contract is available and verified.
