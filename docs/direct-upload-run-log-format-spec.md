# Direct Upload Run Log Format

## File output
- Location: `completed-jobs/directupload-<jobId>-<timestamp>-runlog.txt`
- Encoding: UTF-8 (no BOM)
- Retention: 30 days (auto-pruned by app)

## Structure
1. ASCII header block with run metadata.
2. File outcome section:
   - one logical entry per file
   - status (`OK` or `FAIL`)
   - http status and message
3. Planned folder mutation section:
   - folder paths planned/created for the run
4. Preflight issue section:
   - severity, code, message, relative path

## Export from UI
- Step 3 (`Recent jobs`) auto-refreshes and surfaces the latest persisted run summary per job.
- Logs are persisted in `completed-jobs` and retained for 30 days.

## Example status summary fields
- Requested
- Planned
- Uploaded
- Failed
- Skipped
- Resumed
- CreatedFolders

## ND-DIRECT trace fields
- `ND-DIRECT upload request ... profileKeys={n} customAttributeCount={n} profileFallbackMap={bool}` is emitted before each direct upload request.
- `profileFallbackMap` indicates whether profile serialization fell back to a raw key/value map instead of NetDocuments `customAttributes`.
- `profileFallbackMap=False` means fallback is not used.
- Interpret it with related fields:
  - `profileKeys > 0` and `customAttributeCount > 0`: profile values were sent as `customAttributes` (normal path).
  - `profileKeys > 0` and `customAttributeCount = 0` with `profileFallbackMap=True`: fallback map serialization was used because no numeric attribute IDs were resolved from profile keys.
  - `profileKeys = 0`: no profile payload was sent for that file.
