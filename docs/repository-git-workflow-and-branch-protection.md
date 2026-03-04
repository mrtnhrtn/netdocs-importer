# Git Workflow and Branch Protection

This repository enforces a feature-branch-only workflow.

## Policy Summary

- `main` is protected and not used for implementation work.
- Commits, pulls, and pushes on `main` are blocked locally by hooks.
- Pushes to `main` are blocked remotely by GitHub branch protection.
- Codex and humans use `feature/<task>` branches.
- Pulls use rebase.
- Pushes happen only when explicitly requested.

## Day 1 Setup

Run once per clone:

```powershell
git config core.hooksPath .githooks
```

Optional on POSIX-like environments (Git Bash/WSL/Linux/macOS):

```bash
chmod +x .githooks/pre-commit .githooks/pre-push .githooks/pre-rebase
```

Validate hook path:

```powershell
git config --get core.hooksPath
```

Expected output:

```text
.githooks
```

## Codex Command Flow

1. `git fetch origin --prune`
2. `git branch --show-current`
3. If branch is `main`, run:
   - Existing local: `git switch feature/<task-slug>`
   - Existing remote only: `git switch -c feature/<task-slug> --track origin/feature/<task-slug>`
   - New branch: `git switch -c feature/<task-slug> origin/main`
4. Clean checks:
   - `git diff --quiet`
   - `git diff --cached --quiet`
5. Pull:
   - `git pull --rebase origin <current-branch>`
6. Push only on explicit request:
   - First push: `git push -u origin <current-branch>`
   - Later pushes: `git push origin <current-branch>`

## Scripted Workflow

Use the helper scripts for consistent behavior:

- Start safely on feature branch:
  - `powershell -File .\scripts\git-safe-start.ps1 -Task "short task description"`
- Pull safely with policy checks:
  - `powershell -File .\scripts\git-safe-pull.ps1`
- Push safely (only on explicit request):
  - First push: `powershell -File .\scripts\git-safe-push.ps1 -SetUpstream`
  - Later push: `powershell -File .\scripts\git-safe-push.ps1`

## Human CLI Workflow (Equivalent)

```powershell
git fetch origin --prune
git branch --show-current
git switch -c feature/<task-slug> origin/main   # only if currently on main
git diff --quiet
git diff --cached --quiet
git pull --rebase origin <current-branch>
```

Push only when you intentionally publish:

```powershell
git push -u origin <current-branch>
```

## Troubleshooting

`git-safe-pull.ps1` says the tree is dirty:
- Commit or stash tracked changes first.
- Re-run pull after tree is clean.

Missing upstream for branch:
- Set upstream:
  - `git push -u origin <current-branch>`
- Then run `git-safe-pull.ps1` again.

Push rejected to `main`:
- Expected behavior by local hooks and remote protection.
- Push to your feature branch and open a PR into `main`.

Detached HEAD:
- Switch back to a named feature branch:
  - `git switch feature/<task-slug>`

## GitHub Branch Protection (Remote Enforcement)

Configure repository settings for branch `main`:

- Require a pull request before merging
- Require at least 1 approval
- Require status checks to pass before merge
- Require conversation resolution before merge
- Disable force pushes
- Disable branch deletion
- Include administrators in restrictions

Automated option (requires `GITHUB_TOKEN` with repository admin permission):

```powershell
powershell -File .\scripts\set-github-main-protection.ps1 -Owner "mrtnhrtn" -Repository "netdocs-importer" -Branch "main" -RequiredStatusCheck "build"
```

After applying:

```powershell
Invoke-RestMethod -Uri "https://api.github.com/repos/mrtnhrtn/netdocs-importer/branches/main" -Headers @{ "User-Agent" = "Codex" } | Select-Object name, protected
```
