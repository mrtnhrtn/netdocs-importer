[CmdletBinding()]
param(
    [string]$Remote = "origin",
    [string]$Branch,
    [switch]$SetUpstream
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

$insideWorkTree = (& git rev-parse --is-inside-work-tree 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne "true") {
    throw "Current directory is not a Git working tree."
}

$currentBranch = Get-CurrentBranch
if ([string]::IsNullOrWhiteSpace($currentBranch)) {
    throw "Detached HEAD is not allowed for push workflow."
}
if ($currentBranch -eq "main") {
    throw "Push from 'main' is blocked by policy. Switch to a feature branch first."
}

$targetBranch = if ([string]::IsNullOrWhiteSpace($Branch)) { $currentBranch } else { $Branch }
if ($targetBranch -eq "main") {
    throw "Push target 'main' is blocked by policy."
}

if ($SetUpstream) {
    Invoke-Git push -u $Remote $targetBranch
    Write-Host "Pushed '$targetBranch' to '$Remote' with upstream tracking."
    return
}

Invoke-Git push $Remote $targetBranch
Write-Host "Pushed '$targetBranch' to '$Remote'."
