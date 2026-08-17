<#
.SYNOPSIS
    Установка свежего релиза Revit MCP на машину архитектора. Работает без человека.

.DESCRIPTION
    Запускается задачей планировщика (вход в систему + раз в 3 часа). Ставит обновление
    только когда Revit закрыт: плагин держит DLL, а развёрнутый node.exe держит
    node_modules, поэтому подмена на живом Revit оставляет половину новой сборки рядом с
    половиной старой.

    Что сохраняется от предыдущей установки: settings из commandRegistry.json (там ключ
    Cursor, порт, папка для жалоб), сами команды с их флагом enabled, Logs (жалобы и
    история ходов) и старая база норм рядом как revit-data.db.bak.

    Старая папка переименовывается, а не удаляется: если новая не проходит проверку,
    скрипт возвращает предыдущую и уходит, не тронув рабочее место.

.EXAMPLE
    .\Update-RevitMcp.ps1
    .\Update-RevitMcp.ps1 -CheckOnly     # только сказать, что доступно
    .\Update-RevitMcp.ps1 -Force         # переустановить текущую версию поверх
#>
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $env:LOCALAPPDATA "RevitMcpUpdater\config.json"),
    [switch]$Force,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$script:Root = Split-Path -Parent $ConfigPath
$script:LogFile = Join-Path $script:Root "updater.log"
$script:Status = [ordered]@{
    lastRun    = (Get-Date).ToString("o")
    result     = "unknown"
    message    = ""
    available  = $null
    installed  = [ordered]@{}
}

# ── Журнал и статус ──────────────────────────────────────────────────────────

function Write-Log {
    param([string]$Message, [ValidateSet("INFO", "WARN", "ERROR")][string]$Level = "INFO")
    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    try {
        if (-not (Test-Path $script:Root)) {
            New-Item -ItemType Directory -Force -Path $script:Root | Out-Null
        }
        if ((Test-Path $script:LogFile) -and (Get-Item $script:LogFile).Length -gt 1MB) {
            Move-Item $script:LogFile "$script:LogFile.1" -Force
        }
        Add-Content -Path $script:LogFile -Value $line -Encoding UTF8
    }
    catch { }
    Write-Host $line
}

function Save-Status {
    param([string]$Result, [string]$Message)
    $script:Status.result = $Result
    $script:Status.message = $Message
    try {
        $script:Status | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $script:Root "status.json") -Encoding utf8
    }
    catch { }
}

# ── Мелкие помощники ─────────────────────────────────────────────────────────

function Get-JsonProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($prop) { return $prop.Value }
    return $null
}

function Write-JsonFile {
    param($Object, [string]$Path)
    $json = $Object | ConvertTo-Json -Depth 12
    # Плагин читает эти файлы Newtonsoft'ом, а deploy-local.ps1 - .NET-ом с явной UTF-8;
    # BOM в JSON никому не нужен, поэтому пишем без него.
    [IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-JsonFile {
    param([string]$Path)
    return [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json
}

<#
.SYNOPSIS
    Копирует дерево через robocopy.
.DESCRIPTION
    Copy-Item на Windows PowerShell 5.1 спотыкается о длинные пути внутри node_modules,
    и делает это молча в середине копии. robocopy длинные пути умеет и возвращает код
    ниже 8 при успехе (1 - скопировано, 0 - нечего копировать).
#>
function Copy-Tree {
    param([string]$Source, [string]$Destination)
    $null = robocopy $Source $Destination /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy '$Source' -> '$Destination' завершился с кодом $LASTEXITCODE"
    }
    $global:LASTEXITCODE = 0
}

<#
.SYNOPSIS
    Удаляет дерево, не спотыкаясь о длинные пути.
.DESCRIPTION
    Remove-Item -Recurse на Windows PowerShell 5.1 падает на путях длиннее 260 знаков, а
    внутри node_modules такие есть (@modelcontextprotocol/sdk/dist/esm/client/...). Резервная
    копия аддина с её суффиксом-датой оказывается ровно на границе, и уборка молча
    оставляла бы по сотне мегабайт после каждого обновления. robocopy длинные пути умеет:
    зеркалим поверх пустую папку и сносим то, что осталось.
#>
function Remove-Tree {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return }

    $empty = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    try {
        $null = robocopy $empty $Path /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1
        $code = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        if ($code -ge 8) { throw "не удалось очистить $Path (robocopy $code)" }
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-NodeInfo {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { return $null }
    $raw = & node -v 2>$null
    if ($raw -match '^v(\d+)\.(\d+)\.(\d+)') {
        return [pscustomobject]@{
            Raw   = $raw
            Major = [int]$Matches[1]
            Minor = [int]$Matches[2]
            Patch = [int]$Matches[3]
        }
    }
    return $null
}

<#
.SYNOPSIS
    Снимает node.exe, запущенный из папки аддина.
.DESCRIPTION
    Мост и MCP-сервер - дети Revit, но переживают его достаточно часто, чтобы это мешало:
    живой процесс держит better_sqlite3.node, и подмена папки падает на середине.
    Клиенты поднимают сервер заново при следующем подключении, так что терять нечего.
#>
function Stop-DeployedNode {
    param([string]$AddinRoot)
    $needle = ($AddinRoot -replace '\\', '/')
    $processes = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $cmd = ($_.CommandLine -replace '\\', '/')
            $cmd -and $cmd.ToLowerInvariant().Contains($needle.ToLowerInvariant())
        }
    foreach ($p in $processes) {
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Write-Log "Остановлен node.exe (PID $($p.ProcessId)) - держал файлы аддина"
        }
        catch {
            Write-Log "Не удалось остановить node.exe PID $($p.ProcessId): $($_.Exception.Message)" WARN
        }
    }
}

function Move-DirectoryWithRetry {
    param([string]$Path, [string]$Destination, [int]$Attempts = 5)
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            Move-Item -LiteralPath $Path -Destination $Destination -Force -ErrorAction Stop
            return
        }
        catch {
            if ($i -eq $Attempts) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

# ── GitHub ───────────────────────────────────────────────────────────────────

function Resolve-Token {
    param($Config)
    $encrypted = Get-JsonProperty $Config 'tokenEncrypted'
    if ($encrypted) {
        # DPAPI: расшифровать может только тот же пользователь Windows на той же машине.
        $secure = ConvertTo-SecureString $encrypted
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { return [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    $plain = Get-JsonProperty $Config 'token'
    if ($plain) { return $plain }
    throw "В конфиге нет токена: ни tokenEncrypted, ни token."
}

function Get-GitHubHeaders {
    param([string]$Token)
    return @{
        Authorization          = "Bearer $Token"
        Accept                 = "application/vnd.github+json"
        "User-Agent"           = "revit-mcp-updater"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
}

<#
.SYNOPSIS
    Спрашивает GitHub про последний релиз, пережидая непроснувшуюся сеть.
.DESCRIPTION
    Главный шанс поставить обновление - первая минута после входа в систему, пока Revit
    ещё не открыт. Wi-Fi и VPN в этот момент могут быть не готовы, и без повторов каждое
    утро превращалось бы в строчку с ошибкой вместо установки.
#>
function Get-LatestReleaseWithRetry {
    param([string]$Repo, [string]$Token, [bool]$AllowPrerelease, [int]$Attempts = 3)
    for ($i = 1; $i -le $Attempts; $i++) {
        try { return Get-LatestRelease -Repo $Repo -Token $Token -AllowPrerelease $AllowPrerelease }
        catch {
            if ($i -eq $Attempts) { throw }
            Write-Log "GitHub недоступен ($($_.Exception.Message)) - попытка $i из $Attempts, жду 20 с" WARN
            Start-Sleep -Seconds 20
        }
    }
}

function Get-LatestRelease {
    param([string]$Repo, [string]$Token, [bool]$AllowPrerelease)
    $headers = Get-GitHubHeaders $Token
    if ($AllowPrerelease) {
        $all = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=10" -Headers $headers
        $release = $all | Where-Object { -not $_.draft } | Select-Object -First 1
        if (-not $release) { throw "В репозитории $Repo нет опубликованных релизов." }
        return $release
    }
    return Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
}

<#
.SYNOPSIS
    Подтягивает свежую копию самого апдейтера.
.DESCRIPTION
    Иначе ошибка в этом скрипте лечится только новым сеансом AnyDesk на каждой машине -
    ровно то, ради чего всё и затевалось. Новая копия ложится на диск и вступает в силу
    со следующего запуска: подменять исполняемый файл на середине работы незачем.
#>
function Sync-Self {
    param([string]$Repo, [string]$Token, [string]$Branch, [string]$SelfPath)
    try {
        $headers = Get-GitHubHeaders $Token
        $headers.Accept = "application/vnd.github.raw"
        $url = "https://api.github.com/repos/$Repo/contents/scripts/updater/Update-RevitMcp.ps1" + "?ref=$Branch"
        $response = Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing
        $bytes = $response.RawContentStream.ToArray()

        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { $remoteHash = [BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace("-", "") }
        finally { $sha256.Dispose() }

        if ($remoteHash -eq (Get-FileHash $SelfPath -Algorithm SHA256).Hash) { return }

        [IO.File]::WriteAllBytes($SelfPath, $bytes)
        Write-Log "Апдейтер обновил сам себя - новая копия заработает со следующего запуска"
    }
    catch {
        Write-Log "Не удалось проверить обновление апдейтера: $($_.Exception.Message)" WARN
    }
}

<#
.SYNOPSIS
    Качает ассет релиза приватного репозитория.
.DESCRIPTION
    api.github.com отвечает редиректом на объектное хранилище, а оно отклоняет запрос,
    в котором приехал второй набор учётных данных ("Only one auth mechanism allowed").
    Invoke-WebRequest на PowerShell 5.1 тащит заголовок Authorization через редирект и
    ломается именно так, поэтому редирект отслеживаем руками и токен кладём только в
    запрос к самому GitHub.
#>
function Save-ReleaseAsset {
    param($Asset, [string]$Token, [string]$Destination)

    Add-Type -AssemblyName System.Net.Http | Out-Null
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(30)

    try {
        $url = $Asset.url
        for ($hop = 0; $hop -lt 5; $hop++) {
            $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $url)
            $request.Headers.UserAgent.ParseAdd("revit-mcp-updater")
            $request.Headers.Accept.ParseAdd("application/octet-stream")
            if ($url -like "https://api.github.com/*") {
                $request.Headers.Authorization =
                    New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $Token)
            }

            $response = $client.SendAsync(
                $request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()

            $code = [int]$response.StatusCode
            if ($code -ge 300 -and $code -lt 400 -and $response.Headers.Location) {
                $url = $response.Headers.Location.AbsoluteUri
                $response.Dispose()
                continue
            }
            if (-not $response.IsSuccessStatusCode) {
                $response.Dispose()
                throw "GitHub ответил $code на загрузку $($Asset.name)"
            }

            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $target = [IO.File]::Open($Destination, 'Create', 'Write', 'None')
            try { $source.CopyTo($target, 1048576) }
            finally {
                $target.Dispose()
                $source.Dispose()
                $response.Dispose()
            }
            return
        }
        throw "Слишком много редиректов при загрузке $($Asset.name)"
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

<#
.SYNOPSIS
    Возвращает путь к проверенному по sha256 ассету, скачивая его при необходимости.
#>
function Get-VerifiedAsset {
    param($Release, $Info, [string]$Token, [string]$CacheDir)

    $asset = $Release.assets | Where-Object { $_.name -eq $Info.asset } | Select-Object -First 1
    if (-not $asset) { throw "В релизе нет файла $($Info.asset)" }

    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $path = Join-Path $CacheDir $Info.asset

    if (Test-Path $path) {
        if ((Get-FileHash $path -Algorithm SHA256).Hash -eq $Info.sha256.ToUpperInvariant()) {
            Write-Log "$($Info.asset): уже скачан, контрольная сумма сходится"
            return $path
        }
        Remove-Item $path -Force
    }

    $mb = [math]::Round($Info.sizeBytes / 1MB, 1)
    Write-Log "Качаю $($Info.asset) ($mb МБ)"
    Save-ReleaseAsset -Asset $asset -Token $Token -Destination $path

    $actual = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($actual -ne $Info.sha256.ToUpperInvariant()) {
        Remove-Item $path -Force -ErrorAction SilentlyContinue
        throw "$($Info.asset): контрольная сумма не сошлась (ожидалась $($Info.sha256), получена $actual)"
    }
    return $path
}

function Expand-Zip {
    param([string]$ZipPath, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    # Явная UTF-8: половина нормативов - PDF с русскими именами, а архив собирает .NET 8
    # в CI и распаковывает .NET 4.8 здесь. Без указания кодировки имена зависят от того,
    # выставил ли упаковщик флаг языковой кодировки.
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $Destination, [Text.Encoding]::UTF8)
}

# ── Перенос настроек ─────────────────────────────────────────────────────────

<#
.SYNOPSIS
    Собирает commandRegistry.json для новой установки.
.DESCRIPTION
    commandRegistry.json - единственное место, где живёт ключ Cursor, порт, автозапуск и
    папка для жалоб, и одновременно список команд, который плагин действительно грузит.
    Плагин создаёт его только когда файла нет и никогда не обновляет, поэтому новая
    команда приезжает в DLL и в command.json, но остаётся невидимой ("Method not found"),
    пока её сюда не допишут - ровно то же делает deploy-local.ps1 на машине разработчика.
#>
function Build-CommandRegistry {
    param([string]$OldRegistryPath, [string]$CommandJsonPath, [string]$NewRegistryPath, [string]$RevitVersion)

    if (Test-Path $OldRegistryPath) {
        $registry = Read-JsonFile $OldRegistryPath
    }
    else {
        $registry = [pscustomobject]@{
            commands = @()
            settings = [pscustomobject]@{
                logLevel          = "Info"
                port              = 8080
                autoStartOnLaunch = $true
            }
        }
    }

    if (-not (Test-Path $CommandJsonPath)) {
        Write-Log "В сборке нет command.json - список команд оставлен как был" WARN
        Write-JsonFile $registry $NewRegistryPath
        return
    }

    $declared = (Read-JsonFile $CommandJsonPath).commands
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

    Write-JsonFile $registry $NewRegistryPath
    if ($added.Count -gt 0) {
        Write-Log "Новых команд зарегистрировано: $($added.Count) ($($added -join ', '))"
    }
}

<#
.SYNOPSIS
    Переносит в новое дерево всё, что принадлежит архитектору, а не сборке.
#>
function Import-UserState {
    param([string]$Installed, [string]$Staged, [string]$RevitVersion)

    $fresh = -not (Test-Path $Installed)
    if ($fresh) {
        Write-Log "Прежней установки нет - ставим начисто"
    }
    else {
        $oldLogs = Join-Path $Installed "Logs"
        if (Test-Path $oldLogs) {
            Copy-Tree $oldLogs (Join-Path $Staged "Logs")
            Write-Log "Logs перенесены (жалобы и история ходов)"
        }

        # Правила, сохранённые через save_norm_rule, живут только в этой базе; сборка везёт
        # свою, поэтому старую кладём рядом - так же, как это делает build-assistant-cursor.
        $oldDb = Join-Path $Installed "mcp-server\revit-data.db"
        $newServer = Join-Path $Staged "mcp-server"
        if ((Test-Path $oldDb) -and (Test-Path $newServer)) {
            Copy-Item $oldDb (Join-Path $newServer "revit-data.db.bak") -Force
            Write-Log "Прежняя база норм сохранена как revit-data.db.bak"
        }
    }

    # Реестр собираем и на чистой машине: плагин создаёт его сам, но с пустым списком
    # команд, и тогда каждый инструмент отвечает "Method not found".
    $commandsDir = Join-Path $Staged "Commands"
    New-Item -ItemType Directory -Force -Path $commandsDir | Out-Null
    # command.json приезжает от сборки командсета, на уровень глубже реестра.
    Build-CommandRegistry `
        -OldRegistryPath (Join-Path $Installed "Commands\commandRegistry.json") `
        -CommandJsonPath (Join-Path $commandsDir "RevitMCPCommandSet\command.json") `
        -NewRegistryPath (Join-Path $commandsDir "commandRegistry.json") `
        -RevitVersion $RevitVersion
}

# ── Проверка собранного дерева ───────────────────────────────────────────────

function Test-StagedTree {
    param([string]$Staged, [string]$RevitVersion)

    $required = @(
        "RevitMCPPlugin.dll",
        "Commands\RevitMCPCommandSet\command.json",
        "Commands\RevitMCPCommandSet\$RevitVersion\RevitMCPCommandSet.dll",
        "mcp-server\build\index.js",
        "mcp-server\node_modules\better-sqlite3\package.json",
        "mcp-server\revit-data.db",
        "assistant-bridge\dist\index.js",
        "assistant-bridge\node_modules\@cursor\sdk\package.json",
        "assistant-bundle\.cursor\rules"
    )

    $missing = @()
    foreach ($rel in $required) {
        if (-not (Test-Path (Join-Path $Staged $rel))) { $missing += $rel }
    }
    if ($missing.Count -gt 0) {
        throw "В скачанной сборке нет: $($missing -join ', ')"
    }

    # Пустая библиотека норм - самая тихая поломка: нормоконтроль отвечает "нарушений
    # нет" вместо "проверять нечем". Спрашиваем базу до подмены, пока откатывать нечего.
    $server = Join-Path $Staged "mcp-server"
    $script = "const D=require('better-sqlite3');" +
        "console.log(new D(process.argv[1],{readonly:true}).prepare('SELECT COUNT(*) c FROM norm_rules').get().c);"
    Push-Location $server
    try { $output = & node -e $script (Join-Path $server "revit-data.db") 2>$null }
    finally { Pop-Location }

    # node может дописать в stdout предупреждение - число всегда последней строкой.
    $rules = @($output) | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1
    if (-not $rules -or [int]$rules -lt 1) {
        throw "В скачанной базе норм нет правил - ставить её значит выключить нормоконтроль молча"
    }
    Write-Log "Проверка сборки пройдена (правил норм: $rules)"
}

# ── Подготовка и восстановление ──────────────────────────────────────────────

function Get-InstalledState {
    param([string]$RevitVersion)
    $path = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion\revit_mcp_plugin\version.json"
    if (-not (Test-Path $path)) { return $null }
    try { return Read-JsonFile $path } catch { return $null }
}

<#
.SYNOPSIS
    Качает и распаковывает всё нужное, пока Revit ещё открыт.
.DESCRIPTION
    Подменять папку при живом Revit нельзя, а качать - можно: файлов аддина загрузка не
    касается. Разница решающая. Если ждать закрытия Revit и только потом начинать качать
    сто семьдесят мегабайт, установка растягивается на минуты - а окно у архитектора
    бывает длиной в "закрыл Revit и выключил компьютер". После этой подготовки установка
    сводится к переносу готового дерева и укладывается в секунды.
#>
function Invoke-Prefetch {
    param($Manifest, $Release, [string]$Token, [string]$CacheDir, [string[]]$Years)

    $wanted = @()
    foreach ($year in $Years) {
        $pluginInfo = Get-JsonProperty $Manifest.plugins $year
        if (-not $pluginInfo) { continue }
        $state = Get-InstalledState $year
        if ((Get-JsonProperty $state 'version') -eq $Manifest.version) { continue }
        $wanted += [pscustomobject]@{ Year = $year; Plugin = $pluginInfo; State = $state }
    }

    if ($wanted.Count -eq 0) { return $false }

    foreach ($item in $wanted) {
        Get-VerifiedAsset -Release $Release -Info $item.Plugin -Token $Token -CacheDir $CacheDir | Out-Null
    }

    # Рантайм тянем только если он менялся: иначе установка возьмёт уже лежащий на диске.
    $runtimeSha = $Manifest.runtime.sha256
    $needRuntime = $false
    foreach ($item in $wanted) {
        if ((Get-JsonProperty $item.State 'runtimeSha256') -ne $runtimeSha) { $needRuntime = $true }
    }

    if ($needRuntime) {
        $runtimeCache = Join-Path $CacheDir ("runtime-" + $runtimeSha.Substring(0, 12))
        if (-not (Test-Path (Join-Path $runtimeCache ".complete"))) {
            Remove-Tree $runtimeCache
            $zip = Get-VerifiedAsset -Release $Release -Info $Manifest.runtime -Token $Token -CacheDir $CacheDir
            Expand-Zip -ZipPath $zip -Destination $runtimeCache
            New-Item -ItemType File -Force -Path (Join-Path $runtimeCache ".complete") | Out-Null
        }
    }

    Write-Log ("Готово к установке: $($Manifest.version) для Revit " + (($wanted | ForEach-Object { $_.Year }) -join ', '))
    return $true
}

<#
.SYNOPSIS
    Возвращает папку аддина, если прошлая подмена оборвалась на середине.
.DESCRIPTION
    Между двумя переименованиями есть доля секунды, когда рабочей папки уже нет, а рядом
    лежит только резервная копия. Выключение питания ровно в этот момент оставляет
    архитектора без плагина - Revit просто не покажет вкладку, и само это не починится.
#>
function Restore-InterruptedSwap {
    param([string]$AddinsRoot, [string]$Installed)

    if (Test-Path $Installed) { return }
    $backup = Get-ChildItem $AddinsRoot -Directory -Filter "revit_mcp_plugin.bak-*" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $backup) { return }

    Write-Log "Папки аддина нет, а резервная копия есть - прошлая подмена оборвалась. Возвращаю $($backup.Name)" WARN
    Move-DirectoryWithRetry -Path $backup.FullName -Destination $Installed
}

# ── Установка одной версии Revit ─────────────────────────────────────────────

function Install-RevitVersion {
    param(
        [string]$RevitVersion,
        $Manifest,
        $Release,
        [string]$Token,
        [string]$CacheDir,
        [string]$StagingDir,
        [bool]$ForceInstall
    )

    $addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
    $installed = Join-Path $addinsRoot "revit_mcp_plugin"

    $pluginInfo = Get-JsonProperty $Manifest.plugins $RevitVersion
    if (-not $pluginInfo) {
        Write-Log "В релизе нет сборки под Revit $RevitVersion - пропускаю" WARN
        return $false
    }

    Restore-InterruptedSwap -AddinsRoot $addinsRoot -Installed $installed

    $state = Get-InstalledState $RevitVersion
    $currentVersion = Get-JsonProperty $state 'version'
    $script:Status.installed[$RevitVersion] = $currentVersion

    if ($currentVersion -eq $Manifest.version -and -not $ForceInstall) {
        Write-Log "Revit ${RevitVersion}: уже стоит $currentVersion"
        return $false
    }

    Write-Log "Revit ${RevitVersion}: $(if ($currentVersion) { $currentVersion } else { 'ничего не установлено' }) -> $($Manifest.version)"

    # ── подготовка дерева в staging ──
    $staged = Join-Path $StagingDir $RevitVersion
    Remove-Tree $staged
    New-Item -ItemType Directory -Force -Path $staged | Out-Null

    $pluginZip = Get-VerifiedAsset -Release $Release -Info $pluginInfo -Token $Token -CacheDir $CacheDir
    Expand-Zip -ZipPath $pluginZip -Destination $staged

    $stagedPlugin = Join-Path $staged "revit_mcp_plugin"
    if (-not (Test-Path $stagedPlugin)) {
        throw "В $($pluginInfo.asset) нет папки revit_mcp_plugin"
    }

    # Рантайм одинаков для всех версий Revit и весит больше сотни мегабайт. Когда менялся
    # только C#, его sha256 в манифесте совпадает с установленным - берём с диска.
    $runtimeSha = $Manifest.runtime.sha256
    $runtimeCache = Join-Path $CacheDir ("runtime-" + $runtimeSha.Substring(0, 12))
    $runtimeParts = @("assistant-bridge", "mcp-server", "assistant-bundle", "normatives")

    $reusable = ($null -ne (Get-JsonProperty $state 'runtimeSha256')) -and
                ((Get-JsonProperty $state 'runtimeSha256') -eq $runtimeSha)
    if ($reusable) {
        foreach ($part in $runtimeParts) {
            if (-not (Test-Path (Join-Path $installed $part))) { $reusable = $false; break }
        }
    }

    if ($reusable) {
        Write-Log "Рантайм не менялся - переношу установленный, без загрузки"
        foreach ($part in $runtimeParts) {
            Copy-Tree (Join-Path $installed $part) (Join-Path $stagedPlugin $part)
        }
    }
    else {
        if (-not (Test-Path (Join-Path $runtimeCache ".complete"))) {
            Remove-Tree $runtimeCache
            $runtimeZip = Get-VerifiedAsset -Release $Release -Info $Manifest.runtime -Token $Token -CacheDir $CacheDir
            Expand-Zip -ZipPath $runtimeZip -Destination $runtimeCache
            New-Item -ItemType File -Force -Path (Join-Path $runtimeCache ".complete") | Out-Null
        }
        foreach ($part in $runtimeParts) {
            Copy-Tree (Join-Path $runtimeCache $part) (Join-Path $stagedPlugin $part)
        }
    }

    Import-UserState -Installed $installed -Staged $stagedPlugin -RevitVersion $RevitVersion
    Test-StagedTree -Staged $stagedPlugin -RevitVersion $RevitVersion

    Write-JsonFile ([ordered]@{
        version      = $Manifest.version
        runtimeSha256 = $runtimeSha
        installedAt  = (Get-Date).ToUniversalTime().ToString("o")
        source       = "github"
    }) (Join-Path $stagedPlugin "version.json")

    # ── подмена ──
    Stop-DeployedNode -AddinRoot $installed

    # Revit мог открыться, пока шла загрузка: проверка в начале запуска уже неактуальна.
    if (Get-Process Revit -ErrorAction SilentlyContinue) {
        throw "Revit открылся во время загрузки - установка отложена до следующего запуска"
    }

    New-Item -ItemType Directory -Force -Path $addinsRoot | Out-Null
    $backup = "$installed.bak-{0}" -f (Get-Date -Format "yyyyMMdd-HHmmss")
    $backedUp = $false

    if (Test-Path $installed) {
        Move-DirectoryWithRetry -Path $installed -Destination $backup
        $backedUp = $true
    }

    try {
        Move-DirectoryWithRetry -Path $stagedPlugin -Destination $installed
        foreach ($addin in Get-ChildItem $staged -Filter *.addin -File) {
            Copy-Item $addin.FullName $addinsRoot -Force
        }
    }
    catch {
        Write-Log "Подмена не удалась: $($_.Exception.Message). Возвращаю прежнюю версию" ERROR
        Remove-Tree $installed
        if ($backedUp) { Move-DirectoryWithRetry -Path $backup -Destination $installed }
        throw
    }

    Write-Log "Revit ${RevitVersion}: установлена $($Manifest.version)"
    $script:Status.installed[$RevitVersion] = $Manifest.version

    # Одна предыдущая версия остаётся под рукой на случай отката руками, остальные - мусор.
    $backups = Get-ChildItem $addinsRoot -Directory -Filter "revit_mcp_plugin.bak-*" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending
    foreach ($old in ($backups | Select-Object -Skip 1)) {
        Remove-Tree $old.FullName
    }

    Remove-Tree $staged
    return $true
}

# ── Основной ход ─────────────────────────────────────────────────────────────

$lock = $null
try {
    if (-not (Test-Path $ConfigPath)) {
        throw "Нет файла настроек $ConfigPath - запустите install-updater.ps1"
    }

    New-Item -ItemType Directory -Force -Path $script:Root | Out-Null
    try {
        $lock = [IO.File]::Open((Join-Path $script:Root "updater.lock"), 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch {
        Write-Log "Уже выполняется другой запуск - выхожу"
        exit 0
    }

    Write-Log "=== запуск обновления ==="

    $config = Read-JsonFile $ConfigPath
    $repo = Get-JsonProperty $config 'repo'
    if (-not $repo) { throw "В конфиге не указан repo" }
    $years = @(Get-JsonProperty $config 'revitVersions')
    if ($years.Count -eq 0) { throw "В конфиге не указан ни один revitVersions" }
    $allowPrerelease = [bool](Get-JsonProperty $config 'allowPrerelease')
    $token = Resolve-Token $config

    $release = Get-LatestReleaseWithRetry -Repo $repo -Token $token -AllowPrerelease $allowPrerelease
    $manifestAsset = $release.assets | Where-Object { $_.name -eq "manifest.json" } | Select-Object -First 1
    if (-not $manifestAsset) {
        throw "В релизе $($release.tag_name) нет manifest.json - он собран старым пайплайном"
    }

    $cacheDir = Join-Path $script:Root "cache"
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $manifestPath = Join-Path $cacheDir "manifest.json"
    Save-ReleaseAsset -Asset $manifestAsset -Token $token -Destination $manifestPath
    $manifest = Read-JsonFile $manifestPath

    $script:Status.available = $manifest.version
    Write-Log "Доступна версия $($manifest.version) (собрана $($manifest.published))"

    if ($CheckOnly) {
        Save-Status "checked" "Доступна $($manifest.version)"
        exit 0
    }

    # Нативный better-sqlite3 внутри рантайма собран под конкретный major Node. На другом
    # major он не грузится, и это видно только как молчащий нормоконтроль внутри Revit.
    $node = Get-NodeInfo
    if (-not $node) {
        throw "Node.js не найден. Поставьте Node $($manifest.minNode) или новее с nodejs.org"
    }
    if ($manifest.builtWithNode -match '^v(\d+)') {
        $builtMajor = [int]$Matches[1]
        if ($node.Major -ne $builtMajor) {
            throw "Сборка собрана на Node $($manifest.builtWithNode), а на машине $($node.Raw). Нужен Node $builtMajor.x - иначе нормоконтроль замолчит"
        }
    }

    # Раньше развилки: иначе при вечно открытом Revit апдейтер никогда не подтянул бы
    # собственное исправление.
    $selfUpdate = Get-JsonProperty $config 'selfUpdate'
    if ($null -eq $selfUpdate -or $selfUpdate) {
        $branch = Get-JsonProperty $config 'branch'
        if (-not $branch) { $branch = "main" }
        Sync-Self -Repo $repo -Token $token -Branch $branch -SelfPath $PSCommandPath
    }

    # Revit открыт - значит подменять папку нельзя, но скачать и распаковать можно уже
    # сейчас. Тогда установка при следующем закрытии Revit займёт секунды, а не минуты:
    # у архитектора, который вечером выходит из Revit и сразу выключает компьютер,
    # длинного окна не бывает.
    if (Get-Process Revit -ErrorAction SilentlyContinue) {
        $prefetched = Invoke-Prefetch -Manifest $manifest -Release $release -Token $token `
            -CacheDir $cacheDir -Years $years
        if ($prefetched) {
            Write-Log "Revit запущен - установка отложена, всё скачано заранее"
            Save-Status "prefetched" "Скачана $($manifest.version), встанет при закрытии Revit"
        }
        else {
            Write-Log "Revit запущен, но ставить нечего - установлено то же, что в релизе"
            Save-Status "up-to-date" "Обновление не требовалось"
        }
        exit 0
    }

    $stagingDir = Join-Path $script:Root "staging"
    New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

    $changed = @()
    $failed = @()
    foreach ($year in $years) {
        try {
            if (Install-RevitVersion -RevitVersion $year -Manifest $manifest -Release $release `
                    -Token $token -CacheDir $cacheDir -StagingDir $stagingDir -ForceInstall $Force.IsPresent) {
                $changed += $year
            }
        }
        catch {
            Write-Log "Revit ${year}: $($_.Exception.Message)" ERROR
            $failed += $year
            # Неудачная попытка оставляет распакованное дерево на сотню мегабайт.
            try { Remove-Tree (Join-Path $stagingDir $year) } catch { }
        }
    }

    # Кэш нужен только чтобы пережить обрыв связи внутри одного обновления.
    Get-ChildItem $cacheDir -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } |
        ForEach-Object {
            try {
                if ($_.PSIsContainer) { Remove-Tree $_.FullName }
                else { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
            }
            catch { }
        }

    if ($failed.Count -gt 0) {
        Save-Status "error" ("Не установлено для: " + ($failed -join ", "))
        Write-Log "=== завершено с ошибками ===" ERROR
        exit 1
    }

    if ($changed.Count -gt 0) {
        Save-Status "updated" ("Установлена $($manifest.version) для: " + ($changed -join ", "))
        Write-Log "=== готово: $($manifest.version) ==="
    }
    else {
        Save-Status "up-to-date" "Обновление не требовалось"
        Write-Log "=== готово: всё актуально ==="
    }
    exit 0
}
catch {
    Write-Log $_.Exception.Message ERROR
    Save-Status "error" $_.Exception.Message
    exit 1
}
finally {
    if ($lock) { $lock.Dispose() }
}
