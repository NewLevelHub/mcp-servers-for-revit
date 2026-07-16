# Производительность на крупных моделях

Ожидаемые времена команд, устройство таймаутов и работа с тяжёлыми выгрузками.
Эталонные замеры — жилая модель «Короткий блок» (~572 помещения, ~1300 окон).

## Ожидаемое время команд

| Команда | «Короткий блок» | Ожидание на крупной жилой модели |
| --- | --- | --- |
| `export_tep_data` | ~0,3–0,4 с | до 2 с |
| `create_finish_schedule` | ~2,4 с | до 10 с |
| `export_room_finish_data` | ~5–30 с | до 60 с (растёт с числом помещений и материалов) |
| `create_door_schedule` / `create_window_schedule` / `create_floor_schedule` / `create_curtain_wall_schedule` | секунды | до 30 с |
| `analyze_model_statistics`, `get_material_quantities`, `validate_schedule` | секунды–десятки секунд | до 120 с |
| `create_floor_explication` | десятки секунд | до 180 с |
| Листы (`create_sheet`, `place_view_on_sheet`, `auto_layout_sheet`) | секунды | до 60–120 с; зависят от количества видов и загруженности Revit UI |

Все времена зависят от занятости Revit: пока открыт диалог или идёт регенерация,
external event не выполняется и команда ждёт в очереди — это выглядит как «зависание»,
но таймером не является.

## Таймауты

Два уровня:

- **Плагин (Revit)** — каждая команда ждёт завершения external event 10–180 с
  (тяжёлые выгрузки и проверки — 60–120 с, `create_floor_explication` — 180 с).
  По истечении возвращается `TimeoutException` с именем команды.
- **MCP-сервер (сокет)** — обычные команды: 120 с; тяжёлые
  (`export_room_finish_data`, выгрузки спецификаций, `validate_schedule`,
  `analyze_model_statistics`, `get_material_quantities`, листы, ТЭП-таблица,
  нормативные проверки, `create_floor_explication`): 210 с.
  Сокетный таймаут заведомо больше плагинного, чтобы до чата доходила
  осмысленная ошибка плагина, а не обрыв по сокету.

Список тяжёлых команд — `HEAVY_COMMANDS` в `server/src/utils/SocketClient.ts`.

## Тайминги в логах (REV-3)

Каждый вызов команды пишется в JSONL-метрики:

- файл: `~/.mcp-servers-for-revit/logs/command-metrics_YYYYMMDD.jsonl`
- дублируется в stderr сервера строкой `[METRICS] {...}`

Поля: `command`, `durationMs`, `success`, `responseSize` (байты), `error`.
По `responseSize` видно, какие ответы перегружают чат; по `durationMs` — что
упирается в таймаут.

## Тяжёлые ответы: пагинация выгрузок спецификаций

`create_door_schedule`, `create_window_schedule`, `create_floor_schedule`,
`create_curtain_wall_schedule` на крупной модели возвращали тысячи инстансов
(1313 окон на «Коротком блоке») — такой ответ тормозит чат сильнее, чем сам Revit.

Теперь по умолчанию:

- список `instances` не возвращается — только `groups` (счётчики, размеры, уровни)
  и `instancesPagination.total`;
- `elementIds` в каждой группе усечены до 20 (оригинальное число — в
  `elementIdsTotal`, признак — `elementIdsTruncated`).

Параметры для полного доступа:

| Параметр | По умолчанию | Назначение |
| --- | --- | --- |
| `includeInstances` | `false` | вернуть страницу списка инстансов |
| `instancesOffset` / `instancesLimit` | 0 / 100 | постраничный обход (limit ≤ 500) |
| `maxElementIdsPerGroup` | 20 | сколько elementIds оставлять в группе (до 1000) |

Полная выгрузка из Revit при этом выполняется как раньше — усечение происходит
на MCP-сервере, поэтому пагинация не ускоряет Revit, но убирает мегабайтные
ответы из контекста чата.
