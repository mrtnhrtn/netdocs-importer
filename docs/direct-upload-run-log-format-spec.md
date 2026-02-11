# Direct Upload Run Log Format

## File output
- Location: `reports/directupload-<jobId>-<timestamp>-runlog.txt`
- Encoding: UTF-8 (no BOM)
- Retention: indefinite (not auto-managed by app)

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
- Step 3 (`Recent jobs`) includes:
  - `Export Last Direct Upload Log`
- Export writes a copy to a user-selected location.

## Example status summary fields
- Requested
- Planned
- Uploaded
- Failed
- Skipped
- Resumed
- CreatedFolders
