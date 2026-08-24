# Tool registry — MCP ↔ Revit command contract

Single source of truth for **names and ownership**. When adding a capability, update this doc (and keep `scripts/check-tool-registry.mjs` green).

## Layers

| Layer | Path | Role |
|-------|------|------|
| MCP tools | `server/src/tools/*.ts` | Zod schema + AI-facing name; often wraps / orchestrates |
| Revit commands | root `command.json` + `commandset/Commands/` | JSON-RPC method executed in Revit |
| In-Revit assistant | `plugin/Core/Assistant/ToolCatalog.cs` | Curated subset + RU labels for the dockable chat |

```
Cursor / MCP client
  → server tool (MCP name)
    → sendCommand(Revit command name)   // may differ (aliases)
      → plugin CommandExecutor
        → commandset *Command / *EventHandler
```

Some MCP tools never call Revit (norm library). Some Revit commands are **internal** (no public MCP tool).

## Profiles (`MCP_TOOL_PROFILE`)

| Profile | Behavior |
|---------|----------|
| `default` (unset) | All tools with a `register*` export **except** `DEFAULT_DENYLIST` |
| `lite` | Same set registered, but only `LITE_TOOLS` (22 tools) is **listed**; the rest are hidden |
| `lite+<groups>` | `LITE_TOOLS` plus the named groups, e.g. `lite+sheets,annotation` (REV-41) |
| `norms` | Only `extract_norm_rules_from_pdf`, `query_norm_rules`, `save_norm_rule` |
| `full` | Everything including legacy SQLite helpers |

### `lite`, tool groups and per-turn switching (REV-157, REV-41)

The full catalog serialises to ~225 KB of JSON schema — about 59k tokens the
model reads before writing its first character, on **every** turn. That is the
main reason a one-line question took ~12 s in the in-Revit chat.

`lite` lists the everyday set and hides the rest. `lite+<groups>` adds back only
what a task needs. The set and the group map are in
`server/src/utils/toolCatalog.ts`; `toolCatalog.test.ts` fails if a new tool file
is in neither, so nothing can go missing silently.

| Group | Contents |
|-------|----------|
| `norms` | the `check_*` family, `run_norm_audit`, `apply_norm_result`, the rule library, and the geometry readers they feed on |
| `quality` | `get_model_warnings`, `check_sheet_readiness` — model health before issue |
| `schedules` | schedules and ведомости, `validate_schedule`, bulk data export |
| `sheets` | sheets, title blocks, view placement, auto-layout, ТЭП table |
| `annotation` | dimensions, tags, text notes, filled regions, node details |
| `cad` | `get_cad_link_geometry` + `trace_*_from_cad` |
| `links` | `get_linked_models`, `check_link_clashes` — связи смежников и коллизии с ними |
| `changes` | `create_model_snapshot` — снимок выдачи; `compare_model_versions` — сравнение снимка с текущим состоянием (или с другим снимком), человеческим языком; `create_revision_clouds` — облака изменений из этого diff'а, кластерами, с попаданием в таблицу ревизий |
| `modeling` | grids, stairs, railings, floor openings, framing, family loading |
| `advanced` | `send_code_to_revit`, `say_hello` |

Measured on this build (`tools/list` over stdio, ~3.8 B/token), 21.08.2026:

| Profile | Tools | Bytes | ≈ tokens |
|---------|------:|------:|---------:|
| `default` | 99 | 224 608 | 59 107 |
| `lite` | 22 | 34 866 | 9 175 |
| `lite+quality` | 24 | 37 932 | 9 982 |
| `lite+changes` | 23 | 40 199 | 10 579 |
| `lite+links` | 26 | 48 436 | 12 746 |
| `lite+cad` | 26 | 52 401 | 13 790 |
| `lite+sheets` | 29 | 53 811 | 14 161 |
| `lite+modeling` | 31 | 60 742 | 15 985 |
| `lite+annotation` | 35 | 66 931 | 17 613 |
| `lite+schedules` | 36 | 67 045 | 17 643 |
| `lite+norms` | 43 | 74 604 | 19 633 |
| `lite+sheets,annotation` | 42 | 85 876 | 22 599 |
| `lite+all` | 99 | 224 608 | 59 107 |

The previous figures in this table were taken at 92 tools and had drifted by
seven; re-measure with `tools/list` when adding a tool rather than adjusting a
row by hand.

An unknown group name is logged and ignored, never fatal — a typo in an env var
must not cost the Revit connection.

The assistant-bridge picks the profile **per turn** and passes it through
`agent.send(message, { mcpServers })` — `pickToolProfile` in `agent-session.ts`:

- everyday question or edit → `lite`;
- heavy request whose wording names what it needs → `lite+<groups>`, mapped by
  `HINT_TOOL_GROUPS` (e.g. "проставь размеры" → `lite+annotation`);
- heavy request with nothing to go on (an image, a long brief) → `default`.

The conversation and its history stay on the same agent across the switch.

#### The profile is fixed when the connection opens

Verified against Cursor SDK 1.0.24 on a live model (REV-157), and re-confirmed
2026-08-18 against the clients' own issue trackers:

- Per-send `mcpServers` **works** — a turn asking for `get_cad_link_geometry`
  reached it in a session whose previous turns ran on `lite`.
- Runtime unhiding does **not** work, in any client. `RegisteredTool.enable()`
  does emit `notifications/tools/list_changed`, but no current client acts on it:
  Cursor IDE and CLI and Claude Code all snapshot the catalog at session start.
  The newly enabled tool comes back as `not found` / `disabled` for the rest of
  that session.

So **do not add an `expand_toolset`-style escalation tool.** One was written and
removed on 2026-08-18 for exactly this reason; the group profiles above are the
working form of the same idea. Re-test the client behaviour before trying again:

- [Cursor — list_changed not acted on mid-session](https://forum.cursor.com/t/mcp-notifications-tools-list-changed-not-acted-on-mid-session/161459)
- [claude-code#13646 — tool list not refreshed on list_changed](https://github.com/anthropics/claude-code/issues/13646)

Cursor IDE and any other client keep the `default` profile unless they set the
env var — and they should, until the escalation limit above is lifted: with a
static catalog, a hidden tool stays unreachable for the whole session.

## Model health before issue (REV-47)

Two tools answer "is this ready to go out", which is a different question from
"does it meet СП/ГОСТ" — hence their own `quality` group rather than `norms`.

| Tool | Revit command | What it reads |
|------|---------------|---------------|
| `get_model_warnings` | `get_model_warnings` | `Document.GetWarnings()` — the «Просмотр предупреждений» list, folded by warning text, biggest group first |
| `check_sheet_readiness` | *(server-only)* | sheets via `ai_element_filter` + `get_elements_parameters`: blank штамп lines, missing/duplicate sheet numbers, blank sheet names |

Both are read-only and open no transaction.

`check_sheet_readiness` shares `SHEET_FIELD_ALIASES` with `fill_title_block`, so a
штамп this can check is one that can be filled — hand its output straight to
`fill_title_block`. It reports `field_absent` separately from `empty_field`:
the first means the title block has no such line (wrong template), the second
means nobody typed a name. Telling an architect to "fill in Н.контроль" on a
штамп without that line wastes their time.

A sheet whose parameters cannot be read is listed under `unreadableSheets` and
left ungraded rather than reported as "everything blank".

**Not covered yet:** views that sit on no sheet, and schedules with zero rows.
Both need a new Revit read; `check_sheet_readiness` deliberately stayed
server-only so it ships with a server update and no plugin reinstall.

### Legacy / full-only (keep files, not in default)

| MCP tool | Notes |
|----------|-------|
| `store_room_data` | Local SQLite room metadata |
| `store_project_data` | Local SQLite project metadata |
| `query_stored_data` | Query local SQLite store |

Listed in `DEFAULT_DENYLIST` in `server/src/tools/register.ts`. Use only with `MCP_TOOL_PROFILE=full`.

Empty stub files (`modify_element`, `search_modules`, `use_module`) were removed — do not reintroduce without an implementation.

## Связанные модели смежников (REV-166, REV-167)

| Tool | Revit command | What it reads |
|------|---------------|---------------|
| `get_linked_models` | `get_linked_models` | `RevitLinkInstance` списком: имя файла, раздел по имени, статус загрузки, число элементов и `GetTotalTransform()` — положение связи **в наших координатах** |
| `check_link_clashes` | `check_link_clashes` | пересечения наших стен/перекрытий/проёмов с балками, колоннами, воздуховодами и трубами связи: обе стороны с `ElementId`, уровень, помещение, точка в наших координатах и глубина захода в мм |
| `create_mep_openings` | `create_mep_openings` | задание на отверстия: где трубы и воздуховоды связи проходят сквозь наши стены и плиты, какого размера нужен проём, марка и отметка низа; по подтверждению — сами проёмы |
| `check_shared_site` | — (считает по `get_linked_models`) | сверка общей площадки: уровни, оси, базовая точка и точка съёмки, способ вставки связи |

Первый инструмент эпика «Смежники»: всё, что придёт после (коллизии, задание на
отверстия, сверка площадки), читает связи через него.

**Только чтение, в обе стороны.** Транзакция не открывается, и в саму связь не
пишется ничего: файл принадлежит другой организации. Это сказано в описании
инструмента, потому что модель решает по описанию.

**Координаты.** Связь, вставленная не в 0,0, хранит свои элементы в своих числах.
`GetTotalTransform()` — единственный правильный мост; `placement` отдаёт начало
координат связи в мм, поворот и зеркальность, а `coordinateSamples` показывает
элемент сразу в двух системах (`linkPointMm` и `hostPointMm`), чтобы смещение
можно было сверить с моделью, а не принять на веру.

**Цена обхода.** По умолчанию проход дешёвый: статус, трансформация и
`GetElementCount()`, который не материализует ни одного элемента. Дорогое —
`includeCategories` (обходит каждый элемент каждой связи) — выключено, а
`levelName` режет счёт до одного этажа. Каждая связь измеряется отдельно
(`elapsedMs`), общее время — в корне ответа. Замеры и порядок их снятия:
[performance.md](performance.md).

**Выгруженная и битая связь — это ответ, а не сбой.** `GetLinkDocument()` отдаёт
null для выгруженной и ничего для отсутствующей; каждая связь читается в своём
try/catch и попадает в список со статусом (`Загружена` / `Выгружена` /
`Файл не найден` / `Ссылка повреждена`) и пояснением. Одна перепривязанная связь
субподрядчика не должна стоить архитектору всего списка.

`get_linked_models` и `get_cad_link_geometry` — пара в `TOOL_TWINS`: оба «читают
связь», но первый про связанные `.rvt`, второй про DWG-подложку. Описания
ссылаются друг на друга, `check:tool-registry` следит, чтобы ссылки не пропали.

### Коллизии со связями (REV-167)

`check_link_clashes` отвечает на вопрос «я поменял планировку — что теперь
бьётся», а не заменяет Navisworks. Строка отчёта несёт `ElementId` обеих сторон,
поэтому `operate_element` подсвечивает наш элемент прямо из отчёта, а смежнику
называется id в его файле.

**Как считается.** Солид нашего элемента один раз переводится в координаты связи
через `GetTotalTransform().Inverse`, и поиск идёт внутри связи — иначе пришлось
бы тащить тысячи чужих солидов к нам. `ElementIntersectsSolidFilter` — медленный
фильтр (Revit применяет его поэлементно), поэтому он всегда идёт в паре с
`BoundingBoxIntersectsFilter` по тому же солиду: быстрый фильтр отрабатывает по
пространственному индексу и отдаёт медленному единицы кандидатов.

**Глубина и порог.** Фильтр отвечает «пересекаются», но не «насколько». Тело
пересечения считается булевой операцией, и наименьшая сторона его габарита —
это `depthMm`. Балка через стену 200 мм даёт 200, касание — 1, и порог
`toleranceMm` (по умолчанию 5 мм) режет именно по этому числу. Отброшенные
касания считаются в `ignoredBelowTolerance`, а не исчезают молча.

**Одна коллизия — одна строка.** Стена здесь смоделирована пачкой отдельных
элементов: бетон 500, две минваты, штукатурка, отделка. Балка сквозь неё даёт
пять пересечений, и на живом прогоне четыре тестовые балки дали 18 строк. Поэтому
перекрытия одного элемента связи в одном месте (радиус 1,5 м) сворачиваются в одну
строку: самое глубокое становится строкой — это несущий слой, о котором и спор со
смежником, — а остальные уходят в `alsoHits` со своими id и глубинами. Ничего не
теряется: `rawClashCount` показывает число до свёртки, `mergeLayers: false` возвращает
построчный вид. Одна и та же балка, кусающая две разные стены в шести метрах друг от
друга, остаётся двумя строками.

**Чего не измерили — не выбрасываем.** Булева операция падает на
самопересекающейся геометрии, обычной в файле субподрядчика. Такая коллизия
попадает в отчёт без глубины и с пояснением: фильтр Revit её нашёл, значит она
есть, а тихо выброшенная коллизия — худший способ ошибиться.

**Лимиты — часть контракта.** Обход останавливается по `maxClashes` (500) или
`timeBudgetSeconds` (90 с, максимум 150) и выставляет `truncated`. `maxHostElements` (50000) —
предохранитель от разгона, а не рабочий предел: на обычной панельке в наших
категориях 10473 элемента, и прежний лимит 5000 резал модель пополам. Ожидание
команды в плагине — 180 с, сокет для неё в `HEAVY_COMMANDS`, чтобы частичный
список успел дойти до чата.

**Выгруженная связь — это ответ.** Каждая связь попадает в `links` со своим
`scanned` и пояснением. Молча пропущенная выгруженная связь КР читалась бы как
«коллизий нет» — единственный способ ошибиться, которого этот инструмент себе
позволить не может.

### Сверка общей площадки (REV-169)

`check_shared_site` отвечает на вопрос, который никто не задаёт вовремя: стоят ли
наша модель и связь смежника на одной разбивке. Уровни, оси, базовая точка и
точка съёмки, способ вставки связи.

Ошибка тут не болит в день, когда её сделали. Связь вставили чуть иначе, или у КР
этаж на 50 мм ниже — месяц всё выглядит нормально, а потом балки оказываются не
на своих отметках и отверстия не совпадают с трубами. Тридцать секунд в начале
против недели в конце.

**Данные читаются в собственных координатах каждой модели.** Если сначала
пересчитать связь в наши числа через `GetTotalTransform`, исчезнет ровно та
разница, которую проверка и ищет. Поэтому `get_linked_models` отдаёт `hostSite` и
`site` как есть, а сравнение живёт на сервере — заодно его тесты запускаются без
Revit, чего про остальной эпик сказать нельзя.

**Совпадение по имени важнее совпадения по отметке.** Иначе «2 этаж» спарится с
чужой «Антресолью» на той же высоте, и настоящая разница в 50 мм останется
незамеченной.

**Одна отметка под двумя именами — это находка, а не совпадение.** Наш «2 этаж» и
их «Уровень 2» на 3900 глазом не различить, но каждое обращение к уровню по имени
после этого промахивается.

**Ось могли начертить с другого конца.** Концы сравниваются в обе стороны, иначе
каждая вторая ось читалась бы как смещённая на длину самой оси.

**Связь без осей — одна строка, а не сорок.** Проверка, которая на здоровой модели
выдаёт три экрана, перестаёт запускаться; это требование из тикета, а не вкус.

### Задание на отверстия (REV-168)

`create_mep_openings` берёт те же пересечения, но отвечает на другой вопрос.
`check_link_clashes` говорит «здесь бьётся», а задание должно сказать «вот такой
прямоугольник» — поэтому тело пересечения не просто измеряется, а проецируется в
плоскость стены (или в план плиты), и прямоугольник читается уже там. Труба,
идущая под углом, получает тот более широкий проём, который ей действительно
нужен, и никому не приходится считать эллипс.

Вершины берутся с рёбер солида и проецируются на оси стены, а не через
`GetBoundingBox()`: тот отдаёт габарит в своей системе координат и на повёрнутой
стене намерил бы не тот прямоугольник.

**Сначала превью, всегда.** При `apply: false` (по умолчанию) транзакция не
открывается вообще. Отсутствующий аргумент не может быть прочитан как разрешение
резать дырки в чужой модели, поэтому вернуть план и дождаться подтверждения —
единственное поведение по умолчанию.

**Пачка труб — один проём.** Прямоугольники ближе `mergeGapMm` (200 мм)
сливаются, и слияние идёт до тех пор, пока что-то меняется: третья труба,
далёкая от первой, но близкая ко второй, попадает в тот же проём. Иначе в стене
остаются перемычки в палец толщиной, которые никто не станет выкладывать.

**Размер под стройку.** Замер плюс запас `clearanceMm` (50 мм на сторону, иначе
гильзу и уплотнение не вставить), округление вверх до `sizeStepMm` (50 мм) —
никто не режет отверстие 137 мм. Замер до округления остаётся в
`measuredWidthMm` / `measuredHeightMm`.

**Повторный прогон не плодит дубли.** Существующие проёмы читаются до всякой
транзакции и сопоставляются по хосту и положению; совпавшие возвращаются со
статусом `exists`. Свои проёмы инструмент узнаёт по марке `ОТВ-…` — сопоставлять
по любому размещённому семейству нельзя, иначе дверь рядом с планируемым
отверстием отменила бы его, и задание молча пришло бы на один проём короче.

**Без семейства проёма марки не будет.** С `openingTypeId` в стену ставится
семейство, которому проставляется марка — и тогда собирается ведомость
(`create_schedule` по категории семейства + `place_view_on_sheet`). Без него
режется обычный проём Revit: он держит геометрию, но не марку, и инструмент
пишет это в `note`, а не оставляет обнаружить пустую спеку.

### Задание переживает собственную реализацию (REV-186)

На живой приёмке 21.08.2026 вскрылось неочевидное: **прорезанное задание
исчезает**. Отверстия убирают материал, трассы связи перестают пересекать наши
стены, и повторный вызов честно отвечает «отверстия не нужны» на этаже, где их
только что нарезали.

Поэтому инструмент читает задание ещё и **из самой модели** — по марке `ОТВ-…`
на размещённых семействах. Такие строки возвращаются со статусом `exists`, и
задание можно показать через месяц, а ведомость собрать не пересканированием
связи, а по элементам.

Отсюда же следует, зачем на самом деле нужен `openingTypeId`: обычный проём
Revit марку не держит, а значит его не опознать при повторном чтении. Это
весомее, чем ведомость, ради которой параметр вводился.

**Чего не восстановить.** Для какой именно трассы резали отверстие — не
восстанавливается: эта связь жила в связанном файле, а не у нас. Строка
возвращается с размером, местом и маркой, а список `mepElementIds` — пустой, а
не выдуманный.

**Нумерация продолжается, а не начинается заново.** Новым отверстиям марки
раздаются с того номера, на котором остановилась модель — иначе на этаже
появились бы две «ОТВ-2эт-01».

## Снимок модели в базу (REV-170)

| Tool | Revit command | What it does |
|------|---------------|--------------|
| `create_model_snapshot` | `export_model_snapshot` (по страницам) | пишет состояние открытой модели в `~/.mcp-servers-for-revit/model-snapshots.db`: на элемент — `ElementId`, `UniqueId`, категория, тип, уровень, помещение, габарит и хеш ключевых параметров. Он же перечисляет (`action: "list"`) и удаляет (`action: "delete"`) снимки |

Первый тикет эпика «Что изменилось». `compare_model_versions` (REV-171),
облака изменений и таблица ревизий читают снимки отсюда — своих чтений модели у
них не будет.

**В Revit ничего не пишется.** Транзакция не открывается: это чтение модели и
запись в нашу базу. Единственное, что инструмент меняет, — содержимое
`model-snapshots.db`.

**Почему БД, а не файл.** Каталог ленты (REV-151) лёг файлом: он маленький и
читается целиком. Здесь наоборот — сотни тысяч строк, несколько снимков одной
модели рядом и запросы вида «что есть в этом снимке, чего нет в прошлом». Это
работа для SQLite, которая в сервере уже подключена. Решение зафиксировано в
тикете, чтобы не обсуждать его заново на ревью.

**Страницами, а не одним ответом.** 300 000 элементов не проходят в кадр сокета
(предел 50 МБ). Сервер просит страницу (`batchSize`, по умолчанию 5000), пишет её
в базу одной транзакцией и просит следующую. Отсюда же и «писать пачками» из
тикета: транзакция на строку заставила бы SQLite синхронизироваться на каждом
элементе.

**Список элементов строится один раз.** Решить, что попадает в снимок, нельзя без
категории элемента, а категорию нельзя прочитать, не материализовав элемент, —
значит, фильтр это полный проход по модели. Он делается на первой странице,
сортируется по id (чтобы страницы не наезжали и не пропускали) и кэшируется в
обработчике под `snapshotToken`. Если токен на следующей странице другой — модель
правили во время снятия; сервер останавливается, помечает снимок `partial` и
говорит об этом, вместо того чтобы сшить два разных состояния в один снимок.

**Ключи стабильные, а не локализованные.** Категория едет как
`categoryKey` (`OST_Walls`) рядом с локализованным `category` («Стены»), а
параметры — под именами `BuiltInParameter` (`ALL_MODEL_MARK`), а не под «Марка».
Revit на тестовой машине запускается то по-русски, то по-английски
(см. `docs/` и историю REV-165): снимок, сделанный на локализованных ключах,
сравнился бы с вчерашним как «переписана вся модель». Локализованные подписи
едут отдельно, один раз на страницу, — отчёту есть что показать архитектору.

**Что считается изменением.** Хеш берётся от ключевого набора параметров:
марка и комментарии, стадия создания и сноса, уровень и смещения, конструктивные
привязки стен, длина/площадь/объём, имя-номер-площадь помещения. Набор
фиксированный и короткий — обход `Element.Parameters` на 300k элементов это
десятки миллионов чтений значений. Свои параметры организации добавляются по
имени через `extraParameters`.

Правила хеша живут в `server/src/utils/modelSnapshot.ts` и проверяются
`modelSnapshot.test.ts` без Revit: значение изменилось — хеш изменился, порядок
параметров поменялся — не изменился, шум округления ниже 1e-6 не изменение,
пустой параметр и отсутствующий — одно и то же.

**Повторный запуск не дублирует записи.** Два замка: снимок уникален по паре
(модель, имя) — повторная «выдача АР 19.08.2026» замещает прежнюю, а не ложится
рядом; элемент уникален по паре (снимок, `UniqueId`) — повторно пришедшая
страница переписывает свои строки. Хранится `keepPerModel` снимков одной модели
(по умолчанию 5), старшие удаляются после **успешного** снятия нового — после
оборвавшегося не удаляется ничего.

**Неполный снимок отвечает ошибкой.** `success: false` и `status: "partial"`,
то есть `normalizeToolResult` поднимает `isError`. Снимок, в котором не хватает
половины модели, при сравнении читается как «всё это добавили с прошлого раза»;
молча отдать его за готовую выдачу — ровно та болезнь, из-за которой в тулах
появился `isError`.

`create_model_snapshot` и `analyze_model_statistics` — пара в `TOOL_TWINS`. Оба
обходят всю модель и рассказывают, что в ней; но первый стоит минуты и остаётся
в базе, а второй отвечает за секунды и не хранит ничего. Взять дешёвый, чтобы
«записать выдачу», значит потерять сравнение совсем — прошлое состояние потом
неоткуда взять.

**Что в снимок не идёт.** Аннотации и элементы вида (`includeAnnotation`), и
служебные категории (`includeServiceCategories`): эскизные линии перекрытий и
лестниц, материалы, наборы характеристик, листы, компоненты легенды, камеры,
траектория солнца. На «Коротком блоке» это 10 325 строк из 27 162 — 38 % снимка,
из них 9 576 одних только `OST_SketchLines`. Дело не в объёме: эскизные линии —
внутренности перекрытия, и правка одной плиты пришла бы как тридцать изменённых
линий вместо одного изменённого перекрытия, площадь и объём которого снимок и так
пишет. Отсечение узкое: разделители помещений, границы зон и базовые точки
остаются — это либо замысел, либо то, что нельзя пропустить.

**Уровень есть не у всех, и это состояние модели.** На «Коротком блоке» 25 %
элементов снимка без уровня, из них 3900 балок. Проверено в самой модели: у них
нет ни `LevelId`, ни «Опорного уровня», ни одного параметра, указывающего на
`Level`. Снимок читает уровень через `LevelId`, потом через именованные
параметры, потом просматривает все параметры элемента в поисках ссылки на
`Level` — и, не найдя, оставляет пусто, а не выдумывает этаж. Группировать такие
элементы REV-171 может по Z габарита, который в снимке есть.

**Снимки лежат не в `revit-data.db`, и это сделано намеренно.**
Файл базы — `~/.mcp-servers-for-revit/model-snapshots.db`
(`server/src/database/snapshotDb.ts`), переопределяется переменной
`REVIT_MCP_SNAPSHOT_DB`.

Тикет называл `server/revit-data.db`, и половина того решения в силе: SQLite, а
не файл в профиле. Но конкретно **этот** файл — seed библиотеки норм: он
собирается в репозитории и везётся со сборкой, а `deploy-local`
(`Deploy-NormLibrary` в `build-assistant-cursor.ps1`) и авто-апдейтер
(`Update-RevitMcp.ps1`) **заменяют его целиком**, оставляя прежний лишь как
`revit-data.db.bak`. Снимки там прожили бы до первого обновления — и «что
изменилось с прошлой выдачи» осталось бы без второй половины сравнения.

Соседний файл внутри `mcp-server/` не спас бы тоже: апдейтер собирает дерево
заново и подменяет целиком, перенося из старой установки только `Logs` и
настройки. Поэтому снимки уехали в профиль пользователя — туда же, где живут
каталог ленты (REV-151) и JSONL-метрики, и куда релизный тракт не заглядывает.
Снимок, снятый до обновления, есть и после него, и после переустановки, и после
перехода на другую версию Revit.

У двух баз противоположные сроки жизни: библиотека норм приезжает со сборкой и
свободно заменяется, снимки принадлежат архитектору. `snapshots.test.ts`
проверяет, что путь не ведёт ни в `revit-data.db`, ни внутрь `mcp-server/`.

**Чего пока нет.** В режиме openai панель этот инструмент не видит: каталог там
пишется руками в `plugin/Core/Assistant/ToolCatalog.cs`. Это тот же пробел, что
у эпика «Смежники», и закрывается тем же тикетом (`links-openai-catalog`) —
добавляя туда связи, добавьте и `create_model_snapshot` (и `compare_model_versions`
с `create_revision_clouds`, REV-171/172 — тот же пробел, ещё не закрыт). В режиме
cursor инструмент виден сразу: сервер находит его сканированием папки.

Замеры времени снятия — [performance.md](performance.md).

## Сравнение версий модели (REV-171)

| Tool | Revit command | What it does |
|------|---------------|--------------|
| `compare_model_versions` | `export_model_snapshot` (только для "текущего состояния"; не нужен, если сравниваются два снимка) | Диффит два снимка из `create_model_snapshot`, либо снимок и открытую сейчас модель. Отвечает сводкой сверху и списком, сгруппированным по уровням и помещениям, с пагинацией |

Второй тикет эпика «Что изменилось» — стоит на `create_model_snapshot` (REV-170)
и ничего своего в базу не пишет. Облака изменений и таблица ревизий (REV-172)
строятся прямо на этом diff'е — каждое изменение несёт `location` (центр
габарита, мм) именно для этого.

**Пары ищутся по `UniqueId`, не по геометрии.** Это то же самое ключевание, на
котором стоит снимок: Revit не меняет `UniqueId`, пока элемент не удалён и не
создан заново, поэтому переставленная стена сама попадает в «есть в обоих
снимках» и обходится без геометрического мэтчинга. Смещение центра bounding box
дальше `moveToleranceMm` (по умолчанию 5 мм — тот же порядок, что допуск на
оси в REV-169) отмечает элемент как «переставлен».

**Что не считается изменением.** `HOST_AREA_COMPUTED`, `HOST_VOLUME_COMPUTED`,
`CURVE_ELEM_LENGTH`, `ROOM_PERIMETER` — пересчитываемые Revit'ом числа; сами по
себе они из diff'а исключены (`VOLATILE_PARAMETER_KEYS` в `utils/modelDiff.ts`),
иначе перекрытие в соседней комнате приходило бы как «изменение» каждый раз, когда
где-то на этаже подвинули стену. `ROOM_AREA` исключением не является: это та
самая цифра, ради которой затевался весь эпик — «площадь пом. 45 выросла на
4 м²» строится именно на ней.

**Заголовок собирается, не считывается с одного элемента.** `buildDiffHeadline`
кластерует переставленные элементы по (уровень, категория) и берёт самый большой
кластер, плюс самое большое изменение площади помещения — ровно два факта из
примера в тикете. Единичная перестановка в заголовок не попадает: кластер меньше
двух элементов — не находка для сводки, только для списка.

**У элементов без уровня** (см. предыдущий раздел — балки без `LevelId`) свой
уровень пуст и в снимке, и здесь; diff складывает их в отдельную группу
«(без уровня)», а не пытается угадать этаж по Z габарита — это способнее
дальнейшего тикета, если понадобится.

**Сравнение с текущим состоянием vs. с другим снимком.** Без `toSnapshotId` /
`toLabel` инструмент читает открытую модель напрямую через `export_model_snapshot`
(та же постраничная схема, что в `create_model_snapshot`) и ничего не сохраняет —
сравнение разовое. С двумя `snapshotId` Revit не нужен вовсе: оба снимка уже в
`model-snapshots.db`.

**Имя модели проверяется.** Снимок «выдачи АР» и открытая сейчас модель другого
проекта дают бессмысленные числа; инструмент отказывает, если имена не совпадают,
если явно не передан `allowModelMismatch: true`.

Правила самого diff'а живут в `server/src/utils/modelDiff.ts` и проверяются
`modelDiff.test.ts` без Revit — на синтетических `SnapshotElementRow`, как и
`modelSnapshot.test.ts` для REV-170.

## Облака изменений и таблица ревизий (REV-172)

| Tool | Revit command | What it does |
|------|---------------|--------------|
| `create_revision_clouds` | `create_revision_clouds` (`commandset/Commands/AnnotationComponents/CreateRevisionCloudsCommand.cs`) | Кластерует плоский список изменений (`compare_model_versions`' `location`) в облака, заводит/переиспользует Revision, рисует по одному облаку на кластер на плане нужного уровня |

Третий тикет эпика — оформление diff'а, а не сам diff. Кластеризация и
геометрия облака целиком в TypeScript (`server/src/utils/revisionClouds.ts`,
тесты на синтетических точках, без Revit); плагин только находит вид по уровню,
проверяет, не рисовалось ли уже такое облако, и рисует.

**Кластеризация — не «ближе к центру», а цепочкой.** Изменения объединяются в
один кластер, если между ними по цепочке ближайших соседей нет разрыва больше
`clusterRadiusMm` (по умолчанию 3000 мм — масштаб одной комнаты), даже если
крайние точки кластера дальше друг от друга, чем радиус. Иначе большая комната
с восемью изменениями по её периметру распалась бы на несколько облаков вместо
одного — ровно то, что критерий приёмки запрещает.

**Уровень — это уровень, а не вид.** Кластер несёт `level` (имя уровня из
diff'а), а не имя вида: `create_revision_clouds` сам ищет план этого уровня —
`ViewPlan`, чей `GenLevel.Name` совпадает — среди видов, размещённых на листах
(иначе облако нарисуется, но ни в одну таблицу ревизий не попадёт: Revit
подхватывает ревизию с листа только через `Viewport`). Несколько таких планов
на уровне или ни одного — это предупреждение, а не тихий выбор наугад; `viewMap`
называет вид явно для уровня, где авто-подбор промахнулся.

**Повторный прогон не плодит дубли — ни облаков, ни ревизии.** Кластер несёт
`signature` — хеш от отсортированного набора `UniqueId` изменений, которые в
него вошли (`utils/revisionClouds.ts`, `cloudSignature`). Плагин пишет
`MCP-DIFF:<signature> <комментарий>` в Comments каждого созданного облака и
перед рисованием ищет по всему документу облако с той же сигнатурой — нашёл,
пропускает. Тот же приём и для Revision: неизданная ревизия с тем же
`Description` переиспользуется, а не создаётся заново. Signature зависит только
от состава, не от порядка прихода изменений — второй прогон того же diff'а
после перезапуска сервера видит те же сигнатуры.

**Марка ревизии и попадание в таблицу — штатное поведение Revit**, не код этого
инструмента: как только облако существует на виде, размещённом на листе, Revit
сам добавляет ревизию в `RevisionSchedule` этого листа и присваивает ей номер
(`Revision.SequenceNumber`). Дальше открывается остальное — облако можно вручную
подписать номером на листе, — но это уже не то, что критерии приёмки просят.

**Чего эта версия не делает.** Марка (Revision Tag — семейство с номером
рядом с облаком, которое архитектор обычно ставит на лист вручную) не
размещается — только сама ревизия и облако. Комментарии — единственное место,
где хранится сигнатура; переименование параметра Comments вручную в Revit
потеряет дедупликацию для этого облака (новый прогон нарисует его повторно).

Separate from `MCP_TOOL_PROFILE` (server env). The dockable assistant filters
`plugin/Core/Assistant/ToolCatalog.cs` so the model sees **≤ 30** tools per
request instead of the full ~70.

| Layer | Contents |
|-------|----------|
| **core** (always) | `get_current_view_info`, `get_current_view_elements`, `get_selected_elements`, `get_available_family_types`, `get_element_parameters`, `set_element_parameter`, `export_room_data`, `operate_element`, `delete_element`, `query_norm_rules` |
| **modeling** | CAD tracing (`get_cad_link_geometry`, `trace_*_from_cad`), create_* elements, rooms/levels/stairs/railings/openings, **`ensure_wall_type`**, `ensure_opening_type` |
| **annotation** | grids, dimensions, `tag_rooms` / `tag_walls`, text notes, detail lines/views/regions, **`create_node_detail`**, `place_detail_component`, `load_family`, `get_document_styles`, `color_splash` |
| **schedules** | door/window/floor schedules, floor explication, TEP (`render_tep_table` / `export_tep_data`), schedule configure/validate |
| **sheets** | `create_sheet`, `place_view_on_sheet`, `auto_layout_sheet`, `fit_schedule_to_sheet` |
| **norms** | `run_norm_audit`, `check_*`, filled regions (`create_filled_regions` — room/plan only), annotate findings, geometry helpers (no `export_egress_graph`) |
| **data** | other `export_*`, materials, `analyze_model_statistics`, `ai_element_filter`, `batch_execute`, `send_code_to_revit` |

**How profiles are chosen**

1. Scenario chip → `ScenarioPreset.Profiles` (exact).
2. Free text → `IntentRouter` heuristic (keywords); optional cheap LLM call without tools if ambiguous.
3. **Escalation:** if the model calls a tool outside the active set, the host returns `tool_not_in_profile` with `availableInProfiles`, merges those profiles, and expands the catalog on the next round (no hard fail).

API: `ToolCatalog.GetOpenAiTools(profiles)`, `IntentRouter.ResolveHeuristic` / `ResolveAsync`.

## Name aliases (MCP → Revit command)

These are **intentional**. MCP / Cursor may send the stable AI-facing name; Revit keeps the historical `CommandName`. The **in-Revit assistant** catalog lists only the canonical name; `ToolCatalog.ResolveToolAlias` maps legacy names before execute (REV-116).

| MCP / alias | Canonical Revit `commandName` (assistant catalog) |
|-------------|-----------------------------------------------------|
| `color_elements` | `color_splash` |
| `tag_all_rooms` | `tag_rooms` |
| `tag_all_walls` | `tag_walls` |
| `create_model_snapshot` | `export_model_snapshot` |

`fill_title_block` and `number_rooms` are **server-only** (Cursor MCP). They are not in the assistant catalog; calling them returns a clear Russian soft-error.

## Ownership legend

| Tag | Meaning |
|-----|---------|
| `commandset` | 1:1 MCP tool ↔ Revit command (same name, unless aliased) |
| `server-only` | Logic / orchestration in TypeScript; may call one or more Revit commands |
| `plugin-builtin` | Handled inside the Revit add-in (not a commandset class), e.g. `batch_execute` |
| `internal` | In `command.json` / C#, **no** public MCP tool — building block for other tools |

## Aliases & special cases

| Name | Kind | Notes |
|------|------|-------|
| `batch_execute` | `plugin-builtin` | `assemblyPath: plugin:builtin` in `command.json` |
| `export_egress_graph` | `internal` | Used by `check_evacuation_distance`, `number_rooms`; **not** in in-Revit assistant catalog (REV-116) |
| `run_norm_audit` | `server-only` (+ thin plugin orchestrator for in-Revit chat) | Full audit in `server/src/normatives/normAudit/` |
| `annotate_norm_findings` | `server-only` (+ plugin helper for in-Revit chat) | Composes `create_text_notes` / leaders |
| `extract_norm_rules_from_pdf` / `query_norm_rules` / `save_norm_rule` | `server-only` | SQLite / PDF; no Revit call |
| `fill_title_block` / `number_rooms` | `server-only` | Cursor MCP only; not in assistant `Definitions` — soft-error if invented |
| `trace_walls_from_cad` | `server-only` | Orchestrates `get_cad_link_geometry` + geometry merge + `create_line_based_element` + verify (REV-140). REV-152/153: `openingGapMm` joins a run across a gap **only where the CAD shows a door or window**, so walls stay continuous and Revit cuts the openings instead of every door getting a stub host; verify samples along the axis instead of judging it by its midpoint |
| `trace_openings_from_cad` | `server-only` | Orchestrates CAD opening detection + host match + `create_point_based_element` + verify (REV-147/148/149); category door\|window\|both. REV-149: doors come from DWG swing arcs (hinge = arc centre → exact centre, width, swing side and hand); `strictLocation` defaults on; verify reads the placed elements back instead of comparing the plan with itself. REV-152: the placed door's own plan swing arc is measured against the DWG arc — `swingMismatchCount` / `swingIssues`. REV-153: `exactTypes` calls `ensure_opening_type` so an opening is built at its traced width, not the nearest stock size |
| `ensure_opening_type` | `command.json` | Returns a door/window `FamilySymbol` of a requested width/height, duplicating the source type and setting its size when the project has nothing that close (REV-153) |
| `ensure_wall_type` | `command.json` | Duplicate a wall type and set core thickness (REV-154); also in assistant modeling profile |
| `create_node_detail` | `commandset` | Drafting node from wall/floor CompoundStructure (junction/single); hatches, dimensions, labels |
| `create_detail_regions` | `commandset` | Hatch arbitrary contours on drafting/detail/section/plan (`MCP-DR`); not room-based `create_filled_regions` |
| `load_family` | `commandset` | `doc.LoadFamily` for `.rfa` paths on the Revit machine; returns loaded types for `place_detail_component` |
| `create_detail_view` | `commandset` | Modes: `callout`, `drafting`, **`section`** (live cut; Fine draws compound layers) |
| `create_detail_lines` | `commandset` | Polylines + arcs + `lineStyleName` (OST_Lines subcategories) |
| `get_document_styles` | `commandset` | Also returns `lineStyles`, `filledRegionTypes`, `fillPatterns` (not only dimensions/grids/text) |
| `trace_columns_from_cad` | `server-only` | Orchestrates `get_cad_link_geometry` + column symbol grouping + `create_point_based_element` with rotation (REV-149); rectangular and round columns. Columns must **not** go through `trace_walls_from_cad` — they come out as four stubs |
| `check_door_width`, `check_tambour_size`, `check_room_norms`, `check_window_openings`, `check_vertical_circulation`, `check_accessibility`, `check_evacuation_distance` | `server-only` (or hybrid) | Often compose geometry/export commands + norm library; may not have a matching `check_*` in `command.json` |
| `create_model_snapshot` | `server-only` orchestrator over `export_model_snapshot` | Pages the model out of Revit and writes it into `model_snapshots` / `snapshot_elements`; also lists and deletes snapshots. Hashing and storage live in TS (`utils/modelSnapshot.ts`, `database/snapshots.ts`), the Revit command only reports facts (REV-170) |
| `compare_model_versions` | `server-only`; calls `export_model_snapshot` only for the "current state" side | Diffs two stored snapshots, or a snapshot against a fresh read of the open model. Matching, filtering and wording all live in TS (`utils/modelDiff.ts`), tested against synthetic rows (REV-171) |
| `highlight_room_tags` | **removed / not implemented** | Do not advertise; do not add to `PRIORITY_TOOL_FILES` without a tool file |

## Default 1:1 map

Unless listed under aliases or special cases, MCP tool name **equals** Revit `commandName` and lives in `commandset`.

Examples: `say_hello`, `get_current_view_info`, `create_line_based_element`, `operate_element`, `create_filled_regions`, `check_evacuation_width`, `check_room_depth`, `check_min_dimensions`, `check_fire_doors`, …

Full machine-checkable lists: run `npm run check:tool-registry` from `server/` (script lives at repo root `scripts/check-tool-registry.mjs`).

## Arguments are closed, and filters are checked (19.08.2026)

Every tool registered with a parameter shape gets that shape as a **strict**
Zod object — `server/src/tools/register.ts` does it for all of them at once, no
tool module opts in. Two consequences worth knowing before you add a tool:

- an argument name the model invented is **refused**, with the tool's real
  parameter names in the message, instead of being silently dropped. Plain
  `z.object()` strips unknown keys, and the call then succeeds having ignored
  what was asked: `get_current_view_elements({categories: […]})` returned every
  element in the view, and the model reported that as the room count;
- the published JSON Schema carries `additionalProperties: false`, so the model
  reads the closed list before it calls. Tools registered *without* a shape keep
  `inputSchema: undefined` — that is what lets a client call them with no
  arguments, and it is left alone.

Add a line to `ARGUMENT_HINTS` in `server/src/utils/toolArgs.ts` only when a
tool's refusal has actually been watched to confuse the model in the field.

The same rule applies one level down: a filter the target cannot parse must be
refused, never passed on. Revit resolves categories by the exact `OST_*` enum
and returns *everything* when nothing parses, so category arguments go through
`normalizeCategoryNames` first.

## Checklist: adding a tool

1. Implement `commandset` Command + EventHandler (if Revit work is needed).
2. Register in root `command.json`.
3. Add `server/src/tools/<mcp_name>.ts` with `register*` export; if MCP name ≠ command name, document alias here and in the known-alias map in the check script.
4. Optionally expose in `plugin/Core/Assistant/ToolCatalog.cs` for in-Revit chat.
5. Run `npm run check:tool-registry` (from `server/`) and update this doc if ownership/alias changed.

## Related

- Agent rules: [AGENTS.md](../AGENTS.md), [.cursor/rules/revit-mcp.mdc](../.cursor/rules/revit-mcp.mdc)
- Drift script: [scripts/check-tool-registry.mjs](../scripts/check-tool-registry.mjs)
