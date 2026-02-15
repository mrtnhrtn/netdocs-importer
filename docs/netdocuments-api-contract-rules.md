# NetDocuments API Contract Rules

This project must use only endpoint paths and query/body parameters documented in the NetDocuments REST API manual and v2 Swagger, with one approved exception: `indexpriority`.

## Hard Rules

1. Do not introduce undocumented query parameters or body fields.
2. Do not send known parameters to the wrong endpoint family.
3. For target-browser child expansion, surface `ndfld`, `ndflt`, and `ndcs` when available.
4. In child expansion results, only folder/collabspace nodes (`ndfld`, `ndcs`) may expand further.
5. `ndsq` is not implemented in this app and must not be treated as a supported browse/upload target.
6. Keep `indexpriority` support as-is; treat it as the only intentional undocumented exception.

## Code Review/Test Checklist

1. Verify every new NetDocuments path against REST manual/v2 Swagger.
2. Verify each query parameter is accepted by that exact endpoint.
3. Add or update tests for any endpoint-shape change before merge.
4. Reject changes that rely on guessed endpoint behavior.
