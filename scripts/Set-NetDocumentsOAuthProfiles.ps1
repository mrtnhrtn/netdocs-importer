<#
.SYNOPSIS
Creates an encrypted NetDocuments OAuth profile blob for NetDocsImporter.

.DESCRIPTION
Reads a region-keyed JSON file containing OAuth client configuration
(clientId/clientSecret/redirectUri/endpoints), encrypts it with DPAPI,
and writes the encrypted blob to the path used by NetDocsImporter.

By default:
- Output path: %ProgramData%\NetDocsImporter\oauth-profiles.dat
- DPAPI scope: LocalMachine

.PARAMETER InputJsonPath
Required. Path to a UTF-8 JSON file with region keys (for example AU, CAN, EU).
Each region entry should include:
- region
- clientId
- clientSecret
- redirectUri
- apiBaseUrl
- oauthAuthorizeBaseUrl
- oauthTokenUrl

.PARAMETER OutputPath
Optional. Destination path for the encrypted blob.
Default: %ProgramData%\NetDocsImporter\oauth-profiles.dat

.PARAMETER Scope
Optional. DPAPI scope.
- LocalMachine (default, recommended for shared machine deployment)
- CurrentUser (dev/test only)

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\scripts\Set-NetDocumentsOAuthProfiles.ps1 `
  -InputJsonPath .\oauth-profiles.json

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\scripts\Set-NetDocumentsOAuthProfiles.ps1 `
  -InputJsonPath .\oauth-profiles.json -Scope CurrentUser

.NOTES
- Do not commit plaintext JSON containing secrets.
- Store source secrets in a vault, generate JSON only on a trusted admin machine,
  and delete plaintext JSON after provisioning.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$InputJsonPath,

    [Parameter(Mandatory = $false)]
    [string]$OutputPath = (Join-Path $env:ProgramData "NetDocsImporter\oauth-profiles.dat"),

    [Parameter(Mandatory = $false)]
    [ValidateSet("LocalMachine", "CurrentUser")]
    [string]$Scope = "LocalMachine"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-DpapiTypesAvailable {
    try {
        $null = [System.Security.Cryptography.ProtectedData]::Protect([byte[]]::new(0), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return $true
    }
    catch {
        return $false
    }
}

function Try-LoadProtectedDataAssembly {
    try {
        Add-Type -AssemblyName "System.Security" -ErrorAction Stop
        if (Test-DpapiTypesAvailable) { return $true }
    }
    catch {
    }

    try {
        Add-Type -AssemblyName "System.Security.Cryptography.ProtectedData" -ErrorAction Stop
        if (Test-DpapiTypesAvailable) { return $true }
    }
    catch {
    }

    $candidatePatterns = @(
        (Join-Path $PSHOME "System.Security.Cryptography.ProtectedData.dll"),
        (Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.NETCore.App\*\System.Security.Cryptography.ProtectedData.dll"),
        (Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.WindowsDesktop.App\*\System.Security.Cryptography.ProtectedData.dll")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($pattern in $candidatePatterns) {
        $candidates = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue |
            Sort-Object -Property FullName -Descending
        foreach ($candidate in $candidates) {
            try {
                Add-Type -Path $candidate.FullName -ErrorAction Stop
                if (Test-DpapiTypesAvailable) { return $true }
            }
            catch {
            }
        }
    }

    return $false
}

try {
    if (-not (Test-DpapiTypesAvailable) -and -not (Try-LoadProtectedDataAssembly)) {
        throw "DPAPI unavailable"
    }
}
catch {
    throw "DPAPI types are unavailable in this PowerShell runtime. Run this script on Windows PowerShell 5.1 or a Windows PowerShell 7 runtime with System.Security.Cryptography.ProtectedData available."
}

if (-not (Test-Path -LiteralPath $InputJsonPath)) {
    throw "Input JSON file not found: $InputJsonPath"
}

$json = Get-Content -LiteralPath $InputJsonPath -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($json)) {
    throw "Input JSON is empty."
}

$null = $json | ConvertFrom-Json

$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
$dataProtectionScope = [System.Enum]::Parse([System.Security.Cryptography.DataProtectionScope], $Scope, $true)
$encrypted = [System.Security.Cryptography.ProtectedData]::Protect($bytes, $null, $dataProtectionScope)

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

[System.IO.File]::WriteAllBytes($OutputPath, $encrypted)

try {
    & icacls $OutputPath /inheritance:r /grant:r "BUILTIN\Administrators:(F)" "BUILTIN\Users:(R)" | Out-Null
}
catch {
    Write-Warning "Could not set ACL on '$OutputPath'. Run with elevated privileges if needed."
}

Write-Host "Provisioned NetDocuments OAuth profile blob to '$OutputPath' with DPAPI scope '$Scope'."
