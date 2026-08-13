<#
.SYNOPSIS
    Builds Cursor in-Revit stack: assistant-bridge, MCP server, rules bundle; stages into Revit add-in.

.EXAMPLE
    .\scripts\build-assistant-cursor.ps1 -RevitVersion 2023
    .\scripts\build-assistant-cursor.ps1 -RevitVersion 2023 -StagingOnly
#>
[CmdletBinding()]
param(
    [string]$RevitVersion = "2023",
    [string]$RepoRoot,
    [switch]$StagingOnly
)

$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $RepoRoot = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { (Get-Location).Path }
}

function Test-NodeVersion {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) {
        Write-Error "Node.js not found. Install Node.js 22.13+ from https://nodejs.org/"
    }
    $ver = & node -v
    if ($ver -notmatch '^v(\d+)\.(\d+)') {
        Write-Error "Cannot parse node version: $ver"
    }
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    if ($major -lt 22 -or ($major -eq 22 -and $minor -lt 13)) {
        Write-Error "Node.js $ver found; Cursor SDK requires >= 22.13"
    }
    Write-Host "Node $ver" -ForegroundColor Gray
}

function Invoke-NpmBuild {
    param([string]$Dir, [string]$Label)
    Write-Host "Building $Label..." -ForegroundColor Cyan
    Push-Location $Dir

    # npm prints deprecation notices to stderr. With 2>&1 under EAP=Stop PowerShell turns the
    # first such line into a terminating error, so a successful install was reported to the
    # caller as "npm step failed" with a glob@10.5.0 warning as the reason. Judge npm by its
    # exit code; `throw` below is terminating regardless of this preference.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        # npm ci wipes node_modules first. When a running MCP server holds
        # better-sqlite3's native .node binding open, that delete fails with EPERM
        # half-way through and leaves packages without their manifests - which only
        # surfaces later, as a dead MCP server inside Revit. Reserve ci for a clean
        # tree; refresh an existing one in place, where a locked file is harmless.
        if ((Test-Path "package-lock.json") -and -not (Test-Path "node_modules")) {
            npm ci 2>&1 | Out-Host
        }
        else {
            npm install 2>&1 | Out-Host
        }
        if ($LASTEXITCODE -ne 0) { throw "npm install failed in $Dir" }
        npm run build 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed in $Dir" }
        npm prune --omit=dev 2>&1 | Out-Host
    }
    finally {
        $ErrorActionPreference = $prevEap
        Pop-Location
    }
}

<#
.SYNOPSIS
    Deploys the norm library the norm checkers need at runtime.
.DESCRIPTION
    npm run build produces neither of these, so a plain deploy leaves the MCP server
    with an empty rules database and no PDFs. Every norm audit then honestly reports
    "no numeric rules in the library" - which reads to an architect as "no violations"
    on a model that has them (seen 2026-08-13: 4 real violations reported as 0).

    resolveNormativesDir() walks three levels up from build/normatives, so the add-in
    root is exactly where it looks for the PDF folder - no code change needed.
#>
function Deploy-NormLibrary {
    param([string]$TargetRoot, [string]$DestServer)

    $seedDb = Join-Path $serverDir "revit-data.db"
    if (Test-Path $seedDb) {
        $destDb = Join-Path $DestServer "revit-data.db"
        if (Test-Path $destDb) {
            # Never discard rules the user saved through save_norm_rule.
            Copy-Item $destDb "$destDb.bak" -Force
        }
        Copy-Item $seedDb $destDb -Force
        $mb = [math]::Round((Get-Item $seedDb).Length / 1MB, 1)
        Write-Host "Norm rules database deployed ($mb MB; previous kept as revit-data.db.bak)" -ForegroundColor Cyan
    }
    else {
        Write-Warning "server\revit-data.db not found - the norm library will be empty and audits will report 0 violations."
    }

    $normSrc = Join-Path $RepoRoot "normatives"
    if (Test-Path $normSrc) {
        $normDest = Join-Path $TargetRoot "normatives"
        $pdfCount = (Get-ChildItem $normSrc -Filter *.pdf -File -ErrorAction SilentlyContinue | Measure-Object).Count
        Write-Host "Deploying normatives ($pdfCount PDF) -> $normDest" -ForegroundColor Cyan
        if (Test-Path $normDest) { Remove-Item $normDest -Recurse -Force }
        Copy-Item $normSrc $normDest -Recurse -Force
    }
    else {
        Write-Warning "normatives\ not found - PDF-backed norm checks will be skipped."
    }
}

<#
.SYNOPSIS
    Fails loudly when node_modules is half-installed.
.DESCRIPTION
    npm ci deletes node_modules before installing, so a flaky install (this repo lives
    on a Parallels share) can leave a package present but without its own package.json.
    Node then ignores that package's exports map, resolves the subpath literally and
    dies with ERR_MODULE_NOT_FOUND - at MCP server startup, where nobody reads stderr.
    Seen 2026-08-13: @modelcontextprotocol/sdk shipped without package.json, the server
    never started, and the in-Revit chat answered "нет связи с моделью Revit".
#>
function Assert-NodeModulesIntact {
    param([string]$Dir, [string]$Label)

    $pkg = Get-Content (Join-Path $Dir "package.json") -Raw | ConvertFrom-Json
    $deps = @()
    if ($pkg.dependencies) { $deps = $pkg.dependencies.PSObject.Properties.Name }

    $broken = @()
    foreach ($dep in $deps) {
        if (-not (Test-Path (Join-Path $Dir "node_modules\$dep\package.json"))) {
            $broken += $dep
        }
    }

    if ($broken.Count -gt 0) {
        throw ("{0}: node_modules is incomplete - no package.json in: {1}. Usually an npm step that hit EPERM because a running MCP server (Revit, Cursor or Claude Code) holds better_sqlite3.node open. Close them, run 'npm install' in {2}, and retry." -f $Label, ($broken -join ', '), $Dir)
    }

    Write-Host "$Label dependencies OK ($($deps.Count) packages)" -ForegroundColor Gray
}

function Copy-Tree {
    param(
        [string]$Source,
        [string]$Destination,
        [string[]]$Include = @("*")
    )
    if (-not (Test-Path $Source)) {
        Write-Error "Missing: $Source"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($pattern in $Include) {
        Get-ChildItem -Path $Source -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object {
                $rel = $_.FullName.Substring($Source.Length).TrimStart('\', '/')
                $target = Join-Path $Destination $rel
                $parent = Split-Path -Parent $target
                if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
                Copy-Item -Path $_.FullName -Destination $target -Force
            }
    }
}

Test-NodeVersion

& (Join-Path $PSScriptRoot "copy-assistant-bundle.ps1") -RepoRoot $RepoRoot

$bridgeDir = Join-Path $RepoRoot "assistant-bridge"
$serverDir = Join-Path $RepoRoot "server"
$bundleDir = Join-Path $RepoRoot "assistant-bundle"

Invoke-NpmBuild -Dir $bridgeDir -Label "assistant-bridge"

$serverJs = Join-Path $serverDir "build\index.js"
try {
    Invoke-NpmBuild -Dir $serverDir -Label "MCP server"
}
catch {
    if (Test-Path $serverJs) {
        Write-Warning "MCP server npm step failed, using existing build: $serverJs"
        Write-Warning $_.Exception.Message
    }
    else {
        throw
    }
}

if (-not (Test-Path (Join-Path $bridgeDir "dist\index.js"))) {
    Write-Error "assistant-bridge/dist/index.js not found after build"
}
if (-not (Test-Path (Join-Path $serverDir "build\index.js"))) {
    Write-Error "server/build/index.js not found after build"
}

# Deliberately outside the "use existing build" fallback above: a compiled build/ next
# to a gutted node_modules still deploys, and the failure only shows up in Revit.
Assert-NodeModulesIntact -Dir $bridgeDir -Label "assistant-bridge"
Assert-NodeModulesIntact -Dir $serverDir -Label "MCP server"

if ($StagingOnly) {
    $stage = Join-Path $RepoRoot "staging\assistant-cursor"
    Write-Host "Staging to $stage" -ForegroundColor Cyan
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    $destBridge = Join-Path $stage "assistant-bridge"
    $destServer = Join-Path $stage "mcp-server"
    $destBundle = Join-Path $stage "assistant-bundle"

    New-Item -ItemType Directory -Force -Path $destBridge | Out-Null
    Copy-Item (Join-Path $bridgeDir "dist") (Join-Path $destBridge "dist") -Recurse -Force
    Copy-Item (Join-Path $bridgeDir "node_modules") (Join-Path $destBridge "node_modules") -Recurse -Force
    Copy-Item (Join-Path $bridgeDir "package.json") $destBridge -Force

    New-Item -ItemType Directory -Force -Path $destServer | Out-Null
    Copy-Item (Join-Path $serverDir "build") (Join-Path $destServer "build") -Recurse -Force
    Copy-Item (Join-Path $serverDir "node_modules") (Join-Path $destServer "node_modules") -Recurse -Force
    Copy-Item (Join-Path $serverDir "package.json") $destServer -Force
    Deploy-NormLibrary -TargetRoot $stage -DestServer $destServer

    Copy-Item $bundleDir $destBundle -Recurse -Force
    Write-Host "Staged under $stage" -ForegroundColor Green
    return
}

$addinRoot = Join-Path $env:AppData "Autodesk\Revit\Addins\$RevitVersion\revit_mcp_plugin"
if (-not (Test-Path $addinRoot)) {
    Write-Warning "Add-in folder not found: $addinRoot - build the plugin first (deploy-local.ps1)."
    $addinRoot = Join-Path $RepoRoot "plugin\bin\AddIn $RevitVersion Debug R$($RevitVersion.Substring(2))\revit_mcp_plugin"
    if (-not (Test-Path $addinRoot)) {
        Write-Error "Cannot find plugin output folder. Run .\scripts\deploy-local.ps1 -RevitVersion $RevitVersion first."
    }
    Write-Host "Using plugin output: $addinRoot" -ForegroundColor Yellow
}

function Deploy-AssistantPayload {
    param([string]$TargetRoot)
    $destBridge = Join-Path $TargetRoot "assistant-bridge"
    $destServer = Join-Path $TargetRoot "mcp-server"
    $destBundle = Join-Path $TargetRoot "assistant-bundle"

    Write-Host "Deploying assistant-bridge -> $destBridge" -ForegroundColor Cyan
    if (Test-Path $destBridge) { Remove-Item $destBridge -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $destBridge | Out-Null
    Copy-Item (Join-Path $bridgeDir "dist") (Join-Path $destBridge "dist") -Recurse -Force
    Copy-Item (Join-Path $bridgeDir "node_modules") (Join-Path $destBridge "node_modules") -Recurse -Force
    Copy-Item (Join-Path $bridgeDir "package.json") $destBridge -Force

    Write-Host "Deploying mcp-server -> $destServer" -ForegroundColor Cyan
    if (Test-Path $destServer) { Remove-Item $destServer -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $destServer | Out-Null
    Copy-Item (Join-Path $serverDir "build") (Join-Path $destServer "build") -Recurse -Force
    Copy-Item (Join-Path $serverDir "node_modules") (Join-Path $destServer "node_modules") -Recurse -Force
    Copy-Item (Join-Path $serverDir "package.json") $destServer -Force
    Deploy-NormLibrary -TargetRoot $TargetRoot -DestServer $destServer

    Write-Host "Deploying assistant-bundle -> $destBundle" -ForegroundColor Cyan
    if (Test-Path $destBundle) { Remove-Item $destBundle -Recurse -Force }
    Copy-Item $bundleDir $destBundle -Recurse -Force
}

Deploy-AssistantPayload -TargetRoot $addinRoot

Write-Host ""
Write-Host "Cursor stack installed." -ForegroundColor Green
Write-Host "Next:" -ForegroundColor Yellow
Write-Host "  1. Revit -> Settings -> Assistant -> mode Cursor, paste CURSOR_API_KEY"
Write-Host "  2. Open Server on ribbon, open AI-assistant pane"
Write-Host "  3. Smoke: get_current_view_info on a floor plan"
