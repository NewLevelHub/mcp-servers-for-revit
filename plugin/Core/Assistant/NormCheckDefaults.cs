using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// In-Revit agent calls Revit check_* commands directly (no Node norm library).
    /// When the model omits required limits, inject values from the exported offline
    /// catalog (or hardcoded СП РК fallbacks) so checks run with citations.
    /// </summary>
    public static class NormCheckDefaults
    {
        public static string EnrichArgs(string toolName, string argsJson, string userText = null)
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
                return argsJson ?? "{}";
            }

            if (string.IsNullOrWhiteSpace(toolName))
                return args.ToString(Formatting.None);

            var n = toolName.Trim();
            var fromCatalog = NormCatalogStore.GetResolved(n);
            var catalogOk = NormCatalogStore.IsAvailable;

            if (n.Equals("check_evacuation_width", StringComparison.OrdinalIgnoreCase))
            {
                if (args["minWidthMm"] == null)
                {
                    ApplyNumber(args, fromCatalog, "minWidthMm", 1200);
                    // Prefer trusted corridor citation — catalog often picks «световой карман / дверь 1,2 м».
                    ApplyTrustedSource(args, fromCatalog,
                        "СП РК 3.02-101-2012",
                        "п. 4.4 / эвак. пути",
                        "Ширина эвакуационного коридора принимается не менее 1,2 м (1200 мм).",
                        IsCorridorWidthQuote);
                    MarkCatalogStatus(args, catalogOk, fromCatalog != null);
                }
            }
            else if (n.Equals("check_room_depth", StringComparison.OrdinalIgnoreCase))
            {
                if (args["minDepthMm"] == null && args["maxDepthMm"] == null)
                {
                    ApplyNumber(args, fromCatalog, "maxDepthMm", 6000);
                    if (args["roomScope"] == null)
                    {
                        var scope = fromCatalog?["roomScope"]?.ToString();
                        args["roomScope"] = string.IsNullOrWhiteSpace(scope) ? "living" : scope;
                    }
                    ApplyTrustedSource(args, fromCatalog,
                        "СП РК 3.02-101-2012",
                        "п. 4.4.10.22",
                        "Глубина жилых комнат при одностороннем освещении должна быть не более 6 м.",
                        IsRoomDepthQuote);
                    MarkCatalogStatus(args, catalogOk, fromCatalog != null);
                }
            }
            else if (n.Equals("check_min_dimensions", StringComparison.OrdinalIgnoreCase))
            {
                // п. 4.6.5 (1.4 м) — только МГН/престарелые. Для ordinary LLM часто
                // передаёт 1400 из старых schema — снимаем, если не запрошен МГН.
                var housing = args["housingType"]?.ToString() ?? "";
                var isMgn = housing.Equals("mgn", StringComparison.OrdinalIgnoreCase);
                if (!isMgn)
                {
                    StripIfEquals(args, "minBalconyWidthMm", 1400);
                    StripIfEquals(args, "minLoggiaWidthMm", 1400);
                }

                var hasLimit =
                    args["minBalconyWidthMm"] != null
                    || args["minLoggiaWidthMm"] != null
                    || args["minLoggiaDepthMm"] != null
                    || args["minFirePierToOpeningMm"] != null
                    || args["minFirePierBetweenOpeningsMm"] != null
                    || args["minFirePathOutdoorWidthMm"] != null;

                if (!hasLimit)
                {
                    if (fromCatalog != null)
                    {
                        foreach (var prop in fromCatalog.Properties())
                        {
                            if (prop.Name.Equals("source", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (args[prop.Name] == null && prop.Value != null && prop.Value.Type != JTokenType.Null)
                                args[prop.Name] = prop.Value.DeepClone();
                        }
                        ApplySource(args, fromCatalog,
                            "СП РК 3.02-101-2012",
                            "п. 4.2.30 / простенки",
                            "Для обычного жилья: воздушная зона / путь к Н1 — ширина лоджий/балконов ≥ 1,2 м; " +
                            "простенок до проёма ≥ 1,2 м. Норма 1,4 м (п. 4.6.5) — только МГН.");
                        MarkCatalogStatus(args, catalogOk, true);
                    }
                    else
                    {
                        // Ordinary fallback: fire path / pier only — NOT 1400 МГН.
                        args["minFirePathOutdoorWidthMm"] = 1200;
                        args["minFirePierToOpeningMm"] = 1200;
                        ApplySource(args, null,
                            "СП РК 3.02-101-2012",
                            "п. 4.2.30 / простенки",
                            "Для обычного жилья: путь к Н1 — ширина ≥ 1,2 м; простенок ≥ 1,2 м. " +
                            "1,4 м (п. 4.6.5) — только МГН, не применять к квартирным лоджиям.");
                        MarkCatalogStatus(args, false, false);
                    }
                }
            }

            if (n.Equals("operate_element", StringComparison.OrdinalIgnoreCase))
                NormalizeOperateElementArgs(args);

            if (IsNormCheckTool(n))
                InjectActiveViewScope(args);
            else if (n.Equals("export_room_data", StringComparison.OrdinalIgnoreCase))
                InjectExportRoomDataScope(args, userText);

            return args.ToString(Formatting.None);
        }

        private static void InjectExportRoomDataScope(JObject args, string userText)
        {
            if (ExportRoomDataScopeRules.WantsModelStatistics(userText))
                return;

            if (args["filterByActiveView"] != null
                || args["levelName"] != null
                || args["levelId"] != null)
                return;

            if (!ExportRoomDataScopeRules.ShouldInjectActiveViewFilter(userText))
                return;

            args["filterByActiveView"] = true;
        }

        private static bool IsNormCheckTool(string n) =>
            n.Equals("check_evacuation_width", StringComparison.OrdinalIgnoreCase)
            || n.Equals("check_room_depth", StringComparison.OrdinalIgnoreCase)
            || n.Equals("check_min_dimensions", StringComparison.OrdinalIgnoreCase)
            || n.Equals("check_fire_doors", StringComparison.OrdinalIgnoreCase);

        private static void InjectActiveViewScope(JObject args)
        {
            if (args["filterByActiveView"] == null)
                args["filterByActiveView"] = true;

            if (args["levelId"] != null && args["viewId"] != null)
                return;

            try
            {
                var raw = SocketService.Instance.ExecuteJsonRpcLocal(
                    "get_current_view_info", "{}");
                var jo = JObject.Parse(raw);
                var r = jo["result"] as JObject ?? jo;
                if (args["levelId"] == null && r["LevelId"] != null)
                    args["levelId"] = r["LevelId"];
                if (args["viewId"] == null && r["Id"] != null)
                    args["viewId"] = r["Id"];
                if (string.IsNullOrWhiteSpace(args["levelName"]?.ToString()))
                {
                    var ln = r["LevelName"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(ln))
                        args["levelName"] = ln;
                }
            }
            catch
            {
                /* ignore */
            }
        }

        private static void NormalizeOperateElementArgs(JObject args)
        {
            var data = args["data"] as JObject;
            if (data == null)
                return;

            var cat = data["categoryNames"];
            if (cat == null || cat.Type == JTokenType.Null)
                return;

            if (cat.Type == JTokenType.String)
            {
                data["categoryNames"] = new JArray(SplitCategoryNames(cat.ToString()));
                return;
            }

            var arr = cat as JArray;
            if (arr == null)
                return;

            var flat = new List<string>();
            foreach (var item in arr)
            {
                if (item.Type != JTokenType.String)
                    continue;
                var s = item.ToString().Trim();
                if (s.Length == 0)
                    continue;
                if (s.Contains(",") || s.Contains(";") || s.Contains("/"))
                    flat.AddRange(SplitCategoryNames(s));
                else
                    flat.Add(s);
            }

            if (flat.Count > 0)
                data["categoryNames"] = new JArray(flat);
        }

        private static IEnumerable<string> SplitCategoryNames(string raw)
        {
            return (raw ?? string.Empty)
                .Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0);
        }

        /// <summary>
        /// Merge citation from enriched args into the Revit result so the model can quote GOST/СП.
        /// </summary>
        public static string AttachSourceToResult(string toolName, string argsJson, string rawResult)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(argsJson)
                    ? new JObject()
                    : JObject.Parse(argsJson);
                var source = args["source"] as JObject;
                if (source == null && args["catalogMissing"] == null)
                    return rawResult;

                var jo = JObject.Parse(rawResult);
                if (jo["error"] != null)
                    return rawResult;

                var result = jo["result"] as JObject;
                if (result == null)
                    return rawResult;

                if (source != null && result["source"] == null)
                    result["source"] = source.DeepClone();

                if (args["catalogMissing"] != null)
                    result["catalogMissing"] = args["catalogMissing"].DeepClone();
                if (args["catalogUsed"] != null)
                    result["catalogUsed"] = args["catalogUsed"].DeepClone();

                var success = result["Success"]?.Value<bool?>()
                    ?? result["success"]?.Value<bool?>();
                if (success == true && result["citationHint"] == null && source != null)
                {
                    var doc = source["document"]?.ToString() ?? "";
                    var clause = source["clause"]?.ToString() ?? "";
                    var quote = source["quote"]?.ToString() ?? "";
                    result["citationHint"] =
                        $"Цитировать только: {doc} {clause}. Кратко: «{Truncate(quote, 180)}».";
                }

                return jo.ToString(Formatting.None);
            }
            catch
            {
                return rawResult;
            }
        }

        private static void ApplyNumber(JObject args, JObject fromCatalog, string key, double? fallback)
        {
            if (args[key] != null)
                return;
            var token = fromCatalog?[key];
            if (token != null && token.Type != JTokenType.Null)
            {
                args[key] = token.DeepClone();
                return;
            }
            if (fallback != null)
                args[key] = fallback.Value;
        }

        private static void StripIfEquals(JObject args, string key, double value)
        {
            var token = args[key];
            if (token == null || token.Type == JTokenType.Null)
                return;
            double num;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                num = token.Value<double>();
            else if (!double.TryParse(token.ToString(), out num))
                return;
            if (Math.Abs(num - value) < 0.5)
                args.Remove(key);
        }

        private static void ApplySource(
            JObject args,
            JObject fromCatalog,
            string document,
            string clause,
            string quote)
        {
            ApplyTrustedSource(args, fromCatalog, document, clause, quote, _ => true);
        }

        /// <summary>
        /// Use catalog citation only when it looks related; otherwise keep a trusted fallback quote.
        /// </summary>
        private static void ApplyTrustedSource(
            JObject args,
            JObject fromCatalog,
            string document,
            string clause,
            string quote,
            Func<string, bool> catalogQuoteOk)
        {
            if (args["source"] is JObject)
                return;
            if (fromCatalog?["source"] is JObject catSource)
            {
                var catQuote = catSource["quote"]?.ToString() ?? "";
                if (catalogQuoteOk(catQuote))
                {
                    args["source"] = catSource.DeepClone();
                    return;
                }
            }
            args["source"] = new JObject
            {
                ["document"] = document,
                ["clause"] = clause,
                ["quote"] = quote
            };
        }

        private static bool IsCorridorWidthQuote(string quote)
        {
            if (string.IsNullOrWhiteSpace(quote))
                return false;
            var q = quote.ToLowerInvariant();
            if (q.Contains("светов") || q.Contains("карман"))
                return false;
            if (q.Contains("двер") && !q.Contains("коридор"))
                return false;
            return (q.Contains("коридор") || q.Contains("дәліз") || q.Contains("эвак"))
                   && (q.Contains("ширин") || q.Contains("ені") || q.Contains("width"));
        }

        private static bool IsRoomDepthQuote(string quote)
        {
            if (string.IsNullOrWhiteSpace(quote))
                return false;
            var q = quote.ToLowerInvariant();
            return q.Contains("глубин") || q.Contains("терең") || q.Contains("depth");
        }

        private static void MarkCatalogStatus(JObject args, bool catalogOk, bool resolvedHit)
        {
            args["catalogUsed"] = catalogOk && resolvedHit;
            args["catalogMissing"] = !catalogOk;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
                return s ?? "";
            return s.Substring(0, max - 1) + "…";
        }
    }
}
