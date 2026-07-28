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
            long? levelId = args["levelId"]?.Value<long?>();
            long? viewId = args["viewId"]?.Value<long?>();
            var filterByActiveView = args["filterByActiveView"]?.Value<bool>() ?? true;

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

            var includeCompliant = args["includeCompliant"]?.Value<bool>() ?? false;
            var mode = args["mode"]?.ToString() ?? "report";
            var annotate = args["annotate"]?.Value<bool>() ?? true;

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
                    LevelId = result["LevelId"]?.Value<long?>() ?? result["levelId"]?.Value<long?>(),
                    ViewId = result["Id"]?.Value<long?>() ?? result["id"]?.Value<long?>()
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

            TryAppendArray(target, result["findings"], checkName);
            TryAppendArray(target, result["Findings"], checkName);
            TryAppendViolators(target, result["violators"], checkName, "violation");
            TryAppendViolators(target, result["Violators"], checkName, "violation");
            TryAppendViolators(target, result["Violations"], checkName, "violation");
            TryAppendViolators(target, result["nearLimit"], checkName, "nearLimit");
            TryAppendViolators(target, result["NearLimit"], checkName, "nearLimit");

            var doors = result["doors"] ?? result["Doors"];
            if (doors is JArray doorArr)
            {
                foreach (JToken d in doorArr)
                {
                    var compliant = d["compliant"] ?? d["Compliant"];
                    if (compliant != null && compliant.Type == JTokenType.Boolean && !compliant.Value<bool>())
                    {
                        target.Add(NormalizeFinding(d, checkName, "violation"));
                    }
                }
            }
        }

        private static void TryAppendArray(JArray target, JToken arr, string checkName)
        {
            var a = arr as JArray;
            if (a == null)
                return;
            foreach (JToken item in a)
                target.Add(NormalizeFinding(item, checkName, item["status"]?.ToString() ?? "violation"));
        }

        private static void TryAppendViolators(JArray target, JToken arr, string checkName, string status)
        {
            var a = arr as JArray;
            if (a == null)
                return;
            foreach (JToken item in a)
                target.Add(NormalizeFinding(item, checkName, status));
        }

        private static JObject NormalizeFinding(JToken item, string checkName, string status)
        {
            var id = item["elementId"] ?? item["ElementId"] ?? item["roomId"] ?? item["RoomId"]
                ?? item["id"] ?? item["Id"];
            var source = item["source"] ?? item["Source"];
            if (source == null || source.Type == JTokenType.Null)
            {
                source = new JObject
                {
                    ["document"] = item["document"] ?? item["Document"] ?? "",
                    ["clause"] = item["clause"] ?? item["Clause"] ?? "",
                    ["quote"] = item["quote"] ?? item["Quote"] ?? ""
                };
            }

            return new JObject
            {
                ["checkType"] = item["checkType"] ?? checkName,
                ["status"] = status,
                ["elementId"] = id,
                ["name"] = item["name"] ?? item["Name"] ?? item["roomName"] ?? "",
                ["actualMm"] = item["actualMm"] ?? item["ActualMm"] ?? item["widthMm"] ?? item["DepthMm"] ?? item["depthMm"],
                ["requiredMm"] = item["requiredMm"] ?? item["RequiredMm"] ?? item["minWidthMm"] ?? item["MaxDepthMm"] ?? item["maxDepthMm"],
                ["note"] = item["note"] ?? item["reason"] ?? item["Reason"] ?? "",
                ["source"] = source
            };
        }
    }
}
