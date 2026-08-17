<#
.SYNOPSIS
    Ставит авто-обновление Revit MCP на машину архитектора. Запускается один раз.

.DESCRIPTION
    После этого свежие релизы приезжают сами: задача планировщика проверяет GitHub при
    входе в систему и каждые 3 часа, и ставит сборку, когда Revit закрыт. Ни репозитория,
    ни .NET SDK, ни сборки на машине больше не нужно - только Node.js.

    Скрипт самодостаточен: если рядом нет Update-RevitMcp.ps1, он скачает его из
    репозитория тем же токеном. То есть на машину достаточно перенести один этот файл.

    Токен нужен fine-grained, только на чтение (Contents: Read-only) и только на этот
    репозиторий. Он ложится в %LocalAppData% зашифрованным DPAPI - расшифровать сможет
    только этот пользователь Windows на этой машине.

.EXAMPLE
    .\install-updater.ps1
    .\install-updater.ps1 -RevitVersions 2023,2025 -Token github_pat_xxx
#>
[CmdletBinding()]
param(
    [string]$Repo = "NewLevelHub/mcp-servers-for-revit",
    [string]$Token,
    [string[]]$RevitVersions,
    [string]$Branch = "main",
    [switch]$AllowPrerelease,
    [string]$TaskName = "RevitMcpUpdater",
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$InstallDir = Join-Path $env:LOCALAPPDATA "RevitMcpUpdater"
$ScriptName = "Update-RevitMcp.ps1"
$TargetScript = Join-Path $InstallDir $ScriptName
$ConfigPath = Join-Path $InstallDir "config.json"

Write-Host ""
Write-Host "=== Авто-обновление Revit MCP ===" -ForegroundColor Cyan
Write-Host ""

# ── Токен ────────────────────────────────────────────────────────────────────

if (-not $Token) {
    $secure = Read-Host "Токен GitHub (fine-grained, Contents: Read-only)" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $Token = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
if (-not $Token) { throw "Без токена обновления качать нечем." }

$headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = "application/vnd.github+json"
    "User-Agent"           = "revit-mcp-updater-setup"
    "X-GitHub-Api-Version" = "2022-11-28"
}

Write-Host "Проверяю доступ к $Repo..." -ForegroundColor Gray
try {
    $null = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo" -Headers $headers
}
catch {
    throw "Токен не даёт доступа к $Repo : $($_.Exception.Message)"
}
Write-Host "  доступ есть" -ForegroundColor Green

# ── Какие версии Revit обслуживаем ───────────────────────────────────────────

$addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
if (-not $RevitVersions) {
    if (Test-Path $addinsRoot) {
        $RevitVersions = @(
            Get-ChildItem $addinsRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^\d{4}$' -and (Test-Path (Join-Path $_.FullName "revit_mcp_plugin")) } |
                ForEach-Object { $_.Name }
        )
    }
    if (-not $RevitVersions -or $RevitVersions.Count -eq 0) {
        throw "Не нашёл ни одной установки плагина в $addinsRoot. Укажите вручную: -RevitVersions 2023"
    }
    Write-Host "Найдены установки плагина: $($RevitVersions -join ', ')" -ForegroundColor Green
}

# ── Node.js ──────────────────────────────────────────────────────────────────

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Warning "Node.js не найден. Мост и MCP-сервер без него не запустятся - поставьте Node LTS с nodejs.org."
}
else {
    $nodeVersion = & node -v
    if ($nodeVersion -match '^v(\d+)\.(\d+)') {
        $major = [int]$Matches[1]
        $minor = [int]$Matches[2]
        if ($major -lt 22 -or ($major -eq 22 -and $minor -lt 13)) {
            Write-Warning "Node $nodeVersion старее 22.13 - Cursor SDK на нём не работает. Обновите Node."
        }
        else {
            # Точное требование знает только релиз: нативный модуль базы норм привязан к
            # major Node, и апдейтер на первом же прогоне скажет, какой major нужен.
            Write-Host "Node $nodeVersion" -ForegroundColor Green
        }
    }
}

# ── Раскладка апдейтера ──────────────────────────────────────────────────────

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

$localScript = Join-Path $PSScriptRoot "updater\$ScriptName"
if (-not (Test-Path $localScript)) {
    $localScript = Join-Path $PSScriptRoot $ScriptName
}

if (Test-Path $localScript) {
    Copy-Item $localScript $TargetScript -Force
    Write-Host "Апдейтер скопирован: $TargetScript" -ForegroundColor Green
}
else {
    Write-Host "Скачиваю апдейтер из репозитория..." -ForegroundColor Gray
    $rawHeaders = $headers.Clone()
    $rawHeaders.Accept = "application/vnd.github.raw"
    $url = "https://api.github.com/repos/$Repo/contents/scripts/updater/$ScriptName" + "?ref=$Branch"
    $response = Invoke-WebRequest -Uri $url -Headers $rawHeaders -UseBasicParsing
    # Пишем байтами: скрипт с кириллицей в комментариях легко испортить перекодировкой.
    [IO.File]::WriteAllBytes($TargetScript, $response.RawContentStream.ToArray())
    Write-Host "Апдейтер скачан: $TargetScript" -ForegroundColor Green
}

# ── Конфиг ───────────────────────────────────────────────────────────────────

$config = [ordered]@{
    repo            = $Repo
    revitVersions   = @($RevitVersions)
    allowPrerelease = [bool]$AllowPrerelease
    branch          = $Branch
    selfUpdate      = $true
    tokenEncrypted  = (ConvertTo-SecureString $Token -AsPlainText -Force | ConvertFrom-SecureString)
}
[IO.File]::WriteAllText($ConfigPath, ($config | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Настройки записаны: $ConfigPath" -ForegroundColor Green

# ── Задача планировщика ──────────────────────────────────────────────────────

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$TargetScript`""

# Вход в систему - главный шанс: Revit ещё не открыт. Задержка короткая нарочно - надо
# успеть до того, как архитектор откроет Revit; сеть после включения поднимается не сразу,
# поэтому апдейтер сам пережидает её отсутствие. Повтор каждые 3 часа ловит обед и конец
# дня; при запущенном Revit проверка не пропадает зря - она качает сборку впрок.
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$atLogon.Delay = "PT1M"

$noon = Get-Date -Hour 12 -Minute 0 -Second 0
$periodic = New-ScheduledTaskTrigger -Daily -At $noon
$periodic.Repetition = (New-ScheduledTaskTrigger -Once -At $noon `
        -RepetitionInterval (New-TimeSpan -Hours 3) `
        -RepetitionDuration (New-TimeSpan -Hours 24)).Repetition

$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($atLogon, $periodic) `
    -Settings $settings -Description "Обновление Revit MCP из релизов $Repo" -Force | Out-Null

Write-Host "Задача планировщика зарегистрирована: $TaskName" -ForegroundColor Green

# ── Первый прогон ────────────────────────────────────────────────────────────

if ($NoRun) {
    Write-Host ""
    Write-Host "Готово. Первая проверка - при следующем входе в систему." -ForegroundColor Green
    return
}

Write-Host ""
Write-Host "Первый прогон:" -ForegroundColor Cyan
& $TargetScript -ConfigPath $ConfigPath
$code = $LASTEXITCODE

Write-Host ""
if ($code -eq 0) {
    Write-Host "Готово. Дальше обновления приезжают сами." -ForegroundColor Green
}
else {
    Write-Warning "Первый прогон завершился с ошибкой. Журнал: $InstallDir\updater.log"
}
Write-Host "  журнал:   $InstallDir\updater.log"
Write-Host "  состояние: $InstallDir\status.json"
Write-Host ""
