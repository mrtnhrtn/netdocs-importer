# AGENTS.md

## Git Safety Rules (Mandatory)

- Treat `main` as protected; never implement work on `main`.
- Before any edit, commit, pull, or push, run `git branch --show-current`.
- If current branch is `main`, switch to a feature branch named `feature/<task-slug>` from `origin/main`.
- Never commit on `main`.
- Never push `main`.
- Pull only on non-`main` branches and only when the working tree is clean.
- Push only when the user explicitly requests a push.
- If pull preconditions fail (dirty tree, detached HEAD, missing upstream), stop and report the issue.

## Canonical Git Command Flow For Codex

1. `git fetch origin --prune`
2. `git branch --show-current`
3. If on `main`:
   - If branch exists locally: `git switch feature/<task-slug>`
   - Else if exists remotely: `git switch -c feature/<task-slug> --track origin/feature/<task-slug>`
   - Else: `git switch -c feature/<task-slug> origin/main`
4. Clean check:
   - `git diff --quiet`
   - `git diff --cached --quiet`
5. Pull with rebase on current branch:
   - `git pull --rebase origin <current-branch>`
6. Push only on explicit user request:
   - First push: `git push -u origin <current-branch>`
   - Later pushes: `git push origin <current-branch>`

## Safety Scripts

- `scripts/git-safe-start.ps1 -Task <task>`
- `scripts/git-safe-pull.ps1`
- `scripts/git-safe-push.ps1 [-SetUpstream]`

Use these wrappers to enforce the same branch and precondition checks consistently.
