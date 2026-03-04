# NetDocs Importer

Desktop WPF application for preparing and running NetDocuments uploads.

## Prerequisites

- Windows 10/11
- .NET SDK 10.0+
- Access to a NetDocuments tenant and OAuth profile provisioning

## Build

```powershell
dotnet build NetDocsImporter.sln -c Debug
```

## Run

```powershell
dotnet run --project .\src\NetDocsImporter.App\NetDocsImporter.App.csproj
```

Optional developer mode:

```powershell
dotnet run --project .\src\NetDocsImporter.App\NetDocsImporter.App.csproj -- --dev
```

## Test

```powershell
dotnet test .\tests\NetDocsImporter.Tests\NetDocsImporter.Tests.csproj -c Debug
```

## User Flow

1. **NetDocuments Upload Target**
   - Open **Settings** (gear icon) and connect to NetDocuments.
   - If no active NetDocuments session exists at startup, the app requires sign-in before workflow steps are enabled.
   - Select repository/cabinet.
   - Choose target from Recent/Favorites/Go To Workspace (auto-confirms selection).
   - Expand a workspace/folder in the tree panel to lazy-load child `ndfld`, `ndflt`, `ndsq`, and `ndcs` containers; only folder/collabspace nodes expand further.
2. **Local Folder**
   - Select local source folder and scan.
   - Review direct-upload preflight issues.
   - Refresh plan or run direct upload.
3. **Recent Jobs**
   - Review recent run summaries and report outputs.

## Logging and Reports

- App/system logs are written under `%LocalAppData%\NetDocsImporter\logs`.
- Trace log retention is pruned to 7 days during startup.
- Export and direct-upload reports are written under `%LocalAppData%\NetDocsImporter\reports`.
- Direct-upload run logs are written under `%LocalAppData%\NetDocsImporter\completed-jobs` and retained for 30 days.

## OAuth Profile Provisioning

See `docs/netdocuments-oauth-profile-provisioning-runbook.md` for machine-provisioned profile setup and security notes.

## Git Workflow

See `docs/repository-git-workflow-and-branch-protection.md` for branch protection, local hooks, and Codex-safe pull/push workflow.
