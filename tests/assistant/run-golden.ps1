#Requires -Version 5.1
<#
.SYNOPSIS
  REV-111 golden set dry-run (no Revit).

.EXAMPLE
  .\run-golden.ps1
  .\run-golden.ps1 -Live   # needs ASSISTANT_API_KEY or OPENAI_API_KEY
#>
param(
    [switch]$Live
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $PSScriptRoot "RevitMCPPlugin.Assistant.Tests.csproj"))) {
    $root = Split-Path -Parent $PSScriptRoot
}

Push-Location $PSScriptRoot
try {
    if ($Live) {
        $env:GOLDEN_LIVE = "1"
        if (-not $env:ASSISTANT_API_KEY -and -not $env:OPENAI_API_KEY) {
            Write-Error "Set ASSISTANT_API_KEY or OPENAI_API_KEY for -Live"
        }
    }

    Write-Host "Running assistant golden set (dry-run, no Revit)..."
    dotnet test .\RevitMCPPlugin.Assistant.Tests.csproj -c Release --filter "FullyQualifiedName~GoldenSet"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "OK - see Golden/baseline.json targets; live reports under Golden/reports/ when -Live."
}
finally {
    Pop-Location
}
