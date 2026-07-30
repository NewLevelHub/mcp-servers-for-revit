using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// In-Revit substitute for server run_norm_audit — runs all available check_* Revit commands.
    /// </summary>
    public static class NormAuditOrchestrator
    {
        private static readonly string[] AvailableChecks =
        {
            "check_evacuation_width",
            "check_room_depth",
            "check_min_dimensions",
            "check_fire_doors"
        };

        private static readonly string[] SkippedInPlugin =
        {
            "door_clear_width (используй check_* в Cursor MCP)",
            "tambour_size_min",
            "room_area_min / room_height_min / storey_height",
            "window_sill_height / opening_height",
            "stair_width / ramp / railing",
            "мгн / accessibility (topics=[мгн] в Cursor MCP)"
        };

        public static string Run(string argsJson)
        {
            JObject args;
            try
            {
                args = string.IsNullOrWhiteSpace(argsJson)
                    ? new JObject()
                    : JObject.Parse(argsJson);
            }
            catch
            {
                args = new JObject();
            }

            var levelName = args["levelName"]?.ToString() ?? "";
            long? levelId = JTokenParsing.GetLong(args["levelId"]);
            long? viewId = JTokenParsing.GetLong(args["viewId"]);
            var filterByActiveView = JTokenParsing.GetBool(args["filterByActiveView"], defaultValue: true);

            if (string.IsNullOrWhiteSpace(levelName) && !levelId.HasValue)
            {
                var ctx = ResolveActiveViewContext();
                if (string.IsNullOrWhiteSpace(levelName))
                    levelName = ctx.LevelName;
                if (!levelId.HasValue)
                    levelId = ctx.LevelId;
                if (!viewId.HasValue)
                    viewId = ctx.ViewId;
            }

            var includeCompliant = JTokenParsing.GetBool(args["includeCompliant"]);
            var mode = args["mode"]?.ToString() ?? "report";
            var annotate = JTokenParsing.GetBool(args["annotate"], defaultValue: true);

            var baseCheckArgs = new JObject
            {
                ["levelName"] = levelName,
                ["levelId"] = levelId,
                ["viewId"] = viewId,
                ["filterByActiveView"] = filterByActiveView,
                ["mode"] = "report",
                ["includeCompliant"] = includeCompliant
            };

            var findings = new JArray();
            var checkRuns = new JArray();
            var errors = new List<string>();

            foreach (var check in AvailableChecks)
            {
                try
                {
                    var enriched = NormCheckDefaults.EnrichArgs(check, baseCheckArgs.ToString(Formatting.None));
                    var raw = SocketService.Instance.ExecuteJsonRpcLocal(check, enriched);
                    // Same as LocalAgentHost: citation lives on request args, must merge onto result
                    // or NormalizeFinding leaves empty source → callouts show only room name (REV-130).
                    raw = NormCheckDefaults.AttachSourceToResult(check, enriched, raw);
                    if (check.Equals("check_fire_doors", StringComparison.OrdinalIgnoreCase))
                        raw = FireDoorRulesApplier.EnrichRawResult(raw);

                    var parsed = ParseRpcResult(raw);
                    if (parsed == null)
                    {
                        errors.Add(check + ": пустой ответ");
                        continue;
                    }

                    if (parsed["error"] != null)
                    {
                        errors.Add(check + ": " + (parsed["error"]?["message"]?.ToString() ?? "ошибка"));
                        continue;
                    }

                    var result = parsed["result"] ?? parsed;
                    checkRuns.Add(new JObject
                    {
                        ["checkType"] = check,
                        ["success"] = result["Success"] ?? result["success"] ?? true,
                        ["summary"] = result["Message"] ?? result["message"] ?? ""
                    });

                    CollectFindings(findings, result, check);
                }
                catch (Exception ex)
                {
                    errors.Add(check + ": " + ex.Message);
                }
            }

            findings = DeduplicateFindings(findings);
            NormViolationDisplayHelper.SplitFindings(
                findings,
                out var roomIds,
                out var doorIds,
                violationOnly: true);

            var violationCount = 0;
            var nearLimitCount = 0;
            foreach (JToken f in findings)
            {
                var status = f["status"]?.ToString() ?? "";
                if (status.Equals("violation", StringComparison.OrdinalIgnoreCase))
                    violationCount++;
                else if (status.Equals("nearLimit", StringComparison.OrdinalIgnoreCase))
                    nearLimitCount++;
            }

            var response = new JObject
            {
                ["success"] = true,
                ["Success"] = true,
                ["levelName"] = levelName,
                ["mode"] = mode,
                ["scopeNote"] = "In-Revit: коридоры, глубина жилых, лоджии/балконы, ПД. Полный аудит — Cursor MCP run_norm_audit.",
                ["summary"] =
                    $"Нарушений: {violationCount}" +
                    (nearLimitCount > 0 ? $", на грани: {nearLimitCount}" : "") +
                    $" · помещений к заливке: {roomIds.Count}, дверей: {doorIds.Count}",
                ["findings"] = findings,
                ["roomIds"] = new JArray(roomIds),
                ["doorElementIds"] = new JArray(doorIds),
                ["checkRuns"] = checkRuns,
                ["skippedRules"] = new JArray(SkippedInPlugin),
                ["errors"] = errors.Count > 0 ? new JArray(errors) : null
            };

            if (mode.Equals("highlight", StringComparison.OrdinalIgnoreCase))
            {
                var display = NormViolationDisplayHelper.Highlight(findings, annotate, clearPrevious: true);
                response["highlight"] = display;
                response["displayHint"] =
                    "Подсветка выполнена: только status=violation (не nearLimit). " +
                    "Помещения — цветовая область, двери — SetColor, выноски — annotate_norm_findings.";
            }
            else
            {
                response["displayHint"] =
                    "Для подсветки: mode=highlight (или create_filled_regions roomIds из ответа + operate_element на doorElementIds). " +
                    "Не вызывай create_filled_regions без roomIds — иначе зальёт весь этаж.";
            }

            return response.ToString(Formatting.None);
        }

        private static ActiveViewContext ResolveActiveViewContext()
        {
            try
            {
                var raw = SocketService.Instance.ExecuteJsonRpcLocal("get_current_view_info", "{}");
                var jo = JObject.Parse(raw);
                var result = jo["result"] as JObject ?? jo;
                return new ActiveViewContext
                {
                    ViewName = result["Name"]?.ToString() ?? result["name"]?.ToString() ?? "",
                    LevelName = result["LevelName"]?.ToString()
                        ?? result["levelName"]?.ToString()
                        ?? result["Name"]?.ToString()
                        ?? "",
                    LevelId = JTokenParsing.GetLong(result["LevelId"]) ?? JTokenParsing.GetLong(result["levelId"]),
                    ViewId = JTokenParsing.GetLong(result["Id"]) ?? JTokenParsing.GetLong(result["id"])
                };
            }
            catch
            {
                return new ActiveViewContext();
            }
        }

        private sealed class ActiveViewContext
        {
            public string ViewName { get; set; } = "";
            public string LevelName { get; set; } = "";
            public long? LevelId { get; set; }
            public long? ViewId { get; set; }
        }

        private static JArray DeduplicateFindings(JArray findings)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new JArray();
            foreach (JToken f in findings)
            {
                var id = f["elementId"]?.ToString() ?? "";
                var check = f["checkType"]?.ToString() ?? "";
                var status = f["status"]?.ToString() ?? "";
                var key = id + "|" + check + "|" + status;
                if (!seen.Add(key))
                    continue;
                deduped.Add(f);
            }
            return deduped;
        }

        private static JObject ParseRpcResult(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            try
            {
                return JObject.Parse(raw);
            }
            catch
            {
                return new JObject { ["result"] = JToken.Parse(raw) };
            }
        }

        private static void CollectFindings(JArray target, JToken result, string checkName)
        {
            if (result == null)
                return;

            TryAppendArray(target, result["findings"], checkName, result);
            TryAppendArray(target, result["Findings"], checkName, result);
            // commandset serializes lists as camelCase "violations" (JsonProperty);
            // also accept PascalCase / "violators" from older/alternate payloads.
            TryAppendViolators(target, result["violations"], checkName, "violation", result);
            TryAppendViolators(target, result["Violations"], checkName, "violation", result);
            TryAppendViolators(target, result["violators"], checkName, "violation", result);
            TryAppendViolators(target, result["Violators"], checkName, "violation", result);
            TryAppendViolators(target, result["nearLimit"], checkName, "nearLimit", result);
            TryAppendViolators(target, result["NearLimit"], checkName, "nearLimit", result);

            var doors = result["doors"] ?? result["Doors"];
            if (doors is JArray doorArr)
            {
                foreach (JToken d in doorArr)
                {
                    var compliant = d["compliant"] ?? d["Compliant"];
                    if (compliant != null && compliant.Type == JTokenType.Boolean && !compliant.Value<bool>())
                    {
                        target.Add(NormFindingMapper.Normalize(d, checkName, "violation", result));
                    }
                }
            }
        }

        private static void TryAppendArray(JArray target, JToken arr, string checkName, JToken parentResult)
        {
            var a = arr as JArray;
            if (a == null)
                return;
            foreach (JToken item in a)
                target.Add(NormFindingMapper.Normalize(item, checkName, item["status"]?.ToString() ?? "violation", parentResult));
        }

        private static void TryAppendViolators(
            JArray target,
            JToken arr,
            string checkName,
            string status,
            JToken parentResult)
        {
            var a = arr as JArray;
            if (a == null)
                return;
            foreach (JToken item in a)
                target.Add(NormFindingMapper.Normalize(item, checkName, status, parentResult));
        }
    }
}
