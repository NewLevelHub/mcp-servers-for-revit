# Настройка Revit MCP для проектировщика (без разработки)
# Запуск: PowerShell → правый клик «Выполнить» или: .\scripts\setup-user.ps1

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Example = Join-Path $RepoRoot ".cursor\mcp.json.example"
$Target = Join-Path $RepoRoot ".cursor\mcp.json"

Write-Host ""
Write-Host "=== Настройка Revit MCP для Cursor ===" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $Example)) {
    Write-Host "Ошибка: не найден $Example" -ForegroundColor Red
    exit 1
}

# Пути в конфиге абсолютные, поэтому подставляем реальный корень репозитория,
# а не копируем шаблон как есть — иначе MCP не стартует на чужой машине.
$RepoRootForJson = $RepoRoot.Replace("\", "/")
$Config = (Get-Content $Example -Raw).Replace("<REPO_ROOT>", $RepoRootForJson)

if (Test-Path $Target) {
    Write-Host "Файл уже существует: $Target" -ForegroundColor Yellow
    $answer = Read-Host "Перезаписать? (y/n)"
    if ($answer -ne "y") {
        Write-Host "Пропуск копирования mcp.json"
    } else {
        Set-Content -Path $Target -Value $Config -Encoding utf8
        Write-Host "Обновлён: $Target" -ForegroundColor Green
    }
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path $Target) | Out-Null
    Set-Content -Path $Target -Value $Config -Encoding utf8
    Write-Host "Создан: $Target" -ForegroundColor Green
}

Write-Host "  путь к серверу: $RepoRootForJson/server/build/index.js" -ForegroundColor DarkGray

Write-Host ""
Write-Host "Дальше вручную:" -ForegroundColor Cyan
Write-Host "  1. Установите Node.js 18+ (nodejs.org)"
Write-Host "  2. Установите плагин Revit из Releases в %AppData%\Autodesk\Revit\Addins\<версия>\"
Write-Host "  3. Revit: Open Server + Settings (все команды) + Save"
Write-Host "  4. Cursor: откройте папку $RepoRoot"
Write-Host "  5. Перезапустите Cursor"
Write-Host "  6. В чате: «Проверь связь с Revit по правилам revit-mcp»"
Write-Host ""
Write-Host "Руководство: docs\user-guide-revit-mcp.md" -ForegroundColor Green
Write-Host ""
