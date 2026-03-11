[CmdletBinding()]
param(
    [string]$Remote = "origin",
    [string]$Branch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Git {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Args
    )

    & git @Args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Args -join ' ') failed."
    }
}

function Get-CurrentBranch {
    $branch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to determine current branch."
    }
    return $branch
}

function Assert-CleanTrackedTree {
    & git -c core.safecrlf=false diff --quiet
    if ($LASTEXITCODE -eq 1) {
        throw "Working tree has unstaged changes. Commit or stash before pull --rebase."
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to evaluate unstaged changes."
    }

    & git -c core.safecrlf=false diff --cached --quiet
    if ($LASTEXITCODE -eq 1) {
        throw "Working tree has staged changes. Commit or unstage before pull --rebase."
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to evaluate staged changes."
    }
}

$insideWorkTree = (& git rev-parse --is-inside-work-tree 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne "true") {
    throw "Current directory is not a Git working tree."
}

$currentBranch = Get-CurrentBranch
if ([string]::IsNullOrWhiteSpace($currentBranch)) {
    throw "Detached HEAD is not allowed for pull workflow."
}
if ($currentBranch -eq "main") {
    throw "Pull on 'main' is blocked by policy. Switch to a feature branch first."
}

Assert-CleanTrackedTree

$pullRemote = $Remote
$pullBranch = $Branch

if ([string]::IsNullOrWhiteSpace($pullBranch)) {
    $upstream = (& git rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)) {
        throw "Missing upstream for '$currentBranch'. Set upstream or provide -Remote and -Branch."
    }

    $parts = $upstream.Split("/", 2)
    if ($parts.Count -ne 2) {
        throw "Invalid upstream format '$upstream'. Expected <remote>/<branch>."
    }

    $pullRemote = $parts[0]
    $pullBranch = $parts[1]
}

Invoke-Git pull --rebase $pullRemote $pullBranch
Write-Host "Rebased '$currentBranch' from '$pullRemote/$pullBranch'."
