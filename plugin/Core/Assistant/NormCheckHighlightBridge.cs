using System;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// When the LLM calls check_* alone, still paint filled regions + doors (not only text notes).
    /// </summary>
    public static class NormCheckHighlightBridge
    {
        public static (string RawResult, string DoneExtra) AfterCheckTool(string toolName, string rawResult)
        {
            if (!IsNormCheckTool(toolName))
                return (rawResult, null);

            try
            {
                var jo = JObject.Parse(rawResult);
                if (jo["error"] != null)
                    return (rawResult, null);

                var result = jo["result"] as JObject ?? jo;
                var success = JTokenParsing.GetBool(result["success"], JTokenParsing.GetBool(result["Success"], defaultValue: true));
                if (!success)
                    return (rawResult, null);

                var findings = NormFindingsExtractor.FromCheckResult(toolName, result);
                if (findings == null || findings.Count == 0)
                    return (rawResult, null);

                var display = NormViolationDisplayHelper.Highlight(findings, annotate: true, clearPrevious: true);
                result["autoHighlight"] = display;

                var roomCount = JTokenParsing.GetInt(display["roomCount"]) ?? 0;
                var doorCount = JTokenParsing.GetInt(display["doorCount"]) ?? 0;
                string extra = null;
                if (roomCount > 0 || doorCount > 0)
                    extra = $"заливка: {roomCount}, двери: {doorCount}";

                if (jo["result"] != null)
                    jo["result"] = result;
                else
                    jo = new JObject { ["jsonrpc"] = "2.0", ["result"] = result };

                return (jo.ToString(Newtonsoft.Json.Formatting.None), extra);
            }
            catch
            {
                return (rawResult, null);
            }
        }

        private static bool IsNormCheckTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;
            var n = toolName.Trim();
            return n.Equals("check_room_depth", StringComparison.OrdinalIgnoreCase)
                || n.Equals("check_evacuation_width", StringComparison.OrdinalIgnoreCase)
                || n.Equals("check_min_dimensions", StringComparison.OrdinalIgnoreCase)
                || n.Equals("check_fire_doors", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class NormFindingsExtractor
    {
        public static JArray FromCheckResult(string checkName, JObject result)
        {
            var findings = new JArray();
            var violations = result["violations"] as JArray ?? result["Violations"] as JArray;
            if (violations != null)
            {
                foreach (JToken v in violations)
                    findings.Add(ToFinding(checkName, v, result));
            }

            if (checkName.Equals("check_fire_doors", StringComparison.OrdinalIgnoreCase))
            {
                var doors = result["doors"] as JArray ?? result["Doors"] as JArray;
                if (doors != null)
                {
                    foreach (JToken d in doors)
                    {
                        var compliant = d["compliant"] ?? d["Compliant"];
                        if (compliant != null && compliant.Type == JTokenType.Boolean && !compliant.Value<bool>())
                            findings.Add(ToFinding(checkName, d, result));
                    }
                }
            }

            return findings;
        }

        private static JObject ToFinding(string checkName, JToken item, JObject result)
        {
            return NormFindingMapper.Normalize(item, checkName, "violation", result);
        }
    }
}
