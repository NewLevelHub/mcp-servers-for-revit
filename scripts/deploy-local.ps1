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
    # Читаем через .NET: Get-Content без -Encoding принимает UTF-8 без BOM за ANSI,
    # и кириллические описания уезжают в мохибейк при каждой записи (ÐŸÑ€Ð¾Ð¼Ð°Ñ€...).
    # ReadAllText с явной UTF-8 снимает BOM, если он есть, и не ломает файлы без него.
    $registry = [IO.File]::ReadAllText($registryPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $declared = ([IO.File]::ReadAllText($commandJson, [Text.Encoding]::UTF8) | ConvertFrom-Json).commands
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
#
# The deployed assistant-bridge counts too. It is a child of Revit but outlives it often
# enough to matter, and it holds Addins/.../assistant-bridge/dist open - so the deploy died
# on Remove-Item halfway through, *after* the plugin DLL was already replaced. That leaves
# the machine with a new plugin and yesterday's bridge, and the only clue is one red line
# buried under a screen of npm warnings.
$stalePatterns = @(
    "$([regex]::Escape((Split-Path -Leaf $repo)))/server/build/index\.js",
    "revit_mcp_plugin/(assistant-bridge|mcp-server)/"
)
$staleServers = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $cmd = ($_.CommandLine -replace '\\', '/')
        $cmd -and ($stalePatterns | Where-Object { $cmd -match $_ })
    }

foreach ($staleServer in $staleServers) {
    try {
        Stop-Process -Id $staleServer.ProcessId -Force -ErrorAction Stop
        Write-Host "Stopped stale node.exe (PID $($staleServer.ProcessId)) - it locks files this deploy replaces" -ForegroundColor Gray
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

# ── Проверка: встало ли на самом деле ────────────────────────────────────────
# "Файл на месте" ничего не доказывает: папка аддинов пережила прошлые сборки, и
# позавчерашний mcp-server спокойно проходил старую проверку существования, пока
# правки лежали только в репозитории. Сравниваем время развёрнутого артефакта с
# самым свежим исходником — протухшее видно сразу.

$addinRoot = Join-Path $env:AppData "Autodesk\Revit\Addins\$RevitVersion\revit_mcp_plugin"
$stale = @()

function Get-NewestSourceTime {
    param([string]$Path, [string[]]$Include)
    if (-not (Test-Path $Path)) { return $null }
    $newest = Get-ChildItem $Path -Recurse -File -Include $Include -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules|build|dist)\\' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) { return $newest.LastWriteTime }
    return $null
}

function Test-Artifact {
    param([string]$Label, [string]$Artifact, [datetime]$SourceTime)

    if (-not (Test-Path $Artifact)) {
        Write-Host ("  {0,-14} НЕТ ФАЙЛА  {1}" -f $Label, $Artifact) -ForegroundColor Red
        $script:stale += $Label
        return
    }

    $built = (Get-Item $Artifact).LastWriteTime
    if ($built -lt $SourceTime) {
        Write-Host ("  {0,-14} УСТАРЕЛ    собран {1:HH:mm dd.MM}, исходник {2:HH:mm dd.MM}" -f `
            $Label, $built, $SourceTime) -ForegroundColor Red
        $script:stale += $Label
    }
    else {
        Write-Host ("  {0,-14} ОК         {1:HH:mm dd.MM}" -f $Label, $built) -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Проверка развёртывания:" -ForegroundColor Cyan

$pluginSrc = Get-NewestSourceTime (Join-Path $repo "plugin") @("*.cs", "*.xaml")
$cmdSrc    = Get-NewestSourceTime (Join-Path $repo "commandset") @("*.cs")
$srvSrc    = Get-NewestSourceTime (Join-Path $repo "server\src") @("*.ts")

if ($pluginSrc) { Test-Artifact "плагин"     (Join-Path $addinRoot "RevitMCPPlugin.dll") $pluginSrc }
if ($cmdSrc)    { Test-Artifact "commandset" (Join-Path $addinRoot "Commands\RevitMCPCommandSet\$RevitVersion\RevitMCPCommandSet.dll") $cmdSrc }
if ($srvSrc)    { Test-Artifact "MCP-сервер"  (Join-Path $addinRoot "mcp-server\build\index.js") $srvSrc }

# Пустая библиотека норм — самая тихая поломка: нормоконтроль отвечает
# "нарушений нет" вместо "проверять нечем".
$normDb = Join-Path $addinRoot "mcp-server\revit-data.db"
if (Test-Path $normDb) {
    # Кавычки в JS — одинарные: PowerShell срезает двойные при передаче в node.exe,
    # и скрипт приезжает в node уже сломанным ("Expected ',', got '<eof>'").
    # Запускать из папки развёрнутого сервера: require ищет модуль от текущего
    # каталога, а в корне репозитория node_modules нет.
    $countScript = "const D=require('better-sqlite3');" +
        "console.log(new D(process.argv[1],{readonly:true}).prepare('SELECT COUNT(*) c FROM norm_rules').get().c);"
    Push-Location (Split-Path -Parent $normDb)
    try { $rules = & node -e $countScript $normDb 2>$null }
    finally { Pop-Location }
    if ($rules -and [int]$rules -gt 0) {
        Write-Host ("  {0,-14} ОК         {1} правил" -f "база норм", $rules) -ForegroundColor Green
    }
    else {
        Write-Host ("  {0,-14} ПУСТАЯ     нормоконтроль ничего не найдёт (npm run seed:norms)" -f "база норм") -ForegroundColor Red
        $stale += "база норм"
    }

    # База норм не собирается этим скриптом, а копируется из server\revit-data.db.
    # Правки в правилах попадут в Revit только после отдельного пересева, поэтому
    # молчать про расхождение нельзя — проверка норм просто продолжит работать по
    # старым значениям, и это ничем не проявится.
    $seedDb = Join-Path $repo "server\revit-data.db"
    $normSrc = Get-NewestSourceTime (Join-Path $repo "server\src\normatives") @("*.ts")
    if ((Test-Path $seedDb) -and $normSrc -and ((Get-Item $seedDb).LastWriteTime -lt $normSrc)) {
        Write-Host ("  {0,-14} ВНИМАНИЕ   правила норм менялись после сборки базы —" -f "") -ForegroundColor Yellow
        Write-Host ("  {0,-14}            если правка про значения: cd server; npm run seed:residential-norms" -f "") -ForegroundColor Yellow
    }
}

Write-Host ""
if ($stale.Count -gt 0) {
    Write-Error ("Развёрнуто НЕ всё: " + ($stale -join ", ") + ". В Revit пойдёт старая версия.")
}

Write-Host "Готово. Запускайте Revit." -ForegroundColor Green
