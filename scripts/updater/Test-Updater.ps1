<#
.SYNOPSIS
    Проверка апдейтера: разбор скриптов, слияние реестра, сквозная установка с откатом.

.DESCRIPTION
    Сеть не нужна: загрузчик ассетов подменяется, а образцом служит уже развёрнутая на
    этой машине установка. Поэтому нужны закрытый Revit и рабочее развёртывание
    (deploy-local.ps1) - без него сравнивать не с чем.

    Песочница живёт в %TEMP%, настоящая папка аддинов не трогается: APPDATA на время
    сквозной части подменяется.

.EXAMPLE
    .\scripts\updater\Test-Updater.ps1
    .\scripts\updater\Test-Updater.ps1 -RevitVersion 2025 -KeepSandbox
#>
[CmdletBinding()]
param(
    [string]$RevitVersion = "2023",
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$updater = Join-Path $PSScriptRoot "Update-RevitMcp.ps1"

$script:Failures = 0
function Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        Write-Host "  OK   $Name" -ForegroundColor Green
    }
    catch {
        $script:Failures++
        Write-Host "  FAIL $Name -> $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ── Разбор ───────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "== синтаксис ==" -ForegroundColor Cyan
foreach ($file in @($updater,
        (Join-Path $repoRoot "scripts\install-updater.ps1"),
        (Join-Path $repoRoot "scripts\deploy-local.ps1"))) {
    Check (Split-Path -Leaf $file) {
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$errors) | Out-Null
        if ($errors -and $errors.Count -gt 0) {
            throw (($errors | ForEach-Object { "строка $($_.Extent.StartLineNumber): $($_.Message)" }) -join "; ")
        }
    }
}

# Функции апдейтера без его основного хода.
$ast = [System.Management.Automation.Language.Parser]::ParseFile($updater, [ref]$null, [ref]$null)
$ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false) |
    ForEach-Object { Invoke-Expression $_.Extent.Text }

$sandbox = Join-Path $env:TEMP ("revit-mcp-updater-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:Root = Join-Path $sandbox "updater"
$script:LogFile = Join-Path $script:Root "updater.log"
$script:Status = [ordered]@{
    lastRun = (Get-Date).ToString("o"); result = "unknown"; message = ""
    available = $null; installed = [ordered]@{}
}
New-Item -ItemType Directory -Force -Path $script:Root | Out-Null

$realAppData = $env:APPDATA
$source = Join-Path $realAppData "Autodesk\Revit\Addins\$RevitVersion\revit_mcp_plugin"
if (-not (Test-Path $source)) {
    Write-Host "Нет развёрнутой установки $source - сравнивать не с чем." -ForegroundColor Red
    Write-Host "Сначала: .\scripts\deploy-local.ps1 -RevitVersion $RevitVersion" -ForegroundColor Yellow
    exit 1
}

# ── Слияние реестра команд ───────────────────────────────────────────────────

Write-Host ""
Write-Host "== реестр команд ==" -ForegroundColor Cyan
$work = Join-Path $sandbox "registry"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$oldRegistry = Join-Path $source "Commands\commandRegistry.json"
$commandJson = Join-Path $source "Commands\RevitMCPCommandSet\command.json"
$newRegistry = Join-Path $work "commandRegistry.json"

Check "настройки и команды переживают слияние" {
    $before = Read-JsonFile $oldRegistry
    Build-CommandRegistry -OldRegistryPath $oldRegistry -CommandJsonPath $commandJson `
        -NewRegistryPath $newRegistry -RevitVersion $RevitVersion
    $after = Read-JsonFile $newRegistry

    $declared = (Read-JsonFile $commandJson).commands
    $names = @($after.commands | ForEach-Object { $_.commandName })
    foreach ($cmd in $declared) {
        if ($names -notcontains $cmd.commandName) { throw "команда $($cmd.commandName) не попала в реестр" }
    }
    if (@($before.commands).Count -gt @($after.commands).Count) { throw "команд стало меньше" }
    foreach ($prop in $before.settings.PSObject.Properties) {
        $now = $after.settings.PSObject.Properties[$prop.Name].Value
        if ("$now" -ne "$($prop.Value)") { throw "настройка $($prop.Name): '$($prop.Value)' -> '$now'" }
    }
}

Check "кириллица в описаниях не поехала" {
    $text = [IO.File]::ReadAllText($newRegistry, [Text.Encoding]::UTF8)
    # Шаблон из кодов: PowerShell 5.1 считает U+201A и соседей кавычками, и мохибейк,
    # вписанный литералом, разорвал бы саму эту строку.
    $mojibake = (([char]0x00D0), ([char]0x00D1), ([char]0xFFFD)) -join '|'
    if ($text -match $mojibake) { throw "мохибейк в файле" }
    if ($text -notmatch '[А-Яа-я]') { throw "кириллицы нет вовсе - описания потерялись" }
}

Check "реестр без BOM" {
    $bytes = [IO.File]::ReadAllBytes($newRegistry)
    if ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { throw "BOM на месте" }
}

# ── Сквозная установка ───────────────────────────────────────────────────────

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Host "Revit запущен - сквозная часть пропущена (апдейтер по делу отказался бы)." -ForegroundColor Yellow
    if ($script:Failures -gt 0) { exit 1 }
    exit 0
}

$env:APPDATA = Join-Path $sandbox "AppData"
$addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$installed = Join-Path $addinsRoot "revit_mcp_plugin"
$oldRuntimeSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

try {
    Write-Host ""
    Write-Host "== песочница ==" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $addinsRoot | Out-Null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Copy-Tree $source $installed
    Write-Host "  копия развёрнутой установки за $([int]$sw.Elapsed.TotalSeconds) с" -ForegroundColor DarkGray

    Write-JsonFile ([ordered]@{
            version       = "v0.0.1"
            runtimeSha256 = $oldRuntimeSha
            installedAt   = (Get-Date).AddDays(-5).ToUniversalTime().ToString("o")
            source        = "github"
        }) (Join-Path $installed "version.json")

    # Метка, которую обязано пережить обновление: ключ Cursor лежит в тех же settings.
    $registryPath = Join-Path $installed "Commands\commandRegistry.json"
    $registry = Read-JsonFile $registryPath
    $registry.settings | Add-Member -NotePropertyName "assistantCursorApiKey" -NotePropertyValue "ключ-не-трогать" -Force
    Write-JsonFile $registry $registryPath

    New-Item -ItemType Directory -Force -Path (Join-Path $installed "Logs") | Out-Null
    Set-Content (Join-Path $installed "Logs\жалоба.jsonl") "важная строка" -Encoding UTF8

    # ── поддельный релиз: настоящие DLL, архив собирается на месте ──
    $release = Join-Path $sandbox "release"
    $pluginTree = Join-Path $release "plugin-tree"
    $pluginDest = Join-Path $pluginTree "revit_mcp_plugin"
    New-Item -ItemType Directory -Force -Path (Join-Path $pluginDest "Commands\RevitMCPCommandSet\$RevitVersion") | Out-Null
    Set-Content (Join-Path $pluginTree "mcp-servers-for-revit.addin") "<RevitAddIns/>" -Encoding UTF8
    Copy-Item (Join-Path $source "RevitMCPPlugin.dll") $pluginDest -Force
    Copy-Item $commandJson (Join-Path $pluginDest "Commands\RevitMCPCommandSet") -Force
    Copy-Item (Join-Path $source "Commands\RevitMCPCommandSet\$RevitVersion\RevitMCPCommandSet.dll") `
        (Join-Path $pluginDest "Commands\RevitMCPCommandSet\$RevitVersion") -Force

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $goodZip = Join-Path $release "plugin.zip"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($pluginTree, $goodZip, 'Fastest', $false)

    # Битая сборка: без DLL командсета - проверка обязана поймать её до подмены.
    $brokenTree = Join-Path $release "broken-tree"
    Copy-Tree $pluginTree $brokenTree
    Remove-Item (Join-Path $brokenTree "revit_mcp_plugin\Commands\RevitMCPCommandSet\$RevitVersion\RevitMCPCommandSet.dll") -Force
    $brokenZip = Join-Path $release "broken.zip"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($brokenTree, $brokenZip, 'Fastest', $false)

    $script:AssetMap = @{ "plugin.zip" = $goodZip; "broken.zip" = $brokenZip }
    # Единственная подмена: сеть. Всё остальное - настоящий код апдейтера.
    function Get-VerifiedAsset {
        param($Release, $Info, [string]$Token, [string]$CacheDir)
        return $script:AssetMap[$Info.asset]
    }

    $fakeRelease = [pscustomobject]@{ tag_name = "v9.9.9"; assets = @() }
    function New-Manifest {
        param([string]$Asset)
        return [pscustomobject]@{
            version = "v9.9.9"
            runtime = [pscustomobject]@{ asset = "runtime.zip"; sha256 = $oldRuntimeSha; sizeBytes = 1 }
            plugins = [pscustomobject]@{ $RevitVersion = [pscustomobject]@{ asset = $Asset; sha256 = "x"; sizeBytes = 1 } }
        }
    }

    $staging = Join-Path $sandbox "staging"
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    Write-Host ""
    Write-Host "== битый релиз ==" -ForegroundColor Cyan
    Check "отклонён, прежняя версия осталась рабочей" {
        $reason = $null
        try {
            Install-RevitVersion -RevitVersion $RevitVersion -Manifest (New-Manifest "broken.zip") `
                -Release $fakeRelease -Token "t" -CacheDir $release -StagingDir $staging -ForceInstall $false | Out-Null
        }
        catch { $reason = $_.Exception.Message }
        if (-not $reason) { throw "битую сборку приняли" }
        if ($reason -notmatch 'RevitMCPCommandSet\.dll') { throw "отказ по другой причине: $reason" }
        if ((Read-JsonFile (Join-Path $installed "version.json")).version -ne "v0.0.1") { throw "версия изменилась" }
        if (-not (Test-Path (Join-Path $installed "Logs\жалоба.jsonl"))) { throw "логи пропали" }
    }

    Write-Host ""
    Write-Host "== рабочий релиз ==" -ForegroundColor Cyan
    Check "установлен, пользовательское перенесено" {
        $changed = Install-RevitVersion -RevitVersion $RevitVersion -Manifest (New-Manifest "plugin.zip") `
            -Release $fakeRelease -Token "t" -CacheDir $release -StagingDir $staging -ForceInstall $false
        if (-not $changed) { throw "апдейтер решил, что ставить нечего" }

        if ((Read-JsonFile (Join-Path $installed "version.json")).version -ne "v9.9.9") { throw "version.json не обновился" }

        $now = Read-JsonFile (Join-Path $installed "Commands\commandRegistry.json")
        if ($now.settings.assistantCursorApiKey -ne "ключ-не-трогать") { throw "ключ Cursor потерян" }
        if (@($now.commands).Count -lt 70) { throw "команд осталось $(@($now.commands).Count)" }

        if ("важная строка" -ne (Get-Content (Join-Path $installed "Logs\жалоба.jsonl") -Encoding UTF8)) { throw "лог потерян" }
        if (-not (Test-Path (Join-Path $installed "mcp-server\revit-data.db.bak"))) { throw "прежняя база норм не сохранена" }
        if (-not (Test-Path (Join-Path $installed "assistant-bridge\dist\index.js"))) { throw "рантайм не перенесён" }
        if (-not (Test-Path (Join-Path $addinsRoot "mcp-servers-for-revit.addin"))) { throw ".addin не положен в корень" }
        if (@(Get-ChildItem $addinsRoot -Directory -Filter "revit_mcp_plugin.bak-*").Count -ne 1) { throw "резервная копия не одна" }
    }

    Check "повторный запуск ничего не делает" {
        $changed = Install-RevitVersion -RevitVersion $RevitVersion -Manifest (New-Manifest "plugin.zip") `
            -Release $fakeRelease -Token "t" -CacheDir $release -StagingDir $staging -ForceInstall $false
        if ($changed) { throw "переустановил ту же версию" }
    }

    Write-Host ""
    Write-Host "== подготовка при открытом Revit ==" -ForegroundColor Cyan
    Check "рантайм распакован заранее, установка потом идёт из кэша" {
        # Возвращаем машину на старую версию, чтобы было что готовить.
        Write-JsonFile ([ordered]@{ version = "v0.0.1"; runtimeSha256 = "другой"; installedAt = (Get-Date).ToUniversalTime().ToString("o"); source = "github" }) `
            (Join-Path $installed "version.json")

        # Рантайм-архив собираем из настоящего дерева: подготовка обязана его распаковать.
        $runtimeTree = Join-Path $release "runtime-tree"
        foreach ($part in @("assistant-bridge", "mcp-server", "assistant-bundle", "normatives")) {
            Copy-Tree (Join-Path $installed $part) (Join-Path $runtimeTree $part)
        }
        $runtimeZip = Join-Path $release "runtime.zip"
        [System.IO.Compression.ZipFile]::CreateFromDirectory($runtimeTree, $runtimeZip, 'Fastest', $false)
        $script:AssetMap["runtime.zip"] = $runtimeZip

        $manifest = New-Manifest "plugin.zip"
        $prefetched = Invoke-Prefetch -Manifest $manifest -Release $fakeRelease -Token "t" `
            -CacheDir $release -Years @($RevitVersion)
        if (-not $prefetched) { throw "подготовка решила, что качать нечего" }

        $cache = Join-Path $release ("runtime-" + $manifest.runtime.sha256.Substring(0, 12))
        if (-not (Test-Path (Join-Path $cache ".complete"))) { throw "рантайм не распакован в кэш" }
        if (-not (Test-Path (Join-Path $cache "mcp-server\build\index.js"))) { throw "в кэше нет MCP-сервера" }

        # А теперь установка - она обязана взять готовое из кэша, а не качать заново.
        $script:AssetMap["runtime.zip"] = "путь-которого-нет"
        $changed = Install-RevitVersion -RevitVersion $RevitVersion -Manifest $manifest `
            -Release $fakeRelease -Token "t" -CacheDir $release -StagingDir $staging -ForceInstall $false
        if (-not $changed) { throw "установка из подготовленного не прошла" }
        if ((Read-JsonFile (Join-Path $installed "version.json")).version -ne "v9.9.9") { throw "версия не обновилась" }
    }

    Write-Host ""
    Write-Host "== чистая машина ==" -ForegroundColor Cyan
    Check "ставится с нуля, команды регистрируются" {
        # Распакованный кэш рантайма вместо загрузки: имя папки апдейтер строит из sha.
        $cache = Join-Path $release ("runtime-" + $oldRuntimeSha.Substring(0, 12))
        foreach ($part in @("assistant-bridge", "mcp-server", "assistant-bundle", "normatives")) {
            Copy-Tree (Join-Path $installed $part) (Join-Path $cache $part)
        }
        Set-Content (Join-Path $cache ".complete") "" -Encoding UTF8

        # Страховка от опечатки в пути: чистим только песочницу.
        if ($addinsRoot -notlike "$sandbox*") { throw "путь вне песочницы: $addinsRoot" }
        Get-ChildItem $addinsRoot -Directory | ForEach-Object { Remove-Tree $_.FullName }
        if (Test-Path $installed) { throw "песочница не очистилась" }

        $changed = Install-RevitVersion -RevitVersion $RevitVersion -Manifest (New-Manifest "plugin.zip") `
            -Release $fakeRelease -Token "t" -CacheDir $release -StagingDir $staging -ForceInstall $false
        if (-not $changed) { throw "на чистой машине ничего не поставил" }

        $now = Read-JsonFile (Join-Path $installed "Commands\commandRegistry.json")
        if (@($now.commands).Count -lt 70) { throw "команд зарегистрировано: $(@($now.commands).Count)" }
        if (-not $now.settings.autoStartOnLaunch) { throw "нет настроек по умолчанию" }
        if (-not (Test-Path (Join-Path $installed "mcp-server\build\index.js"))) { throw "рантайм не встал" }
    }

    Write-Host ""
    Write-Host "== оборванная подмена ==" -ForegroundColor Cyan
    Check "папка возвращается из резервной копии" {
        # Ровно то состояние, что остаётся после выключения питания между двумя
        # переименованиями: рабочей папки нет, рядом лежит только резервная.
        $backup = Join-Path $addinsRoot "revit_mcp_plugin.bak-20260101-000000"
        Move-DirectoryWithRetry -Path $installed -Destination $backup
        if (Test-Path $installed) { throw "не удалось смоделировать обрыв" }

        Restore-InterruptedSwap -AddinsRoot $addinsRoot -Installed $installed
        if (-not (Test-Path (Join-Path $installed "RevitMCPPlugin.dll"))) { throw "плагин не вернулся" }
        if (Test-Path $backup) { throw "резервная копия осталась дублем" }
    }

    Check "версия Revit без сборки в релизе пропускается" {
        $changed = Install-RevitVersion -RevitVersion "2019" -Manifest (New-Manifest "plugin.zip") `
            -Release $fakeRelease -Token "t" -CacheDir $release -StagingDir $staging -ForceInstall $false
        if ($changed) { throw "поставил несуществующую сборку" }
    }
}
finally {
    $env:APPDATA = $realAppData
    if ($KeepSandbox) {
        Write-Host ""
        Write-Host "Песочница оставлена: $sandbox" -ForegroundColor DarkGray
    }
    else {
        try { Remove-Tree $sandbox } catch { Write-Host "Песочница осталась: $sandbox" -ForegroundColor DarkGray }
    }
}

Write-Host ""
if ($script:Failures -gt 0) {
    Write-Host "ПРОВАЛЕНО проверок: $script:Failures" -ForegroundColor Red
    exit 1
}
Write-Host "Все проверки пройдены." -ForegroundColor Green
