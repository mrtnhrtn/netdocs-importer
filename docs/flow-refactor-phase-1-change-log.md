# Flow Refactor (Phase 1)

## What Changed
- NetDocuments connect/setup moved to **Settings** (gear icon) instead of the Select Folder step.
- Startup authentication gate added:
  - If no NetDocuments session can be restored, the app opens Settings and requires login before workflow steps are enabled.
- Distribution-first OAuth behavior:
  - End users do not configure OAuth client details in UI.
  - App resolves OAuth profiles from provisioned machine data.
  - Missing region profile disables Connect with admin guidance.
- Optional developer startup mode:
  - Launch with `/dev` or `--dev` to enable local dev-only OAuth bootstrap panel.
  - Dev bootstrap is for testing only and is not part of production distribution flow.
- Source folder scan now requires an active NetDocuments connection.
- Target destination selection now supports only:
  - `Workspace`
  - `Workspace Filter`
  - `Folder`
- Step 1 selector now includes an expandable tree for Recent, Favorites, and Go to Workspace results:
  - A cabinet-root node is always shown at the top of the tree and expands to top-level cabinet folders.
  - Expanding workspace/folder nodes lazy-loads child folders and workspace filters.
  - Recent and Favorite entries participate in the same expandable tree behavior as Go to Workspace entries.
  - Selecting a child folder/filter commits the same target contract used by Direct API upload.
- Unsupported target types are blocked with:
  - `Only Workspace, Workspace Filter, or Folder are supported as upload destinations in this version.`
- Target selection now syncs profile attributes and inherited/default values.
- Inherited/default values are persisted as `EffectiveProfileDefaults` and reused by:
  - Review & Scope (read-only baseline display)
  - ndImport CSV export (default profile columns/values)

## Caching Behavior
- Target profile metadata is cached per session by `targetType:targetId`.
- Cache is invalidated when repository or cabinet changes.
- Workspace child container expansion uses a 10-minute in-memory cache keyed by service + repository + cabinet + parent container id.
- Child-container cache is invalidated when target-browser context resets (for example repository/cabinet/region/auth changes).
- Browse expansion resolves container ids per context and caches them in-memory so Recents/Favorites rows can expand even when the row id differs from v2 container-search scope format.
- Saved settings persist selected target and serialized effective defaults.

## API Endpoint Strategy
- The target browser is now **v2-first** where endpoint capability is equivalent:
  - Child browsing/search: `v2/search` with cabinet-scoped container queries.
  - Container metadata/path: `v2/container/{id}/info` and `v2/container/{id}/ancestry`.
  - Cabinet root folders: `v2/cabinet/{id}/folders`.
- v1 endpoints remain as compatibility fallback only when a tenant does not provide equivalent v2 behavior.

## Validation Guard
- The target picker filters to supported types.
- Confirmation path still validates selected type before allowing progression.
