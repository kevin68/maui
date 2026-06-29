#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
    Deterministic access gate for preview release-readiness data sources.

.DESCRIPTION
    When someone asks "is .NET MAUI preview N release-ready?", the *authoritative*
    answer to "which staged build is blessed as the official preview" comes from
    an internal '.NET Release Tracker' Copilot plugin that lives in a private
    marketplace repo. Public BAR/Maestro data can list candidate builds but cannot
    by itself identify the blessed one.

    This script does NOT fetch any release data. It only classifies the caller's
    environment so the agent can pick the right data source and the right tone:

        AVAILABLE_ENABLED      caller can read the marketplace repo AND the plugin
                               is already enabled locally  -> use the plugin.
        AVAILABLE_NOT_ENABLED  caller can read the marketplace repo but the plugin
                               is NOT enabled locally       -> offer an opt-in.
        NO_ACCESS              caller cannot confirm read access (no access, gh
                               missing, or gh unauthenticated) -> fall back to
                               PUBLIC data only, and say NOTHING about the private
                               plugin (privacy default).

    The result is printed as a single token line:

        RELEASE_TRACKER_STATUS=<token>

    followed by a '# ...' diagnostic line. The script ALWAYS exits 0 — it is a
    classifier, not a build gate.

.NOTES
    PUBLIC-SAFE CONTRACT (this file ships in the public dotnet/maui repo):
      * Contains NO embargoed/unshipped release data.
      * Contains NO Azure AD resource identifiers (GUIDs / api://... audiences).
      * Contains NO backend service hostnames or internal endpoint paths.
      * Performs NO fetch-and-exec of remote code.
    It only references the *marketplace pointer* (a repo name + a plugin name),
    which is the sanctioned reference. Actual data access is independently gated
    by the plugin's own Azure AD authentication, so a no-access caller cannot
    obtain embargoed data even if they read this script.

.PARAMETER ReleaseRepo
    The private marketplace repo that hosts the release-tracker plugin, in
    'owner/name' form. Default: dotnet/release.

.PARAMETER PluginId
    The plugin name as declared in its plugin.json. Default: dotnet-release-tracker.

.PARAMETER Json
    Emit a JSON object instead of the token line.

.EXAMPLE
    pwsh ./Get-PreviewReleaseReadiness.ps1
    RELEASE_TRACKER_STATUS=NO_ACCESS
    # access=false enabled=false reason=no-read-access-to-dotnet/release
#>
[CmdletBinding()]
param(
    [string]$ReleaseRepo = 'dotnet/release',
    [string]$PluginId = 'dotnet-release-tracker',
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-MarketplaceAccess {
    param([string]$Repo)

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) {
        return [pscustomobject]@{ Access = $false; Reason = 'gh-cli-not-installed' }
    }

    # --silent suppresses the repo JSON body; we only care about the exit code.
    # A 404 (no access) or auth failure both yield a non-zero exit -> no access.
    & gh api "repos/$Repo" --silent 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        return [pscustomobject]@{ Access = $true; Reason = "read-access-to-$Repo" }
    }
    return [pscustomobject]@{ Access = $false; Reason = "no-read-access-to-$Repo" }
}

function Test-PluginEnabled {
    param([string]$Plugin)

    # Scan the well-known Copilot settings locations. We use a tolerant regex
    # (settings files are JSONC and may contain // and /* */ comments) rather
    # than a strict JSON parse. An enabled entry looks like:
    #   "dotnet-release-tracker@<marketplace>": true
    $candidates = @()
    if ($env:HOME) { $candidates += (Join-Path $env:HOME '.copilot/settings.json') }
    if ($env:USERPROFILE) { $candidates += (Join-Path $env:USERPROFILE '.copilot/settings.json') }
    # Project-scope settings, if invoked from within a repo checkout.
    $candidates += (Join-Path (Get-Location) '.github/copilot/settings.json')

    $pattern = '"' + [regex]::Escape($Plugin) + '@[^"]+"\s*:\s*true'
    foreach ($path in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $path) {
            try {
                $text = Get-Content -LiteralPath $path -Raw -ErrorAction Stop
            } catch { continue }
            if ($text -match $pattern) {
                return [pscustomobject]@{ Enabled = $true; Source = $path }
            }
        }
    }
    return [pscustomobject]@{ Enabled = $false; Source = $null }
}

$access = Test-MarketplaceAccess -Repo $ReleaseRepo

if (-not $access.Access) {
    # Privacy default: anything other than confirmed access -> NO_ACCESS.
    $status = 'NO_ACCESS'
    $enabled = $false
    $reason = $access.Reason
} else {
    $pluginState = Test-PluginEnabled -Plugin $PluginId
    $enabled = $pluginState.Enabled
    if ($enabled) {
        $status = 'AVAILABLE_ENABLED'
        $reason = "enabled-via=$($pluginState.Source)"
    } else {
        $status = 'AVAILABLE_NOT_ENABLED'
        $reason = 'access-ok-plugin-not-enabled'
    }
}

if ($Json) {
    [pscustomobject]@{
        status  = $status
        access  = [bool]$access.Access
        enabled = [bool]$enabled
        reason  = $reason
        repo    = $ReleaseRepo
        plugin  = $PluginId
    } | ConvertTo-Json -Compress
} else {
    Write-Output "RELEASE_TRACKER_STATUS=$status"
    Write-Output "# access=$($access.Access.ToString().ToLower()) enabled=$($enabled.ToString().ToLower()) reason=$reason"
}

exit 0
