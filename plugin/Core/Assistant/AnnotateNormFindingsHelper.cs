using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// In-Revit substitute for server annotate_norm_findings → create_text_notes with leaders.
    /// </summary>
    public static class AnnotateNormFindingsHelper
    {
        public static string Run(string argsJson)
        {
            JObject args;
            try
            {
                args = string.IsNullOrWhiteSpace(argsJson)
                    ? new JObject()
                    : JObject.Parse(argsJson);
            }
            catch (Exception ex)
            {
                return Error("Неверные аргументы: " + ex.Message);
            }

            var findings = args["findings"] as JArray;
            if (findings == null || findings.Count == 0)
                return Error("Нужен массив findings из run_norm_audit.");

            var style = args["style"]?.ToString() ?? "leader";
            var textTypeName = args["textTypeName"]?.ToString() ?? "ADSK_Замечания";
            var clearPrevious = args["clearPrevious"]?.Value<bool>() ?? true;
            var leader = !style.Equals("text_only", StringComparison.OrdinalIgnoreCase);

            var notes = new JArray();
            foreach (JToken f in findings)
            {
                var status = f["status"]?.ToString() ?? "";
                if (!status.Equals("violation", StringComparison.OrdinalIgnoreCase)
                    && !status.Equals("nearLimit", StringComparison.OrdinalIgnoreCase))
                    continue;

                var elementId = f["elementId"]?.Value<int?>() ?? 0;
                if (elementId <= 0)
                    continue;

                notes.Add(new JObject
                {
                    ["text"] = FormatAnnotation(f),
                    ["elementId"] = elementId,
                    ["leader"] = leader
                });
            }

            if (notes.Count == 0)
                return Error("Нет нарушений для подписи (violation/nearLimit с elementId).");

            var payload = new JObject
            {
                ["notes"] = notes,
                ["clearPrevious"] = clearPrevious,
                ["textTypeName"] = textTypeName
            };

            var raw = SocketService.Instance.ExecuteJsonRpcLocal("create_text_notes", payload.ToString(Formatting.None));
            return raw;
        }

        private static string FormatAnnotation(JToken f)
        {
            var name = (f["name"]?.ToString() ?? ("id " + f["elementId"])).Trim();
            var doc = f["source"]?["document"]?.ToString()?.Trim() ?? "";
            var clause = f["source"]?["clause"]?.ToString()?.Trim() ?? "";
            var sourceBit = JoinNonEmpty(doc, clause);

            var comparison = FormatComparison(f);
            var text = string.IsNullOrEmpty(comparison) ? name : name + ": " + comparison;
            if (!string.IsNullOrEmpty(sourceBit))
                text += " · " + sourceBit;
            return text;
        }

        private static string FormatComparison(JToken f)
        {
            var actual = f["actualMm"];
            var required = f["requiredMm"];
            if (actual == null || required == null)
                return f["note"]?.ToString() ?? "";

            var a = actual.Value<double>();
            var r = required.Value<double>();
            var op = a < r ? "<" : a > r ? ">" : "=";

            if ((f["checkType"]?.ToString() ?? "").Contains("room_area"))
                return a + " " + op + " " + r + " м²";

            return a + " " + op + " " + r + " мм";
        }

        private static string JoinNonEmpty(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a)) return b ?? "";
            if (string.IsNullOrWhiteSpace(b)) return a;
            return a.Trim() + " " + b.Trim();
        }

        private static string Error(string message) =>
            new JObject
            {
                ["success"] = false,
                ["Success"] = false,
                ["message"] = message
            }.ToString(Formatting.None);
    }
}
