# Cursor smoke — REV-143 (размеры)

Приёмка playbook размеров для **Cursor + MCP** (не in-Revit chat).

## Сценарий

1. Активный план этажа (`get_current_view_info`).
2. 2–3 крупные комнаты: `dimension_room_walls` `placement=interior`, `offsetMm` ≥ 400–500.
3. **Verify:** `get_current_view_elements` `annotationCategoryList: ["OST_Dimensions"]`.
4. При overlap — `operate_element` Delete + redo с большим `offsetMm`.
5. Внешние оси: `dimension_grids` (ярус проёмов — отдельно, REV-141).

## Smoke 2026-08-04 («2 этаж»)

| Шаг | Результат |
| --- | --- |
| Комнаты | `884352`, `667609`, `884343` (гостиные), `offsetMm=500` |
| Interior ids | `1820003`–`1820008` (6 цепочек, 2 на комнату) |
| Verify | ids `1820003`–`1820008` через `get_elements_parameters` (вид «2 этаж»); `Select` ок |
| Collector quirk | `get_current_view_elements` / `ai_element_filter` visible иногда **не** видят только что созданные room dims → после Commit нужен `doc.Regenerate()` (фикс в DimensionRoomWalls) |
| Exterior | `1819999`–`1820002` (4 цепочки `dimension_grids`) |

Значения (мм): 3470×8845, 3070×8470, 3470×6920. Выделены Select на плане.

## Не закрывает

- Ярус проёмов/простенков — **REV-141**
- Live на файле заказчика — **REV-144**
