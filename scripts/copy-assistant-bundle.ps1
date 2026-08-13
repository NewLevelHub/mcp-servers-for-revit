<#
.SYNOPSIS
    Copies .cursor/rules into assistant-bundle for the in-Revit Cursor agent.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$BundleDir
)

$ErrorActionPreference = "Stop"

if (-not $BundleDir) {
    $BundleDir = Join-Path $RepoRoot "assistant-bundle"
}

$rulesSrc = Join-Path $RepoRoot ".cursor\rules"
$rulesDst = Join-Path $BundleDir ".cursor\rules"

New-Item -ItemType Directory -Force -Path $rulesDst | Out-Null

if (-not (Test-Path $rulesSrc)) {
    Write-Error "Rules folder not found: $rulesSrc"
}

Copy-Item -Path (Join-Path $rulesSrc "*.mdc") -Destination $rulesDst -Force
$agents = Join-Path $RepoRoot "AGENTS.md"
if (Test-Path $agents) {
    Copy-Item -Path $agents -Destination $BundleDir -Force
}

Write-Host "assistant-bundle ready: $BundleDir" -ForegroundColor Green
