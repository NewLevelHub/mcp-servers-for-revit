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
            var clearPrevious = JTokenParsing.GetBool(args["clearPrevious"], defaultValue: true);
            var leader = !style.Equals("text_only", StringComparison.OrdinalIgnoreCase);

            var notes = new JArray();
            foreach (JToken f in findings)
            {
                var status = f["status"]?.ToString() ?? "";
                if (!status.Equals("violation", StringComparison.OrdinalIgnoreCase)
                    && !status.Equals("nearLimit", StringComparison.OrdinalIgnoreCase))
                    continue;

                var elementId = JTokenParsing.GetLong(f["elementId"])
                    ?? JTokenParsing.GetLong(f["ElementId"])
                    ?? JTokenParsing.GetLong(f["id"])
                    ?? 0;
                if (elementId <= 0)
                    continue;

                notes.Add(new JObject
                {
                    ["text"] = NormAnnotationText.Format(f),
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

        private static string Error(string message) =>
            new JObject
            {
                ["success"] = false,
                ["Success"] = false,
                ["message"] = message
            }.ToString(Formatting.None);
    }
}
