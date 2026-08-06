using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Shapes raw tool results into a compact, always-valid JSON contract for the LLM (REV-119).
    /// Never mid-string truncates JSON — trims whole array items and drops heavy fields instead.
    /// </summary>
    public static class ToolResultShaper
    {
        public const int DefaultMaxChars = 4000;
        public const int DefaultItemLimit = 20;
        public const int AuditFindingLimit = 30;
        public const int FamilyTypeLimit = 30;
        public const int CategoryLimit = 25;

        public static JObject Shape(string toolName, JToken result)
        {
            if (result == null)
            {
                return FailurePayload(
                    ToolCatalog.DescribeFailure(toolName, "пустой ответ"));
            }

            var name = (toolName ?? "").Trim().ToLowerInvariant();
            JObject shaped;
            switch (name)
            {
                case "export_room_data":
                    shaped = ShapeExportRoomData(result);
                    break;
                case "get_current_view_elements":
                    shaped = ShapeViewElements(result);
                    break;
                case "run_norm_audit":
                    shaped = ShapeNormAudit(result);
                    break;
                case "export_tep_data":
                    shaped = ShapeTepData(result);
                    break;
                case "analyze_model_statistics":
                    shaped = ShapeModelStatistics(result);
                    break;
                case "get_material_quantities":
                    shaped = ShapeMaterialQuantities(result);
                    break;
                case "get_available_family_types":
                    shaped = ShapeFamilyTypes(result);
                    break;
                case "get_cad_link_geometry":
                    shaped = ShapeCadLinkGeometry(result);
                    break;
                case "trace_walls_from_cad":
                    shaped = ShapeTraceWallsFromCad(result);
                    break;
                default:
                    shaped = ShapeGeneric(toolName, result);
                    break;
            }

            AppendAutoHighlightNote(shaped, result);
            return shaped;
        }

        public static string EnsureUnderBudget(JObject jo, int maxChars = DefaultMaxChars)
        {
            if (jo == null)
                return new JObject { ["ok"] = false, ["error"] = "пустой ответ" }.ToString(Formatting.None);

            var json = jo.ToString(Formatting.None);
            if (json.Length <= maxChars)
                return json;

            // Drop items / types / categories / data arrays first.
            ShrinkArray(jo, "items", maxChars);
            ShrinkArray(jo, "types", maxChars);
            ShrinkArray(jo, "categories", maxChars);
            ShrinkArray(jo, "findings", maxChars);
            ShrinkArray(jo, "levels", maxChars);
            ShrinkArray(jo, "roomsByPurpose", maxChars);
            ShrinkArray(jo, "materials", maxChars);

            json = jo.ToString(Formatting.None);
            if (json.Length <= maxChars)
                return json;

            // Drop heavy nested payloads.
            jo.Remove("data");
            jo.Remove("checks");
            jo.Remove("skippedRules");
            jo.Remove("categoryCounts");
            if (jo["truncated"] is JObject trunc)
            {
                trunc["hint"] = "Ответ урезан по бюджету; опирайся на summary/count.";
            }
            else
            {
                jo["truncated"] = new JObject
                {
                    ["shown"] = 0,
                    ["total"] = jo["count"] ?? 0,
                    ["hint"] = "Ответ урезан по бюджету; опирайся на summary/count."
                };
            }

            json = jo.ToString(Formatting.None);
            if (json.Length <= maxChars)
                return json;

            // Last resort: keep only ok + summary + count.
            var minimal = new JObject
            {
                ["ok"] = jo["ok"] ?? true,
                ["summary"] = jo["summary"] ?? "ответ урезан",
                ["count"] = jo["count"],
                ["truncated"] = new JObject
                {
                    ["shown"] = 0,
                    ["total"] = jo["count"] ?? 0,
                    ["hint"] = "Полный payload не влез; используй summary."
                }
            };
            json = minimal.ToString(Formatting.None);
            if (json.Length <= maxChars)
                return json;

            // Extremely long summary — trim the string field only (still valid JSON).
            var summary = minimal["summary"]?.ToString() ?? "";
            var overhead = json.Length - summary.Length;
            var keep = Math.Max(32, maxChars - overhead - 8);
            if (summary.Length > keep)
                minimal["summary"] = summary.Substring(0, keep) + "…";
            return minimal.ToString(Formatting.None);
        }

        public static JObject FailurePayload(ToolCatalog.FailureHint hint)
        {
            var error = hint.Error ?? "ошибка";
            var payload = new JObject
            {
                ["ok"] = false,
                ["error"] = error
            };
            if (!string.IsNullOrWhiteSpace(hint.Fix))
                payload["fix"] = hint.Fix;
            return payload;
        }

        // ─── heavy tools ───────────────────────────────────────────────

        private static JObject ShapeExportRoomData(JToken result)
        {
            var obj = AsObject(result);
            var rooms = FirstArray(obj, "rooms", "Rooms") ?? new JArray();
            var totalRooms = FirstInt(obj, "totalRooms", "TotalRooms") ?? rooms.Count;
            var totalArea = FirstDouble(obj, "totalArea", "TotalArea");
            var levelName = FirstString(obj, "levelName", "LevelName");
            var filteredBy = FirstString(obj, "filteredBy", "FilteredBy");
            var totalInProject = FirstInt(obj, "totalInProject", "TotalInProject");

            var scope = !string.IsNullOrWhiteSpace(levelName)
                ? $" (уровень «{levelName}»)"
                : !string.IsNullOrWhiteSpace(filteredBy)
                    ? $" ({filteredBy})"
                    : "";
            var areaPart = totalArea.HasValue
                ? $", суммарно {FormatArea(totalArea.Value)} м²"
                : "";
            var projectPart = totalInProject.HasValue && totalInProject.Value != totalRooms
                ? $"; в проекте всего {totalInProject.Value}"
                : "";

            var shown = TakeSlim(rooms, DefaultItemLimit, SlimRoom);
            var shaped = OkBase(
                $"{totalRooms} помещений{areaPart}{scope}{projectPart}",
                totalRooms,
                shown,
                rooms.Count,
                "Остальные — повтори с levelName / filterByActiveView.",
                "Числа бери из summary/count. Для марок/норм — elementId (id) из items[].");

            if (!string.IsNullOrWhiteSpace(levelName))
                shaped["levelName"] = levelName;
            if (!string.IsNullOrWhiteSpace(filteredBy))
                shaped["filteredBy"] = filteredBy;
            if (totalArea.HasValue)
                shaped["totalArea"] = totalArea.Value;
            if (totalInProject.HasValue)
                shaped["totalInProject"] = totalInProject.Value;
            return shaped;
        }

        private static JObject ShapeViewElements(JToken result)
        {
            var obj = AsObject(result);
            var elements = FirstArray(obj, "Elements", "elements") ?? new JArray();
            var filtered = FirstInt(obj, "FilteredElementCount", "filteredElementCount") ?? elements.Count;
            var totalInView = FirstInt(obj, "TotalElementsInView", "totalElementsInView");
            var totalCount = FirstInt(obj, "TotalCount", "totalCount") ?? filtered;
            var hasMore = FirstBool(obj, "HasMore", "hasMore") ?? false;
            var viewName = FirstString(obj, "ViewName", "viewName");

            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in elements.OfType<JObject>())
            {
                var cat = FirstString(el, "Category", "category") ?? "Другое";
                if (!categoryCounts.ContainsKey(cat))
                    categoryCounts[cat] = 0;
                categoryCounts[cat]++;
            }

            // If Elements was already truncated by the command (HasMore), counts from
            // the partial array under-count — prefer top-level FilteredElementCount for total,
            // but still expose what we can from the sample.
            var topCats = categoryCounts
                .OrderByDescending(kv => kv.Value)
                .Take(12)
                .ToList();
            var catParts = topCats.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
            var catSummary = catParts.Count > 0 ? " · " + string.Join(", ", catParts) : "";
            if (hasMore || elements.Count < filtered)
                catSummary += " (категории по выборке Elements[]; итог — FilteredElementCount)";

            var viewPart = !string.IsNullOrWhiteSpace(viewName) ? $" «{viewName}»" : "";
            var summary = $"На виде{viewPart}: {filtered} элементов (FilteredElementCount)" +
                          (totalInView.HasValue && totalInView.Value != filtered
                              ? $", всего на виде {totalInView.Value}"
                              : "") +
                          catSummary;

            var shown = TakeSlim(elements, DefaultItemLimit, SlimElement);
            var catObj = new JObject();
            foreach (var kv in topCats)
                catObj[kv.Key] = kv.Value;

            var shaped = OkBase(
                summary,
                filtered,
                shown,
                Math.Max(filtered, elements.Count),
                hasMore
                    ? "Есть ещё элементы (HasMore). Повтори с offset/limit или сузь categoryList."
                    : "Counts в summary/count; не пересчитывай items[].",
                "Числа бери из summary/count/categoryCounts. items[] — только образец без Properties.");

            shaped["categoryCounts"] = catObj;
            shaped["totalCount"] = totalCount;
            shaped["filteredElementCount"] = filtered;
            if (totalInView.HasValue)
                shaped["totalElementsInView"] = totalInView.Value;
            if (hasMore)
                shaped["hasMore"] = true;
            return shaped;
        }

        private static JObject ShapeNormAudit(JToken result)
        {
            var obj = AsObject(result);
            var findings = FirstArray(obj, "findings", "Findings") ?? new JArray();
            var summaryToken = obj["summary"] ?? obj["Summary"];
            string summaryText;
            int? violations = null;
            int? nearLimit = null;

            if (summaryToken is JObject sObj)
            {
                violations = FirstInt(sObj, "violations", "Violations");
                nearLimit = FirstInt(sObj, "nearLimit", "NearLimit");
                var compliant = FirstInt(sObj, "compliant", "Compliant");
                var skipped = FirstInt(sObj, "skipped", "Skipped");
                summaryText =
                    $"Нарушений: {violations ?? 0}" +
                    (nearLimit.HasValue && nearLimit.Value > 0 ? $", на грани: {nearLimit.Value}" : "") +
                    (compliant.HasValue ? $", ок: {compliant.Value}" : "") +
                    (skipped.HasValue && skipped.Value > 0 ? $", пропущено: {skipped.Value}" : "");
            }
            else if (summaryToken != null && summaryToken.Type == JTokenType.String)
            {
                summaryText = summaryToken.ToString();
            }
            else
            {
                var vCount = findings.OfType<JObject>().Count(f =>
                    string.Equals(FirstString(f, "status", "Status"), "violation", StringComparison.OrdinalIgnoreCase));
                var nCount = findings.OfType<JObject>().Count(f =>
                    string.Equals(FirstString(f, "status", "Status"), "nearLimit", StringComparison.OrdinalIgnoreCase));
                violations = vCount;
                nearLimit = nCount;
                summaryText = $"Нарушений: {vCount}" + (nCount > 0 ? $", на грани: {nCount}" : "");
            }

            var roomIds = FirstArray(obj, "roomIds", "RoomIds");
            var doorIds = FirstArray(obj, "doorElementIds", "DoorElementIds");
            if (roomIds != null && roomIds.Count > 0)
                summaryText += $" · помещений к заливке: {roomIds.Count}";
            if (doorIds != null && doorIds.Count > 0)
                summaryText += $", дверей: {doorIds.Count}";

            var nextStep = FirstString(obj, "displayHint", "DisplayHint")
                ?? "Для подсветки: create_filled_regions по roomIds + operate_element на doorElementIds.";

            var shown = TakeSlim(findings, AuditFindingLimit, SlimFinding);
            var shaped = OkBase(
                summaryText,
                findings.Count,
                shown,
                findings.Count,
                "Остальные findings — сузь topics/levelName или бери roomIds из ответа.",
                nextStep);

            if (violations.HasValue) shaped["violations"] = violations.Value;
            if (nearLimit.HasValue) shaped["nearLimit"] = nearLimit.Value;
            if (roomIds != null) shaped["roomIds"] = roomIds;
            if (doorIds != null) shaped["doorElementIds"] = doorIds;
            var levelName = FirstString(obj, "levelName", "LevelName");
            if (!string.IsNullOrWhiteSpace(levelName))
                shaped["levelName"] = levelName;
            var mode = FirstString(obj, "mode", "Mode");
            if (!string.IsNullOrWhiteSpace(mode))
                shaped["mode"] = mode;
            return shaped;
        }

        private static JObject ShapeTepData(JToken result)
        {
            var obj = AsObject(result);
            var footprint = FirstDouble(obj, "buildingFootprintArea", "BuildingFootprintArea");
            var totalArea = FirstDouble(obj, "totalArea", "TotalArea");
            var storeyCount = FirstInt(obj, "storeyCount", "StoreyCount");
            var totalRooms = FirstInt(obj, "totalRooms", "TotalRooms");
            var projectName = FirstString(obj, "projectName", "ProjectName");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(projectName))
                parts.Add(projectName);
            if (storeyCount.HasValue)
                parts.Add($"этажей: {storeyCount.Value}");
            if (footprint.HasValue)
                parts.Add($"пятно: {FormatArea(footprint.Value)} м²");
            if (totalArea.HasValue)
                parts.Add($"площадь: {FormatArea(totalArea.Value)} м²");
            if (totalRooms.HasValue)
                parts.Add($"помещений: {totalRooms.Value}");

            var levels = FirstArray(obj, "levels", "Levels") ?? new JArray();
            var byPurpose = FirstArray(obj, "roomsByPurpose", "RoomsByPurpose") ?? new JArray();

            var shaped = new JObject
            {
                ["ok"] = true,
                ["summary"] = parts.Count > 0 ? string.Join(", ", parts) : "ТЭП получены",
                ["count"] = totalRooms ?? levels.Count,
                ["levels"] = levels,
                ["roomsByPurpose"] = byPurpose,
                ["nextStep"] = "Для листа используй render_tep_table; не собирай ТЭП вручную из rooms."
            };
            if (footprint.HasValue) shaped["buildingFootprintArea"] = footprint.Value;
            if (totalArea.HasValue) shaped["totalArea"] = totalArea.Value;
            if (storeyCount.HasValue) shaped["storeyCount"] = storeyCount.Value;
            if (totalRooms.HasValue) shaped["totalRooms"] = totalRooms.Value;
            return shaped;
        }

        private static JObject ShapeModelStatistics(JToken result)
        {
            var obj = AsObject(result);
            var categories = FirstArray(obj, "categories", "Categories") ?? new JArray();
            var slimCats = new JArray();
            var summaryParts = new List<string>();

            foreach (var catTok in categories.OfType<JObject>().Take(CategoryLimit))
            {
                var catName = FirstString(catTok, "categoryName", "CategoryName") ?? "";
                var count = FirstInt(catTok, "elementCount", "ElementCount");
                if (string.IsNullOrWhiteSpace(catName) || count == null)
                    continue;

                slimCats.Add(new JObject
                {
                    ["categoryName"] = catName,
                    ["elementCount"] = count.Value
                });

                if (IsKeyStatsCategory(catName))
                    summaryParts.Add($"{catName}: {count.Value}");
            }

            var totalElements = FirstInt(obj, "totalElements", "TotalElements");
            var summary = summaryParts.Count > 0
                ? string.Join(", ", summaryParts)
                : (totalElements.HasValue ? $"элементов: {totalElements.Value}" : "статистика модели");

            return new JObject
            {
                ["ok"] = true,
                ["projectName"] = obj["projectName"] ?? obj["ProjectName"],
                ["totalElements"] = totalElements,
                ["summary"] = summary,
                ["count"] = totalElements ?? slimCats.Count,
                ["categories"] = slimCats,
                ["nextStep"] = "Счёт помещений/стен/дверей по проекту — из categories[]/summary. " +
                               "Не вызывай export_room_data для статистики модели."
            };
        }

        private static JObject ShapeMaterialQuantities(JToken result)
        {
            var obj = AsObject(result);
            var materials = FirstArray(obj, "materials", "Materials") ?? new JArray();
            var totalMaterials = FirstInt(obj, "totalMaterials", "TotalMaterials") ?? materials.Count;
            var totalArea = FirstDouble(obj, "totalArea", "TotalArea");
            var totalVolume = FirstDouble(obj, "totalVolume", "TotalVolume");

            var areaPart = totalArea.HasValue ? $", площадь {FormatArea(totalArea.Value)}" : "";
            var volPart = totalVolume.HasValue ? $", объём {FormatArea(totalVolume.Value)}" : "";

            var shown = TakeSlim(materials, DefaultItemLimit, SlimMaterial);
            return OkBase(
                $"{totalMaterials} материалов{areaPart}{volPart}",
                totalMaterials,
                shown,
                materials.Count,
                "Остальные — сузь фильтр по категории/имени материала.",
                "Агрегаты в summary; elementIds не включены.");
        }

        private static JObject ShapeFamilyTypes(JToken result)
        {
            JArray types = null;
            if (result is JArray arr)
                types = arr;
            else if (result is JObject obj)
                types = FirstArray(obj, "types", "Types", "familyTypes", "items", "Response");

            if (types == null || types.Count == 0)
            {
                return new JObject
                {
                    ["ok"] = true,
                    ["summary"] = "Типы семейств не найдены",
                    ["count"] = 0,
                    ["items"] = new JArray(),
                    ["types"] = new JArray(),
                    ["nextStep"] = "Проверь categoryName (например OST_Walls) и шаблон проекта."
                };
            }

            var ordered = types
                .OfType<JObject>()
                .OrderByDescending(WallTypePicker.Rank)
                .ThenBy(o => FirstString(o, "name", "Name") ?? "")
                .Take(FamilyTypeLimit)
                .ToList();

            var slim = new JArray();
            foreach (var o in ordered)
            {
                slim.Add(new JObject
                {
                    ["typeId"] = o["typeId"] ?? o["TypeId"] ?? o["FamilyTypeId"] ?? o["familyTypeId"] ?? o["id"] ?? o["Id"],
                    ["name"] = o["name"] ?? o["Name"] ?? o["typeName"] ?? o["TypeName"],
                    ["familyName"] = o["familyName"] ?? o["FamilyName"],
                    ["category"] = o["category"] ?? o["Category"]
                });
            }

            var firstWall = ordered.FirstOrDefault(o => WallTypePicker.Rank(o) > 0);
            var suggestedId = firstWall != null ? WallTypePicker.TryGetTypeId(firstWall) : null;
            if (!suggestedId.HasValue)
            {
                foreach (var o in slim.OfType<JObject>())
                {
                    suggestedId = WallTypePicker.TryGetTypeId(o);
                    if (suggestedId.HasValue) break;
                }
            }

            var shaped = new JObject
            {
                ["ok"] = true,
                ["summary"] = $"Типов: {types.Count}, показано {slim.Count}",
                ["count"] = types.Count,
                ["shown"] = slim.Count,
                ["items"] = slim,
                ["types"] = slim,
                ["truncated"] = new JObject
                {
                    ["shown"] = slim.Count,
                    ["total"] = types.Count,
                    ["hint"] = "Сузь categoryName / familyNameFilter. Не бери Витраж/Curtain для обычных стен."
                },
                ["nextStep"] = "Для стен используй suggestedWallTypeId (Базовая стена / перегородка), " +
                               "не Витраж. Передай typeId числом в create_line_based_element."
            };
            if (suggestedId.HasValue)
                shaped["suggestedWallTypeId"] = suggestedId.Value;
            return shaped;
        }

        private static JObject ShapeCadLinkGeometry(JToken result)
        {
            var obj = result as JObject ?? new JObject();
            var ok = obj["ok"]?.Value<bool>() ?? obj["success"]?.Value<bool>() ?? true;
            if (!ok)
            {
                var msg = FirstString(obj, "message", "summary", "Message") ?? "нет CAD на виде";
                return new JObject
                {
                    ["ok"] = false,
                    ["summary"] = msg,
                    ["count"] = 0,
                    ["items"] = new JArray(),
                    ["nextStep"] = "Привяжите DWG к уровню плана (Вставка → Связь CAD) и повторите."
                };
            }

            var itemsToken = obj["items"] as JArray ?? new JArray();
            var total = FirstInt(obj, "count") ?? itemsToken.Count;
            var shown = new JArray(itemsToken.Take(DefaultItemLimit));
            var name = FirstString(obj, "cadLinkName") ?? "CAD";
            var summary = FirstString(obj, "summary")
                          ?? $"DWG «{name}»: {total} сегмент(ов)";

            var shaped = OkBase(
                summary,
                total,
                shown,
                total,
                "Сегменты урезаны; сузь layerFilter / cadLinkName.",
                "Построй стены create_line_based_element по startMm/endMm (typeId обязателен).");

            if (obj["bboxMm"] != null)
                shaped["bboxMm"] = obj["bboxMm"];
            if (obj["cadLinkName"] != null)
                shaped["cadLinkName"] = obj["cadLinkName"];
            if (obj["cadLinkElementId"] != null)
                shaped["cadLinkElementId"] = obj["cadLinkElementId"];
            if (obj["availableLinks"] != null)
                shaped["availableLinks"] = obj["availableLinks"];

            return shaped;
        }

        private static JObject ShapeTraceWallsFromCad(JToken result)
        {
            var obj = result as JObject ?? new JObject();
            var ok = obj["ok"]?.Value<bool>() ?? false;
            if (!ok)
            {
                var msg = FirstString(obj, "message", "summary", "Message") ?? "не удалось построить стены по CAD";
                return new JObject
                {
                    ["ok"] = false,
                    ["summary"] = msg,
                    ["count"] = 0,
                    ["createdCount"] = 0,
                    ["nextStep"] = "Проверьте wallTypeId, layerFilter, bboxMm; привяжите DWG к уровню."
                };
            }

            var summary = FirstString(obj, "summary") ?? "Стены по CAD созданы";
            var created = FirstInt(obj, "createdCount") ?? FirstInt(obj, "count") ?? 0;
            var planned = FirstInt(obj, "plannedCount") ?? created;
            var elementIds = obj["elementIds"] as JArray ?? new JArray();
            var shownIds = new JArray(elementIds.Take(DefaultItemLimit));

            var shaped = new JObject
            {
                ["ok"] = true,
                ["summary"] = summary,
                ["count"] = created,
                ["plannedCount"] = planned,
                ["createdCount"] = created,
                ["elementIds"] = shownIds,
                ["nextStep"] = "Проверьте контур: get_current_view_elements OST_Walls; затем create_room / двери."
            };

            if (obj["verify"] != null)
                shaped["verify"] = obj["verify"];
            if (obj["stats"] != null)
                shaped["stats"] = obj["stats"];
            if (obj["dryRun"]?.Value<bool>() == true)
                shaped["dryRun"] = true;

            return shaped;
        }

        private static JObject ShapeGeneric(string toolName, JToken result)
        {
            var summary = CompactSummary(toolName, result);
            var shaped = new JObject
            {
                ["ok"] = true,
                ["summary"] = summary
            };

            if (result is JArray arr)
            {
                var shown = new JArray(arr.Take(DefaultItemLimit));
                shaped["count"] = arr.Count;
                shaped["items"] = shown;
                if (arr.Count > shown.Count)
                {
                    shaped["truncated"] = new JObject
                    {
                        ["shown"] = shown.Count,
                        ["total"] = arr.Count,
                        ["hint"] = "Массив урезан; опирайся на summary/count."
                    };
                }
            }
            else if (result is JObject obj)
            {
                // Prefer common count fields for the model.
                var count = FirstInt(obj, "count", "createdCount", "nonCompliantCount", "totalRooms", "FilteredElementCount");
                if (count.HasValue)
                    shaped["count"] = count.Value;

                // Strip obviously huge nested arrays from the copy.
                var data = (JObject)obj.DeepClone();
                TrimHugeArraysInPlace(data, DefaultItemLimit);
                shaped["data"] = data;
            }
            else
            {
                shaped["data"] = result;
            }

            return shaped;
        }

        // ─── helpers ───────────────────────────────────────────────────

        private static void AppendAutoHighlightNote(JObject shaped, JToken result)
        {
            if (shaped == null || result == null)
                return;
            var obj = result as JObject;
            if (obj == null)
                return;

            var highlight = obj["autoHighlight"] as JObject ?? obj["highlight"] as JObject;
            if (highlight == null)
                return;

            var roomCount = FirstInt(highlight, "roomCount", "RoomCount") ?? 0;
            var doorCount = FirstInt(highlight, "doorCount", "DoorCount") ?? 0;
            if (roomCount <= 0 && doorCount <= 0)
                return;

            var note = $"залито {roomCount} областей" +
                       (doorCount > 0 ? $", двери: {doorCount}" : "");
            var summary = shaped["summary"]?.ToString() ?? "";
            if (summary.IndexOf("залито", StringComparison.OrdinalIgnoreCase) < 0)
                shaped["summary"] = string.IsNullOrWhiteSpace(summary) ? note : summary + " · " + note;

            shaped["autoHighlight"] = new JObject
            {
                ["roomCount"] = roomCount,
                ["doorCount"] = doorCount
            };
        }

        private static JObject OkBase(
            string summary,
            int count,
            JArray shown,
            int total,
            string truncHint,
            string nextStep)
        {
            var jo = new JObject
            {
                ["ok"] = true,
                ["summary"] = summary ?? "",
                ["count"] = count,
                ["items"] = shown ?? new JArray(),
                ["nextStep"] = nextStep ?? ""
            };
            var shownCount = shown?.Count ?? 0;
            if (total > shownCount)
            {
                jo["truncated"] = new JObject
                {
                    ["shown"] = shownCount,
                    ["total"] = total,
                    ["hint"] = truncHint ?? "Массив урезан."
                };
            }
            return jo;
        }

        private static JArray TakeSlim(JArray source, int limit, Func<JObject, JObject> map)
        {
            var result = new JArray();
            if (source == null)
                return result;
            foreach (var tok in source.OfType<JObject>().Take(limit))
            {
                var mapped = map(tok);
                if (mapped != null)
                    result.Add(mapped);
            }
            return result;
        }

        private static JObject SlimRoom(JObject o)
        {
            return new JObject
            {
                ["id"] = o["id"] ?? o["Id"],
                ["name"] = o["name"] ?? o["Name"],
                ["number"] = o["number"] ?? o["Number"],
                ["level"] = o["level"] ?? o["Level"],
                ["area"] = o["area"] ?? o["Area"]
            };
        }

        private static JObject SlimElement(JObject o)
        {
            return new JObject
            {
                ["id"] = o["Id"] ?? o["id"],
                ["name"] = o["Name"] ?? o["name"],
                ["category"] = o["Category"] ?? o["category"]
            };
        }

        private static JObject SlimFinding(JObject o)
        {
            var slim = new JObject
            {
                ["checkType"] = o["checkType"] ?? o["CheckType"],
                ["status"] = o["status"] ?? o["Status"],
                ["elementId"] = o["elementId"] ?? o["ElementId"],
                ["name"] = o["name"] ?? o["Name"],
                ["level"] = o["level"] ?? o["Level"],
                ["metric"] = o["metric"] ?? o["Metric"],
                ["actualMm"] = o["actualMm"] ?? o["ActualMm"],
                ["requiredMm"] = o["requiredMm"] ?? o["RequiredMm"]
            };
            var source = o["source"] as JObject ?? o["Source"] as JObject;
            if (source != null)
            {
                slim["source"] = new JObject
                {
                    ["document"] = source["document"] ?? source["Document"],
                    ["clause"] = source["clause"] ?? source["Clause"]
                };
            }
            return slim;
        }

        private static JObject SlimMaterial(JObject o)
        {
            return new JObject
            {
                ["materialName"] = o["materialName"] ?? o["MaterialName"],
                ["materialClass"] = o["materialClass"] ?? o["MaterialClass"],
                ["area"] = o["area"] ?? o["Area"],
                ["volume"] = o["volume"] ?? o["Volume"],
                ["elementCount"] = o["elementCount"] ?? o["ElementCount"]
            };
        }

        private static void ShrinkArray(JObject jo, string propertyName, int maxChars)
        {
            if (!(jo[propertyName] is JArray arr) || arr.Count == 0)
                return;

            while (arr.Count > 0 && jo.ToString(Formatting.None).Length > maxChars)
                arr.RemoveAt(arr.Count - 1);

            if (jo["truncated"] is JObject trunc)
            {
                trunc["shown"] = arr.Count;
                if (trunc["hint"] == null)
                    trunc["hint"] = "Урезано по бюджету символов.";
            }
        }

        private static void TrimHugeArraysInPlace(JObject data, int limit)
        {
            if (data == null)
                return;

            var keys = data.Properties().Select(p => p.Name).ToList();
            foreach (var key in keys)
            {
                if (!(data[key] is JArray arr))
                    continue;
                if (arr.Count <= limit)
                    continue;

                // Drop elementIds-style id lists entirely.
                if (key.IndexOf("elementId", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.Equals("Properties", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("properties", StringComparison.OrdinalIgnoreCase))
                {
                    data.Remove(key);
                    continue;
                }

                var kept = new JArray(arr.Take(limit));
                data[key] = kept;
            }
        }

        internal static string CompactSummary(string toolName, JToken result)
        {
            var label = ToolCatalog.FriendlyName(toolName);
            if (result is JArray arr)
                return $"{label}: {arr.Count}";
            if (result is JObject obj)
            {
                if (toolName != null
                    && toolName.Equals("run_norm_audit", StringComparison.OrdinalIgnoreCase))
                {
                    var s = obj["summary"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(s) && obj["summary"]?.Type == JTokenType.String)
                        return "проверка норм: " + s;
                }
                if (toolName != null
                    && toolName.Equals("analyze_model_statistics", StringComparison.OrdinalIgnoreCase))
                {
                    var s = obj["summary"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return "статистика модели: " + s;
                }
                if (obj["count"] != null) return $"{label}: {obj["count"]}";
                if (obj["createdCount"] != null) return $"{label}: {obj["createdCount"]}";
                if (obj["nonCompliantCount"] != null)
                    return $"{label}: нарушений {obj["nonCompliantCount"]}";
                if (obj["rules"] is JArray rulesArr) return $"{label}: {rulesArr.Count}";
                if (obj["created"] is JArray created) return $"{label}: {created.Count}";
                if (obj["Success"] != null || obj["success"] != null)
                {
                    var ok = obj["Success"]?.Value<bool>() ?? obj["success"]?.Value<bool>() ?? true;
                    return ok ? label : label + " (неуспех)";
                }
                var message = FirstString(obj, "message", "Message");
                if (!string.IsNullOrWhiteSpace(message))
                    return label + ": " + message;
            }
            return label;
        }

        private static bool IsKeyStatsCategory(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
                return false;
            var n = categoryName.ToLowerInvariant();
            return n.Contains("стен") || n.Contains("wall")
                || n.Contains("двер") || n.Contains("door")
                || n.Contains("помещ") || n.Contains("room")
                || n.Contains("окн") || n.Contains("window");
        }

        private static JObject AsObject(JToken result)
        {
            return result as JObject ?? new JObject();
        }

        private static JArray FirstArray(JObject obj, params string[] names)
        {
            if (obj == null) return null;
            foreach (var n in names)
            {
                if (obj[n] is JArray arr)
                    return arr;
            }
            return null;
        }

        private static int? FirstInt(JObject obj, params string[] names)
        {
            if (obj == null) return null;
            foreach (var n in names)
            {
                var t = obj[n];
                if (t == null || t.Type == JTokenType.Null) continue;
                if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                    return t.Value<int>();
                if (int.TryParse(t.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                    return v;
            }
            return null;
        }

        private static double? FirstDouble(JObject obj, params string[] names)
        {
            if (obj == null) return null;
            foreach (var n in names)
            {
                var t = obj[n];
                if (t == null || t.Type == JTokenType.Null) continue;
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                    return t.Value<double>();
                if (double.TryParse(t.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                    return v;
            }
            return null;
        }

        private static bool? FirstBool(JObject obj, params string[] names)
        {
            if (obj == null) return null;
            foreach (var n in names)
            {
                var t = obj[n];
                if (t == null || t.Type == JTokenType.Null) continue;
                if (t.Type == JTokenType.Boolean)
                    return t.Value<bool>();
            }
            return null;
        }

        private static string FirstString(JObject obj, params string[] names)
        {
            if (obj == null) return null;
            foreach (var n in names)
            {
                var t = obj[n];
                if (t == null || t.Type == JTokenType.Null) continue;
                var s = t.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return null;
        }

        private static string FormatArea(double value)
        {
            return value.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
        }
    }
}
