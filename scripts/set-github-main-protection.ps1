[CmdletBinding()]
param(
    [string]$Owner = "mrtnhrtn",
    [string]$Repository = "netdocs-importer",
    [string]$Branch = "main",
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$RequiredStatusCheck = "build"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "GitHub token is required. Set GITHUB_TOKEN with repo admin permission."
}

$url = "https://api.github.com/repos/$Owner/$Repository/branches/$Branch/protection"

$headers = @{
    "Accept" = "application/vnd.github+json"
    "Authorization" = "Bearer $Token"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent" = "netdocs-importer-git-governance"
}

$payload = @{
    required_status_checks = @{
        strict = $true
        contexts = @($RequiredStatusCheck)
    }
    enforce_admins = $true
    required_pull_request_reviews = @{
        dismiss_stale_reviews = $false
        require_code_owner_reviews = $false
        required_approving_review_count = 1
        require_last_push_approval = $false
    }
    restrictions = $null
    required_conversation_resolution = $true
    allow_force_pushes = $false
    allow_deletions = $false
    block_creations = $false
    required_linear_history = $false
    lock_branch = $false
    allow_fork_syncing = $true
}

$body = $payload | ConvertTo-Json -Depth 10
$response = Invoke-RestMethod -Method Put -Uri $url -Headers $headers -Body $body -ContentType "application/json"

Write-Host ("Branch protection applied to '{0}/{1}:{2}'." -f $Owner, $Repository, $Branch)
Write-Host ("Protected: {0}" -f $response.protected)
