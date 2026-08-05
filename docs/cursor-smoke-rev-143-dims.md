# Cursor smoke — REV-143 (размеры)

Приёмка playbook размеров для **Cursor + MCP** (не in-Revit chat).

## Сценарий

1. Активный план этажа (`get_current_view_info`).
2. 2–3 крупные комнаты: `dimension_room_walls` `placement=interior`, `offsetMm` ≥ 400–500.
3. **Verify:** `get_current_view_elements` `annotationCategoryList: ["OST_Dimensions"]`.
4. При overlap — `operate_element` Delete + redo с большим `offsetMm`.
5. Внешние оси: `dimension_grids` (`includeOpeningTier: true` по умолчанию — 3 яруса, REV-141).

## Smoke 2026-08-04 («2 этаж»)

| Шаг | Результат |
| --- | --- |
| Комнаты | `884352`, `667609`, `884343` (гостиные), `offsetMm=500` |
| Interior ids | `1820003`–`1820008` (6 цепочек, 2 на комнату) |
| Verify | ids `1820003`–`1820008` через `get_elements_parameters` (вид «2 этаж»); `Select` ок |
| Collector quirk | `get_current_view_elements` / `ai_element_filter` visible иногда **не** видят только что созданные room dims → после Commit нужен `doc.Regenerate()` (фикс в DimensionRoomWalls) |
| Exterior | `1819999`–`1820002` (4 цепочки `dimension_grids`, без яруса проёмов — до REV-141) |

Значения (мм): 3470×8845, 3070×8470, 3470×6920. Выделены Select на плане.

## Smoke REV-141 (ярус проёмов)

После пересборки `Debug R23` + **перезапуск Revit** (DLL иначе залочена) + reload MCP server:

1. `get_current_view_info` — активный план этажа с осями и фасадными проёмами.
2. `dimension_grids` с дефолтами (`includeOpeningTier: true`).
3. Ожидание: в ответе `openingTierIds` (1–2 id), `openingOffsetMm: 400`, всего до 6 цепочек (3×2 стороны).
4. `get_current_view_elements` `annotationCategoryList: ["OST_Dimensions"]` — ближайший ярус к контуру = проёмы/простенки.

Полная приёмка на файле заказчика — **REV-144**.
