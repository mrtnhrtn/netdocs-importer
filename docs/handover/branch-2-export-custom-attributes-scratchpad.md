# Branch Scratchpad: 2 Export Custom Attributes

## Branch
- Proposed branch: `codex/implement-export-custom-attributes`
- Purpose: either complete end-to-end export custom attribute support or remove the current misleading toggle.

## Finding Being Addressed
- Finding 2: `Include custom attributes` is exposed and persisted but does not affect the export pipeline.

## Action Log
- 2026-03-07: Confirmed the UI toggle is bound and persisted.
- 2026-03-07: Confirmed preflight always passes `customAttributeIds: null`.
- 2026-03-07: Confirmed the run path does not perform later enrichment for custom attributes.
- 2026-03-07: Created this scratchpad for the later feature branch.
- 2026-03-08: Confirmed the preflight now surfaces a visible warning when `Include custom attributes` is enabled, but export output is still not enriched end-to-end.

## Recommended Direction
- Preferred: implement the feature fully so the toggle becomes truthful.
- Fallback: remove or disable the toggle until attribute selection and extraction are ready.

## Implementation Questions
- Where should the selected custom attribute ids come from:
  - all synced custom attributes for the selected cabinet
  - a user-selected subset
  - a fixed preflight set derived from schema/profile usage
- Should metadata dumps include raw attribute ids, friendly names, or both.
- Should custom attributes be fetched during preflight only, run phase only, or both.

## Verification Plan
- Add tests proving selected custom attributes enter preflight items and final metadata output.
- Build solution and run export tests.

## Recommended Next Steps
- Decide product behavior first: all custom attributes vs selected subset.
- Avoid shipping a toggle that only warns but still does not affect export output after the branch is merged.

