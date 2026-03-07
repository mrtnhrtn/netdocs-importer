[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Task
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

function Test-LocalBranchExists {
    param([Parameter(Mandatory = $true)][string]$BranchName)
    & git show-ref --verify --quiet "refs/heads/$BranchName"
    return $LASTEXITCODE -eq 0
}

function Test-RemoteBranchExists {
    param([Parameter(Mandatory = $true)][string]$BranchName)
    & git show-ref --verify --quiet "refs/remotes/origin/$BranchName"
    return $LASTEXITCODE -eq 0
}

function Convert-ToTaskSlug {
    param([Parameter(Mandatory = $true)][string]$InputText)

    $slug = $InputText.ToLowerInvariant()
    $slug = [Regex]::Replace($slug, "[^a-z0-9]+", "-")
    $slug = [Regex]::Replace($slug, "-{2,}", "-")
    $slug = $slug.Trim("-")
    if ([string]::IsNullOrWhiteSpace($slug)) {
        throw "Task name produced an empty slug. Provide a task with letters or numbers."
    }
    return $slug
}

$insideWorkTree = (& git rev-parse --is-inside-work-tree 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne "true") {
    throw "Current directory is not a Git working tree."
}

Invoke-Git fetch origin --prune

$currentBranch = Get-CurrentBranch
if ([string]::IsNullOrWhiteSpace($currentBranch)) {
    throw "Detached HEAD is not allowed. Switch to or create a feature branch first."
}

if ($currentBranch -ne "main") {
    Write-Host "Already on '$currentBranch'. No branch switch required."
    return
}

$slug = Convert-ToTaskSlug -InputText $Task
$targetBranch = "feature/$slug"

if (Test-LocalBranchExists -BranchName $targetBranch) {
    Invoke-Git switch $targetBranch
    Write-Host "Switched to existing local branch '$targetBranch'."
    return
}

if (Test-RemoteBranchExists -BranchName $targetBranch) {
    Invoke-Git switch -c $targetBranch --track "origin/$targetBranch"
    Write-Host "Created local tracking branch '$targetBranch' from origin."
    return
}

Invoke-Git switch -c $targetBranch origin/main
Write-Host "Created and switched to '$targetBranch' from origin/main."
