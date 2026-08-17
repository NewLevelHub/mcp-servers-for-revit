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
            foreach (var group in NormAnnotationText.GroupByElement(findings))
            {
                notes.Add(new JObject
                {
                    ["text"] = string.Join("\n", group.Lines),
                    ["elementId"] = group.ElementId,
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
