<#
.SYNOPSIS
    Builds the command set and MCP server and installs them into the local Revit add-ins folder.

.DESCRIPTION
    The csproj already copies binaries into %AppData%\Autodesk\Revit\Addins on Debug builds, but
    the copy is marked ContinueOnError - so when Revit is open it holds the DLL and the copy is
    skipped without a word, leaving you testing yesterday's plugin. This script refuses to run
    while Revit is open, then reports what it wrote.

.EXAMPLE
    .\scripts\deploy-local.ps1 -RevitVersion 2023
#>
[CmdletBinding()]
param(
    [string]$RevitVersion = "2023",
    [switch]$SkipServer
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$revit = Get-Process Revit -ErrorAction SilentlyContinue
if ($revit) {
    Write-Error "Revit is running (PID $($revit.Id -join ', ')). Close it first - the plugin DLL is locked and would be silently skipped."
}

$configuration = "Debug R$($RevitVersion.Substring(2))"
Write-Host "Building plugin ($configuration)..." -ForegroundColor Cyan
dotnet build (Join-Path $repo "plugin\RevitMCPPlugin.csproj") -c $configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Plugin build failed." }

Write-Host "Building command set ($configuration)..." -ForegroundColor Cyan
dotnet build (Join-Path $repo "commandset\RevitMCPCommandSet.csproj") -c $configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Command set build failed." }

$target = Join-Path $env:AppData "Autodesk\Revit\Addins\$RevitVersion\revit_mcp_plugin\Commands\RevitMCPCommandSet\$RevitVersion"
$dll = Join-Path $target "RevitMCPCommandSet.dll"
if (-not (Test-Path $dll)) {
    Write-Error "Expected $dll after the build, but it is not there - check the copy step in the csproj."
}

$age = (Get-Date) - (Get-Item $dll).LastWriteTime
if ($age.TotalMinutes -gt 5) {
    Write-Error "$dll is $([int]$age.TotalMinutes) min old - the build did not replace it. Deploy aborted."
}
Write-Host "Installed $dll ($((Get-Item $dll).LastWriteTime))" -ForegroundColor Green

# commandRegistry.json is what the plugin actually loads, and PathManager only creates it
# when it is missing - nothing ever refreshes it. A new command therefore ships in the DLL
# and in command.json but stays invisible ("Method not found") until this merge runs.
$commandsDir = Split-Path -Parent (Split-Path -Parent $target)
$registryPath = Join-Path $commandsDir "commandRegistry.json"
$commandJson = Join-Path $repo "command.json"

if ((Test-Path $registryPath) -and (Test-Path $commandJson)) {
    $registry = Get-Content $registryPath -Raw | ConvertFrom-Json
    $declared = (Get-Content $commandJson -Raw | ConvertFrom-Json).commands
    $known = @($registry.commands | ForEach-Object { $_.commandName })

    $added = @()
    foreach ($cmd in $declared) {
        if ($known -contains $cmd.commandName) { continue }
        $registry.commands += [pscustomobject]@{
            commandName            = $cmd.commandName
            assemblyPath           = "RevitMCPCommandSet\{VERSION}\RevitMCPCommandSet.dll"
            enabled                = $true
            supportedRevitVersions = @($RevitVersion)
            developer              = [pscustomobject]@{
                name         = "mcp-servers-for-revit"
                email        = ""
                website      = ""
                organization = "mcp-servers-for-revit"
            }
            description            = $cmd.description
        }
        $added += $cmd.commandName
    }

    if ($added.Count -gt 0) {
        $registry | ConvertTo-Json -Depth 10 | Set-Content $registryPath -Encoding utf8
        Write-Host "Registered new commands: $($added -join ', ')" -ForegroundColor Green
    }
    else {
        Write-Host "commandRegistry.json already lists every command in command.json." -ForegroundColor Gray
    }
}
else {
    Write-Warning "commandRegistry.json not found at $registryPath - new commands will not load."
}

# A live MCP server keeps better_sqlite3.node mapped into memory, and Windows refuses to
# unlink a loaded native module - so `npm ci` dies with EPERM (and silently falls back to the
# previous build) while Cursor or Revit still holds a server process. Clients respawn the
# server on reconnect, so stopping it here costs nothing. Runs even with -SkipServer, because
# build-assistant-cursor.ps1 below builds server/ too.
$serverEntryPattern = "$([regex]::Escape((Split-Path -Leaf $repo)))/server/build/index\.js"
$staleServers = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and ($_.CommandLine -replace '\\', '/') -match $serverEntryPattern }

foreach ($staleServer in $staleServers) {
    try {
        Stop-Process -Id $staleServer.ProcessId -Force -ErrorAction Stop
        Write-Host "Stopped running MCP server (PID $($staleServer.ProcessId)) - it locks better_sqlite3.node" -ForegroundColor Gray
    }
    catch {
        Write-Warning "Could not stop node.exe PID $($staleServer.ProcessId): $($_.Exception.Message)"
    }
}

if (-not $SkipServer) {
    Write-Host "Building MCP server..." -ForegroundColor Cyan
    $serverDir = Join-Path $repo "server"
    $serverJs = Join-Path $serverDir "build\index.js"
    Push-Location $serverDir
    try {
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'

        $npmOk = $false
        foreach ($cmd in @('ci', 'install')) {
            Write-Host "npm $cmd in server/..." -ForegroundColor Gray
            npm $cmd 2>&1 | Out-Host
            if ($LASTEXITCODE -eq 0) {
                $npmOk = $true
                break
            }
        }

        $ErrorActionPreference = $prevEap

        if (-not $npmOk) {
            if (Test-Path $serverJs) {
                Write-Warning "npm install failed in server/ (EPERM?). Using existing build/index.js"
            }
            else {
                Write-Error "npm install failed in server/ and build/index.js is missing."
            }
        }
        else {
            $ErrorActionPreference = 'Continue'
            npm run build 2>&1 | Out-Host
            $buildCode = $LASTEXITCODE
            $ErrorActionPreference = $prevEap

            if ($buildCode -ne 0) {
                if (Test-Path $serverJs) {
                    Write-Warning "npm run build failed; using existing $serverJs"
                }
                else {
                    Write-Error "MCP server build failed."
                }
            }
            else {
                Write-Host "Built server/build/index.js" -ForegroundColor Green
            }
        }
    }
    finally { Pop-Location }
}

$buildCursor = Join-Path $repo "scripts\build-assistant-cursor.ps1"
if (Test-Path $buildCursor) {
    Write-Host "Building Cursor in-Revit stack (bridge + bundle)..." -ForegroundColor Cyan
    & $buildCursor -RevitVersion $RevitVersion
    # build-assistant-cursor may emit npm warnings to stderr; success = payload folders exist.
    $addinRoot = Join-Path $env:AppData "Autodesk\Revit\Addins\$RevitVersion\revit_mcp_plugin"
    $cursorOk = (Test-Path (Join-Path $addinRoot "assistant-bridge\dist\index.js")) `
        -and (Test-Path (Join-Path $addinRoot "mcp-server\build\index.js")) `
        -and (Test-Path (Join-Path $addinRoot "assistant-bundle\.cursor\rules"))
    if (-not $cursorOk) {
        Write-Error "build-assistant-cursor.ps1 failed - Cursor payload not found in $addinRoot"
    }
}

Write-Host ""
Write-Host "Done. Start Revit, then restart the MCP client so it reconnects." -ForegroundColor Green
