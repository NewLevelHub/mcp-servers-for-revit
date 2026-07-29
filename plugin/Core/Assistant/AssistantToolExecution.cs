using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Tool dispatch / parse / cache helpers for <see cref="LocalAgentHost"/>.
    /// Kept separate so the host stays focused on the LLM turn loop.
    /// </summary>
    internal static class AssistantToolExecution
    {
        public static string ResolveToolAlias(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return toolName;

            switch (toolName.Trim().ToLowerInvariant())
            {
                case "tag_all_rooms": return "tag_rooms";
                case "tag_all_walls": return "tag_walls";
                case "color_elements": return "color_splash";
                default: return toolName.Trim();
            }
        }

        public static string ExecuteAssistantTool(string toolName, string argsJson)
        {
            if (toolName.Equals("query_norm_rules", StringComparison.OrdinalIgnoreCase))
                return NormCatalogStore.ExecuteQueryTool(argsJson);

            if (toolName.Equals("run_norm_audit", StringComparison.OrdinalIgnoreCase))
                return WrapLocalToolResult(NormAuditOrchestrator.Run(argsJson));

            if (toolName.Equals("annotate_norm_findings", StringComparison.OrdinalIgnoreCase))
                return WrapLocalToolResult(AnnotateNormFindingsHelper.Run(argsJson));

            return SocketService.Instance.ExecuteJsonRpcLocal(toolName, argsJson);
        }

        public static string WrapLocalToolResult(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return body;
            try
            {
                var jo = JObject.Parse(body);
                if (jo["result"] != null || jo["error"] != null)
                    return body;
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "local",
                    ["result"] = jo
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return body;
            }
        }

        public static (bool ok, string summary, string forModel) ParseToolResponse(string toolName, string raw)
        {
            try
            {
                var jo = JObject.Parse(raw);
                if (jo["error"] != null)
                {
                    var msg = jo["error"]?["message"]?.ToString() ?? "ошибка";
                    var human = ToolCatalog.HumanizeFailure(toolName, msg);
                    return (false, human, new JObject { ["ok"] = false, ["error"] = human }.ToString());
                }

                var result = ExtractResultPayload(jo);
                if (result is JObject resultObj)
                {
                    var successToken = resultObj["Success"] ?? resultObj["success"] ?? resultObj["ok"];
                    if (successToken != null && successToken.Type == JTokenType.Boolean && !successToken.Value<bool>())
                    {
                        var msg = resultObj["Message"]?.ToString()
                            ?? resultObj["message"]?.ToString()
                            ?? "неуспех";
                        var human = ToolCatalog.HumanizeFailure(toolName, msg);
                        var failPayload = new JObject
                        {
                            ["ok"] = false,
                            ["error"] = human,
                            ["result"] = resultObj
                        };
                        return (false, human, failPayload.ToString(Newtonsoft.Json.Formatting.None));
                    }
                }

                if (result == null)
                {
                    var human = ToolCatalog.HumanizeFailure(toolName, "пустой ответ");
                    return (false, human, new JObject { ["ok"] = false, ["error"] = human }.ToString());
                }

                var compact = CompactResult(toolName, result);
                var forModel = result.ToString(Newtonsoft.Json.Formatting.None);
                forModel = SlimFamilyTypesForModel(toolName, forModel);
                forModel = TruncateForHistory(forModel);
                return (true, compact, forModel);
            }
            catch
            {
                var human = ToolCatalog.HumanizeFailure(toolName, raw);
                return (false, human, new JObject { ["ok"] = false, ["error"] = human }.ToString());
            }
        }

        /// <summary>
        /// In-Revit tools (run_norm_audit, annotate_*) return bare JSON without jsonrpc result wrapper.
        /// </summary>
        public static JToken ExtractResultPayload(JObject jo)
        {
            if (jo == null)
                return null;
            if (jo["result"] != null)
                return jo["result"];
            if (jo["Success"] != null || jo["success"] != null || jo["findings"] != null || jo["summary"] != null)
                return jo;
            return null;
        }

        public static string CompactResult(string toolName, JToken result)
        {
            if (result is JArray arr)
                return $"{ToolCatalog.FriendlyName(toolName)}: {arr.Count}";
            if (result is JObject obj)
            {
                if (toolName.Equals("run_norm_audit", StringComparison.OrdinalIgnoreCase))
                {
                    var s = obj["summary"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return "проверка норм: " + s;
                }
                if (obj["count"] != null) return $"{ToolCatalog.FriendlyName(toolName)}: {obj["count"]}";
                if (obj["createdCount"] != null) return $"{ToolCatalog.FriendlyName(toolName)}: {obj["createdCount"]}";
                if (obj["nonCompliantCount"] != null)
                    return $"{ToolCatalog.FriendlyName(toolName)}: нарушений {obj["nonCompliantCount"]}";
                if (obj["rules"] is JArray rulesArr) return $"{ToolCatalog.FriendlyName(toolName)}: {rulesArr.Count}";
                if (obj["created"] is JArray created) return $"{ToolCatalog.FriendlyName(toolName)}: {created.Count}";
                if (obj["Success"] != null || obj["success"] != null)
                {
                    var ok = obj["Success"]?.Value<bool>() ?? obj["success"]?.Value<bool>() ?? true;
                    return ok ? ToolCatalog.FriendlyName(toolName) : ToolCatalog.FriendlyName(toolName) + " (неуспех)";
                }
            }
            return ToolCatalog.FriendlyName(toolName);
        }

        public static string InjectMissingTypeIds(
            Dictionary<string, string> cache,
            string toolName,
            string argsJson)
        {
            if (cache == null || cache.Count == 0)
                return argsJson;
            if (!toolName.Equals("create_line_based_element", StringComparison.OrdinalIgnoreCase)
                && !toolName.Equals("create_point_based_element", StringComparison.OrdinalIgnoreCase)
                && !toolName.Equals("create_surface_based_element", StringComparison.OrdinalIgnoreCase))
                return argsJson;

            JObject args;
            try { args = JObject.Parse(argsJson ?? "{}"); }
            catch { return argsJson; }

            var data = args["data"] as JArray;
            if (data == null || data.Count == 0)
                return argsJson;

            var wallTypeId = FindCachedTypeId(cache, preferWall: true);
            var anyTypeId = FindCachedTypeId(cache, preferWall: false);
            var changed = false;

            foreach (var itemTok in data)
            {
                var item = itemTok as JObject;
                if (item == null) continue;
                var tid = item["typeId"]?.Value<long?>() ?? item["TypeId"]?.Value<long?>();
                if (tid.HasValue && tid.Value > 0)
                    continue;

                long? pick = null;
                if (toolName.Equals("create_line_based_element", StringComparison.OrdinalIgnoreCase))
                    pick = wallTypeId ?? anyTypeId;
                else
                    pick = anyTypeId ?? wallTypeId;

                if (pick.HasValue && pick.Value > 0)
                {
                    item["typeId"] = pick.Value;
                    changed = true;
                }
            }

            return changed ? args.ToString(Newtonsoft.Json.Formatting.None) : argsJson;
        }

        public static bool TryGetCachedToolResult(
            Dictionary<string, string> cache,
            string toolName,
            string argsJson,
            out string rawResult)
        {
            rawResult = null;
            if (!IsCacheableReadTool(toolName))
                return false;
            var key = CacheKey(toolName, argsJson);
            return cache.TryGetValue(key, out rawResult);
        }

        public static void RememberCachedToolResult(
            Dictionary<string, string> cache,
            string toolName,
            string argsJson,
            string rawResult)
        {
            if (!IsCacheableReadTool(toolName) || string.IsNullOrEmpty(rawResult))
                return;
            cache[CacheKey(toolName, argsJson)] = rawResult;
        }

        public static bool HasNormalizeError(string argsJson, out string message)
        {
            message = null;
            try
            {
                var jo = JObject.Parse(argsJson ?? "{}");
                message = jo["_normalizeError"]?.ToString();
                return !string.IsNullOrWhiteSpace(message);
            }
            catch
            {
                return false;
            }
        }

        public static string TruncateForHistory(string content)
        {
            if (string.IsNullOrEmpty(content))
                return content ?? "";
            if (content.Length <= LocalAgentHost.MaxToolResultChars)
                return content;
            return content.Substring(0, LocalAgentHost.MaxToolResultChars) + "…";
        }

        private static long? FindCachedTypeId(Dictionary<string, string> cache, bool preferWall)
        {
            foreach (var kv in cache)
            {
                if (kv.Key.IndexOf("get_available_family_types", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    var token = JToken.Parse(kv.Value);
                    if (token is JObject root && root["result"] != null)
                        token = root["result"];

                    JArray types = token as JArray;
                    if (types == null && token is JObject obj)
                        types = (obj["types"] ?? obj["Types"] ?? obj["familyTypes"] ?? obj["Response"]) as JArray;

                    if (types == null)
                        continue;

                    foreach (var t in types.OfType<JObject>().OrderByDescending(o => preferWall && IsLikelyWallType(o)))
                    {
                        if (preferWall && !IsLikelyWallType(t))
                            continue;
                        var id = t["typeId"] ?? t["TypeId"] ?? t["FamilyTypeId"] ?? t["familyTypeId"] ?? t["id"] ?? t["Id"];
                        if (id != null && long.TryParse(id.ToString(), out var n) && n > 0)
                            return n;
                    }
                }
                catch
                {
                    // ignore bad cache entry
                }
            }

            return null;
        }

        private static bool IsCacheableReadTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;
            switch (toolName.Trim().ToLowerInvariant())
            {
                case "get_current_view_info":
                case "get_available_family_types":
                case "get_document_styles":
                case "query_norm_rules":
                    return true;
                default:
                    return false;
            }
        }

        private static string CacheKey(string toolName, string argsJson)
        {
            return toolName.Trim().ToLowerInvariant() + "|" + (argsJson ?? "{}").Trim();
        }

        private static string SlimFamilyTypesForModel(string toolName, string forModel)
        {
            if (!toolName.Equals("get_available_family_types", StringComparison.OrdinalIgnoreCase))
                return forModel;

            try
            {
                var token = JToken.Parse(forModel);
                JArray types = null;
                if (token is JArray arr)
                    types = arr;
                else if (token is JObject obj)
                    types = (obj["types"] ?? obj["Types"] ?? obj["familyTypes"] ?? obj["items"] ?? obj["Response"]) as JArray;

                if (types == null || types.Count == 0)
                    return forModel;

                var ordered = types
                    .OfType<JObject>()
                    .OrderByDescending(IsLikelyWallType)
                    .ThenBy(o => o["name"]?.ToString() ?? o["Name"]?.ToString() ?? "")
                    .Take(30)
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

                var firstWall = ordered.FirstOrDefault(IsLikelyWallType);
                var suggested = firstWall?["typeId"] ?? firstWall?["TypeId"] ?? firstWall?["FamilyTypeId"]
                    ?? firstWall?["id"]
                    ?? slim.FirstOrDefault()?["typeId"];

                return new JObject
                {
                    ["ok"] = true,
                    ["count"] = types.Count,
                    ["shown"] = slim.Count,
                    ["suggestedWallTypeId"] = suggested,
                    ["types"] = slim,
                    ["hint"] = "Для стен используй suggestedWallTypeId или typeId из types[] где category/family содержит Wall. " +
                               "Передай typeId числом в create_line_based_element."
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return forModel;
            }
        }

        private static bool IsLikelyWallType(JObject o)
        {
            if (o == null) return false;
            var blob = string.Join(" ",
                o["category"]?.ToString() ?? "",
                o["Category"]?.ToString() ?? "",
                o["familyName"]?.ToString() ?? "",
                o["FamilyName"]?.ToString() ?? "",
                o["name"]?.ToString() ?? "",
                o["Name"]?.ToString() ?? "");
            return blob.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("OST_Walls", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("Стен", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
