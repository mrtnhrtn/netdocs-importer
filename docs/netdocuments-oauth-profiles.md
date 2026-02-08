# NetDocuments OAuth Profile Provisioning

Use this runbook to provision OAuth client settings without exposing secrets in app UI or source control.

End users only select region and click Connect. They do not see client IDs, client secrets, or redirect URIs.

## Distribution vs Developer mode

- Default launch (distribution mode):
  - no OAuth bootstrap UI is shown.
  - Connect relies on provisioned profiles only.
- Developer mode launch:
  - start app with `/dev` or `--dev`.
  - enables local bootstrap panel for test-only profile entry.
  - never use this mode as end-user distribution workflow.

## Runtime behavior

- Provisioned profile path: `%ProgramData%\NetDocsImporter\oauth-profiles.dat`
- Runtime profile resolution order:
1. ProgramData provisioned profile
2. Per-user fallback profile (legacy compatibility)
3. Missing profile => Connect disabled and admin message shown

## Step 1: Register OAuth app(s) in NetDocuments

- Register one app per region/environment where practical.
- Redirect URI must match exactly, for example:
  - `http://localhost:8400/callback`
- Ensure authorization code + refresh token flow is enabled per tenant policy.

## Step 2: Store secrets in your secure vault

- Keep client IDs/secrets in your enterprise secret manager.
- Do not store secrets in repo, tickets, chat, or plaintext app settings.

## Step 3: Build provisioning JSON on admin machine

Create a temporary file, e.g. `oauth-profiles.json`.

```json
{
  "AU": {
    "region": "AU",
    "clientId": "YOUR_AU_CLIENT_ID",
    "clientSecret": "YOUR_AU_CLIENT_SECRET",
    "redirectUri": "http://localhost:8400/callback",
    "apiBaseUrl": "https://api.au.netdocuments.com",
    "oauthAuthorizeBaseUrl": "https://api.au.netdocuments.com/neWeb2/OAuth.aspx",
    "oauthTokenUrl": "https://api.au.netdocuments.com/v1/OAuth"
  },
  "CAN": {
    "region": "CAN",
    "clientId": "YOUR_CAN_CLIENT_ID",
    "clientSecret": "YOUR_CAN_CLIENT_SECRET",
    "redirectUri": "http://localhost:8400/callback",
    "apiBaseUrl": "https://api.can.netdocuments.com",
    "oauthAuthorizeBaseUrl": "https://api.can.netdocuments.com/neWeb2/OAuth.aspx",
    "oauthTokenUrl": "https://api.can.netdocuments.com/v1/OAuth"
  }
}
```

## Step 4: Encrypt and provision machine-wide blob

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Set-NetDocumentsOAuthProfiles.ps1 -InputJsonPath .\oauth-profiles.json
```

- Default output: `%ProgramData%\NetDocsImporter\oauth-profiles.dat`
- Default scope: DPAPI `LocalMachine`

Dev/test only (user scope):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Set-NetDocumentsOAuthProfiles.ps1 -InputJsonPath .\oauth-profiles.json -Scope CurrentUser
```

## Step 5: Access control

The provisioning script applies ACL:
- `Administrators`: Full
- `Users`: Read

If your environment requires stricter ACLs, adjust after provisioning.

## Step 6: Remove plaintext traces

- Delete temporary `oauth-profiles.json`.
- Clear shell history for the provisioning session.
- Keep only vaulted source data.

## Step 7: Validate app behavior

1. Launch app as standard user.
2. Region with profile: Connect enabled.
3. Region without profile: Connect disabled with admin message.

## Step 8: Rotate secrets

1. Update secret in vault.
2. Regenerate encrypted blob with updated JSON.
3. Redeploy `%ProgramData%\NetDocsImporter\oauth-profiles.dat`.
4. Reconnect/refresh sessions as needed.

## Security posture

- This design hardens secret handling for desktop distribution.
- It does not eliminate all risk of secret extraction on compromised endpoints.
- For higher assurance, use a brokered/token-service architecture or public-client PKCE pattern where possible.
