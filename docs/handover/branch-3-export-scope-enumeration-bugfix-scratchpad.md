# Branch Scratchpad: 3 Export Scope Enumeration Bugfix

## Branch
- Proposed branch: `codex/fix-export-scope-enumeration-failures`
- Purpose: stop export preflight from silently skipping branches when child container enumeration fails.

## Finding Being Addressed
- Finding 3: export scope discovery can silently produce incomplete plans because traversal failures are swallowed.

## Action Log
- 2026-03-07: Reviewed `EnumerateExportScopesAsync(...)` and confirmed exceptions from `GetContainerChildrenAsync(...)` are swallowed with no surfaced issue.
- 2026-03-07: Confirmed the caller treats the resulting scope set as authoritative for export preflight counts and plan generation.
- 2026-03-07: Created this scratchpad for the later bugfix branch.

## Recommended Bugfix Shape
- Return traversal failures to the caller instead of silently dropping them.
- Surface the failures as export preflight issues with enough context to identify the skipped container.
- Mark the preflight result as partial or failed when any scope traversal errors occur.
- Decide explicitly whether run export should be blocked on partial preflight or allowed with strong warning text.

## Risks
- Changing the return contract of scope enumeration may ripple into export preflight code and tests.
- Some tenants may have intermittent endpoint failures; the UI should distinguish transient failures from unsupported endpoints where possible.

## Verification Plan
- Add tests proving child enumeration failures are surfaced rather than swallowed.
- Build solution and run export-specific tests.

## Recommended Next Steps
- Design the smallest contract change that preserves current callers.
- Prefer explicit partial-result reporting over more silent fallback logic.

