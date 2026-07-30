using System;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Maps check_* violator rows → annotate-ready findings.
    /// Pure JSON (no Revit) so REV-130 regressions are unit-testable.
    /// </summary>
    public static class NormFindingMapper
    {
        public static JObject Normalize(
            JToken item,
            string checkName,
            string status,
            JToken parentResult = null)
        {
            var idToken = item["elementId"] ?? item["ElementId"] ?? item["roomId"] ?? item["RoomId"]
                ?? item["id"] ?? item["Id"];
            var elementId = JTokenParsing.GetLong(idToken);

            var source = item["source"] ?? item["Source"]
                ?? parentResult?["source"] ?? parentResult?["Source"];
            if (source == null || source.Type == JTokenType.Null)
            {
                source = new JObject
                {
                    ["document"] = FirstNonEmpty(
                        item["document"], item["Document"],
                        parentResult?["document"], parentResult?["Document"]),
                    ["clause"] = FirstNonEmpty(
                        item["clause"], item["Clause"],
                        parentResult?["clause"], parentResult?["Clause"]),
                    ["quote"] = FirstNonEmpty(
                        item["quote"], item["Quote"],
                        parentResult?["quote"], parentResult?["Quote"])
                };
            }

            var check = (item["checkType"] ?? checkName)?.ToString() ?? "";
            var actual = ResolveActualMm(item, check);
            var required = ResolveRequiredMm(item, parentResult, check);

            return new JObject
            {
                ["checkType"] = string.IsNullOrWhiteSpace(check) ? checkName : check,
                ["status"] = status,
                ["elementId"] = elementId,
                ["name"] = item["name"] ?? item["Name"] ?? item["roomName"] ?? "",
                ["actualMm"] = actual,
                ["requiredMm"] = required,
                ["note"] = item["note"] ?? item["reason"] ?? item["Reason"] ?? "",
                ["source"] = source
            };
        }

        private static JToken ResolveActualMm(JToken item, string check)
        {
            var explicitActual = item["actualMm"] ?? item["ActualMm"]
                ?? item["actualValueMm"] ?? item["ActualValueMm"];
            if (explicitActual != null && explicitActual.Type != JTokenType.Null)
                return explicitActual;

            if (check.IndexOf("room_depth", StringComparison.OrdinalIgnoreCase) >= 0
                || check.IndexOf("depth", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return item["depthMm"] ?? item["DepthMm"] ?? item["widthMm"] ?? item["WidthMm"];
            }

            if (check.IndexOf("evacuat", StringComparison.OrdinalIgnoreCase) >= 0
                || check.IndexOf("corridor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return item["widthMm"] ?? item["WidthMm"] ?? item["actualWidthMm"] ?? item["depthMm"];
            }

            return item["widthMm"] ?? item["WidthMm"]
                ?? item["depthMm"] ?? item["DepthMm"];
        }

        private static JToken ResolveRequiredMm(JToken item, JToken parent, string check)
        {
            var explicitRequired = item["requiredMm"] ?? item["RequiredMm"]
                ?? item["requiredValueMm"] ?? item["RequiredValueMm"];
            if (explicitRequired != null && explicitRequired.Type != JTokenType.Null)
                return explicitRequired;

            if (check.IndexOf("room_depth", StringComparison.OrdinalIgnoreCase) >= 0
                || check.IndexOf("depth", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return item["maxDepthMm"] ?? item["MaxDepthMm"]
                    ?? parent?["maxDepthMm"] ?? parent?["MaxDepthMm"]
                    ?? item["minDepthMm"] ?? parent?["minDepthMm"];
            }

            return item["minWidthMm"] ?? item["MinWidthMm"]
                ?? parent?["minWidthMm"] ?? parent?["MinWidthMm"]
                ?? item["maxDepthMm"] ?? parent?["maxDepthMm"]
                ?? item["minBalconyWidthMm"] ?? parent?["minBalconyWidthMm"]
                ?? item["minLoggiaWidthMm"] ?? parent?["minLoggiaWidthMm"]
                ?? item["minFirePathOutdoorWidthMm"] ?? parent?["minFirePathOutdoorWidthMm"]
                ?? item["minFirePierToOpeningMm"] ?? parent?["minFirePierToOpeningMm"];
        }

        private static string FirstNonEmpty(params JToken[] tokens)
        {
            if (tokens == null)
                return "";
            foreach (var t in tokens)
            {
                var s = t?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return "";
        }
    }
}
