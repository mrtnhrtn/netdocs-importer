# Flow Refactor (Phase 1)

## What Changed
- NetDocuments connect/setup moved to **Select Folder** step.
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
- Unsupported target types are blocked with:
  - `Only Workspace, Workspace Filter, or Folder are supported as upload destinations in this version.`
- Target selection now syncs profile attributes and inherited/default values.
- Inherited/default values are persisted as `EffectiveProfileDefaults` and reused by:
  - Review & Scope (read-only baseline display)
  - ndImport CSV export (default profile columns/values)

## Caching Behavior
- Target profile metadata is cached per session by `targetType:targetId`.
- Cache is invalidated when repository or cabinet changes.
- Saved settings persist selected target and serialized effective defaults.

## Validation Guard
- The target picker filters to supported types.
- Confirmation path still validates selected type before allowing progression.
