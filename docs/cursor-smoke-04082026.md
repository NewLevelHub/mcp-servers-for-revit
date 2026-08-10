# Cursor + MCP smoke — отзыв заказчика 04.08.2026 (REV-146)

Приёмка перед следующим тестом заказчика. Канал: **Cursor ↔ Revit MCP** (не in-Revit assistant).

Связанные тикеты: [REV-137](https://linear.app/newlevelhub/issue/REV-137) (epic), [REV-138](https://linear.app/newlevelhub/issue/REV-138), [REV-140](https://linear.app/newlevelhub/issue/REV-140), [REV-141](https://linear.app/newlevelhub/issue/REV-141), [REV-142](https://linear.app/newlevelhub/issue/REV-142), [REV-147](https://linear.app/newlevelhub/issue/REV-147) (двери/окна по CAD).

## Чеклист

| # | Запрос в Cursor | Ожидаемые tools | Pass? |
|---|-----------------|-----------------|-------|
| 1 | `say_hello` / «какой активный вид?» | `say_hello` / `get_current_view_info` | |
| 2 | «Перечерти стены по DWG» (связь **не** exploded) | `get_cad_link_geometry` и/или `trace_walls_from_cad` (`dryRun` → create); в ответе verify (`maxDeviationMm` / N/M). Правило: `revit-cad-redraw.mdc` (REV-142) | |
| 3 | «Наружные размеры по осям» | `dimension_grids` (3 яруса: проёмы → межосевой → габарит) | |
| 4 | «Размеры комнат» (≥3) | `dimension_room_walls` interior + verify `OST_Dimensions` | |
| 5 | Сессия ~30 мин | 0 ручных restart Revit/MCP (REV-139) | |

## Live notes — стены по CAD (2026-08-06)

**Модель:** план «Уровень 1», связь `двг2.dwg` (ImportInstance, не exploded), ~3149 сегментов.

| Слой | Tool | Результат |
|------|------|-----------|
| `A-WALL` | `trace_walls_from_cad` | 183/183 стен, verify OK (maxDeviation 240 мм на толстых) |
| `I-WALL` | то же | 101/101 |
| `A-GLAZ-CURT` | то же, тип «Наружное остекление» | 63/63 (толщина ~25 мм) |
| Итого на виде | `get_current_view_elements` OST_Walls | **347** стен |

**Проёмы (REV-147):** после стен — `trace_openings_from_cad` dryRun на `A-DOOR` / оконном слое → create; колонны (`S-COLS`) — вне scope.

**Exploded cottage (раньше):** хуже — ложные толщины, overlaps; prefer linked DWG + layer filters.

## Как прогонять

1. Revit: Open Server, нужные команды в Settings.
2. Cursor: корневая папка репо, MCP `mcp-server-for-revit-local`.
3. План этажа + CAD link на виде.
4. Пройти таблицу; fail → follow-up тикет со ссылкой на REV-146.
