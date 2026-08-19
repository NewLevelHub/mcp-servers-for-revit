<#
.SYNOPSIS
    Поднимает версию, коммитит и ставит тег. Ничего не стирает и ничего не переформатирует.

.DESCRIPTION
    Прежняя версия начиналась с `git checkout main; git reset --hard; git pull` - то есть
    молча уничтожала всё незакоммиченное. 19.08.2026 это едва не стоило восемнадцати файлов
    незавершённой работы. Теперь скрипт ничего не сбрасывает: он отказывается работать в
    грязном дереве и объясняет, что сделать.

    Версию в package.json прежняя версия писала через ConvertFrom-Json/ConvertTo-Json, что
    переписывало весь файл: другие отступы, \u003e вместо > и \u0027 вместо апострофа. Diff
    на весь файл, а потом конфликт при слиянии на ровном месте. Здесь правится одна строка
    текстом, форматирование остаётся как было.

.EXAMPLE
    .\scripts\release.ps1 -Version 1.1.4
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Выпустить с текущей ветки, а не с main. Только осознанно: релиз с ветки уедет без
    # того, что уже влито в main.
    [switch]$AllowAnyBranch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"

Push-Location $root
try {
    # ── Проверки, после которых уже ничего не жалко ──────────────────────────

    $dirty = @(git status --porcelain)
    if ($dirty.Count -gt 0) {
        Write-Host ""
        Write-Host "В дереве есть незакоммиченные изменения ($($dirty.Count)):" -ForegroundColor Yellow
        $dirty | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
        if ($dirty.Count -gt 10) { Write-Host "  ... и ещё $($dirty.Count - 10)" }
        Write-Host ""
        Write-Error "Релиз собирается из того, что лежит в репозитории на GitHub. Закоммитьте или спрячьте (git stash) изменения и повторите."
    }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main' -and -not $AllowAnyBranch) {
        Write-Error "Сейчас ветка '$branch'. Релизы выпускаются с main - выполните: git switch main. Если это осознанно, добавьте -AllowAnyBranch."
    }

    if (@(git tag --list $tag).Count -gt 0) {
        Write-Error "Тег $tag уже существует локально. Выберите другую версию."
    }
    if (@(git ls-remote --tags origin "refs/tags/$tag").Count -gt 0) {
        Write-Error "Тег $tag уже есть на GitHub. Выберите другую версию."
    }

    # Отстающая ветка даст релиз без чужих изменений, а догонять придётся потом.
    git fetch origin --quiet

    # Сначала rev-parse: у только что созданной ветки нет origin/..., и rev-list написал бы
    # в поток ошибок, а PowerShell 5.1 при ErrorActionPreference=Stop делает из первой же
    # такой строки сбой - релиз падал бы на пустом месте.
    $upstream = (git rev-parse --verify --quiet "refs/remotes/origin/$branch")
    if ($upstream) {
        $behind = (git rev-list --count "HEAD..origin/$branch")
        if ("$behind" -match '^\d+$' -and [int]$behind -gt 0) {
            Write-Error "Ветка отстаёт от origin/$branch на $behind коммит(ов). Сначала выполните: git pull --ff-only"
        }
    }

    # ── Правка версии: по одной строке в каждом файле ────────────────────────

    # Latin-1 отображает байт в символ один в один, поэтому файл переживает чтение и
    # запись без изменений - включая BOM и то, что в нём вообще не UTF-8.
    # AssemblyInfo.cs именно такой: в нём пять байт в старой кодировке (проект родом из
    # Китая), и чтение как UTF-8 их уничтожало - в diff появлялись "изменённые" строки,
    # которые выглядят точно так же, как были.
    $byteSafe = [Text.Encoding]::GetEncoding(28591)

    function Set-VersionLine {
        param(
            [string]$Path,
            [string]$Pattern,
            [string]$Replacement,
            [int]$ExpectedHits = 1,
            [int]$Limit = [int]::MaxValue,
            $Encoding
        )

        $text = $Encoding.GetString([IO.File]::ReadAllBytes($Path))
        $hits = ([regex]::Matches($text, $Pattern)).Count
        if ($hits -lt $ExpectedHits) {
            Write-Error "$Path : не нашёл строку с версией (шаблон $Pattern)"
        }
        # Regex, а не разбор JSON: разбор переписал бы весь файл своим форматированием.
        $updated = [regex]::new($Pattern).Replace($text, $Replacement, $Limit)
        [IO.File]::WriteAllBytes($Path, $Encoding.GetBytes($updated))
    }

    $pkg = Join-Path $root "server/package.json"
    $lock = Join-Path $root "server/package-lock.json"
    $asm = Join-Path $root "plugin/Properties/AssemblyInfo.cs"
    $versionPattern = '("version":\s*")\d+\.\d+\.\d+(")'

    # Только первое вхождение: дальше в файле идут версии зависимостей.
    Set-VersionLine -Path $pkg -Pattern $versionPattern -Replacement "`${1}$Version`${2}" `
        -Limit 1 -Encoding $byteSafe

    # В lock-файле версия пакета стоит дважды - в корне и в packages[""], дальше идут
    # зависимости, которые трогать нельзя.
    Set-VersionLine -Path $lock -Pattern $versionPattern -Replacement "`${1}$Version`${2}" `
        -ExpectedHits 2 -Limit 2 -Encoding $byteSafe

    Set-VersionLine -Path $asm -Pattern '(Assembly(?:File)?Version\(")\d+\.\d+\.\d+\.0(")' `
        -Replacement "`${1}$Version.0`${2}" -ExpectedHits 2 -Encoding $byteSafe

    Write-Host "версия -> $Version" -ForegroundColor Green
    Write-Host "  server/package.json"
    Write-Host "  server/package-lock.json"
    Write-Host "  plugin/Properties/AssemblyInfo.cs"

    # ── Правка не должна была задеть ничего лишнего ──────────────────────────

    $touched = @(git diff --name-only)
    $expected = @('server/package.json', 'server/package-lock.json', 'plugin/Properties/AssemblyInfo.cs')
    $unexpected = $touched | Where-Object { $expected -notcontains $_ }
    if ($unexpected) {
        git checkout -- $touched
        Write-Error "Правка задела лишние файлы ($($unexpected -join ', ')). Изменения откачены, релиз не выпущен."
    }

    $lines = @(git diff --numstat) | ForEach-Object { ($_ -split "`t")[0] } | Measure-Object -Sum
    if ($lines.Sum -gt 12) {
        git checkout -- $touched
        Write-Error "Правка версии изменила $($lines.Sum) строк - это не похоже на подмену номера. Изменения откачены."
    }

    # ── Коммит и тег ─────────────────────────────────────────────────────────

    git add $expected
    git commit -m $Version
    if ($LASTEXITCODE -ne 0) { Write-Error "Коммит не прошёл." }
    git tag $tag
    if ($LASTEXITCODE -ne 0) { Write-Error "Не удалось поставить тег $tag." }

    Write-Host ""
    Write-Host "Готово: коммит $Version и тег $tag." -ForegroundColor Green
    Write-Host "Сборка запустится после отправки:" -ForegroundColor Yellow
    Write-Host "  git push origin $branch"
    Write-Host "  git push origin $tag"
    Write-Host ""
    Write-Host "Отменить, пока не отправлено:  git tag -d $tag; git reset --hard HEAD~1" -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
