# Branch Scratchpad: 1 Export All Versions

## Branch
- Proposed branch: `codex/decide-export-all-versions-behavior`
- Purpose: finish the remaining truthfulness gap in `All versions` behavior now that version-aware export planning is partially wired.

## Finding Being Addressed
- Finding 1: `All versions` is user-visible and currently expands per-version export items when `VersionsLite` returns exact version ids, but when those hints are absent or incomplete the fallback enumeration path is intentionally disabled and preflight drops back to a single export item.

## Action Log
- 2026-03-07: Confirmed `EnumerateDocumentVersionsAsync(...)` is called by export preflight when `VersionHints` are absent.
- 2026-03-07: Confirmed `EnumerateDocumentVersionsAsync(...)` currently returns an empty list without making any HTTP calls.
- 2026-03-07: Confirmed tests currently lock in that no-op behavior.
- 2026-03-07: Confirmed `ParseDocumentVersions(...)` remains in the code but is unused.
- 2026-03-07: Created this scratchpad for the later design/feature branch.
- 2026-03-08: Confirmed export preflight already consumes `VersionsLite` arrays and emits separate export items per returned version id.
- 2026-03-08: Confirmed the current product gap is narrower than the original note: the unresolved case is incomplete or missing `VersionsLite` coverage, not the happy path where exact version ids are present.

## Recommendation
- Do not implement undocumented endpoint probing casually.
- Preferred product-safe behavior:
  - keep using `VersionsLite` when present
  - if exact version coverage is unavailable, surface a warning or blocking capability state instead of silently pretending all versions were planned
  - consider disabling `All versions` for tenants or targets where version enumeration cannot be proven

## Why This Recommendation
- The current tests indicate previous caution around undocumented or unstable endpoints.
- Silent fallback to a single export item is misleading and breaks user expectation for `All versions`.
- A truthful degraded mode is better than speculative endpoint guessing in a production export pipeline.

## Decision Options
- Option A: make `All versions` conditional on reliable version data and warn otherwise.
- Option B: find and validate a supported endpoint for explicit version enumeration, then implement it with tests.
- Option C: remove the toggle until reliable enumeration exists.

## Verification Plan
- Update tests to match the selected product decision.
- Build solution and run export tests.

## Recommended Next Steps
- Decide whether the product accepts a capability-gated or warning-blocked `All versions` mode when `VersionsLite` coverage is incomplete.
- Only pursue endpoint implementation if a supported contract is available and verified.
