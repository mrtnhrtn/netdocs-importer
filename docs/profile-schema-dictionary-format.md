# Profile Schema Format

The profile schema dictionary maps NetDocuments profile field codes (e.g. `1001`) and value codes (e.g. `2001`) to human-readable labels. The application uses this to resolve labels for profiling and to enrich ndImport exports.

The preferred format is JSON. A simple CSV format is also supported.

## JSON (preferred)

Single-cabinet schema:

```json
{
  "version": 1,
  "cabinet": "My Cabinet",
  "schemaVersion": "2024.1",
  "fields": [
    {
      "code": "1001",
      "name": "Document Type",
      "values": [
        { "code": "2001", "label": "Correspondence" },
        { "code": "2002", "label": "Agreement" }
      ]
    }
  ]
}
```

Multi-cabinet schema:

```json
{
  "version": 1,
  "cabinets": [
    {
      "cabinet": "Cabinet A",
      "schemaVersion": "2024.1",
      "fields": [
        { "code": "1001", "name": "Document Type", "values": [] }
      ]
    },
    {
      "cabinet": "Cabinet B",
      "schemaVersion": "2024.2",
      "fields": [
        { "code": "1001", "name": "Document Type", "values": [] }
      ]
    }
  ]
}
```

Notes:
- `code` can be a JSON string or number.
- The schema loader selects the cabinet matching the current ndImport cabinet name if possible.

## CSV (optional)

CSV headers:

```
FieldCode,FieldName,ValueCode,ValueLabel
```

Example:

```
1001,Document Type,2001,Correspondence
1001,Document Type,2002,Agreement
1002,Client,3001,Acme Corp
```
