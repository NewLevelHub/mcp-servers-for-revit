using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Fixes common LLM mistakes for create_*_element / create_room payloads
    /// before they hit Revit (missing data wrapper, single object instead of array, null lines).
    /// </summary>
    public static class CreateElementArgsNormalizer
    {
        public static string Normalize(string toolName, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(argsJson))
                return argsJson ?? "{}";

            JObject args;
            try
            {
                args = JObject.Parse(argsJson);
            }
            catch
            {
                return argsJson;
            }

            var n = toolName.Trim();
            if (n.Equals("get_available_family_types", StringComparison.OrdinalIgnoreCase))
                NormalizeFamilyTypesQuery(args);

            if (IsCreateArrayTool(n))
                NormalizeDataArray(args);

            if (n.Equals("create_line_based_element", StringComparison.OrdinalIgnoreCase))
                ValidateLineElements(args);

            if (n.Equals("create_room", StringComparison.OrdinalIgnoreCase))
                NormalizeDataArray(args);

            if (n.Equals("set_element_parameter", StringComparison.OrdinalIgnoreCase))
                NormalizeRoomBoundingAlias(args);

            if (n.Equals("dimension_room_walls", StringComparison.OrdinalIgnoreCase))
                NormalizeRoomId(args);

            if (n.Equals("create_filled_regions", StringComparison.OrdinalIgnoreCase))
                NormalizeFilledRegions(args);

            if (n.Equals("operate_element", StringComparison.OrdinalIgnoreCase))
                NormalizeOperateElement(args);

            return args.ToString(Formatting.None);
        }

        private static void NormalizeFamilyTypesQuery(JObject args)
        {
            var categoryName = args["categoryName"]?.ToString();
            if (string.IsNullOrWhiteSpace(categoryName) && args["category"] != null)
                categoryName = args["category"]?.ToString();

            if (!string.IsNullOrWhiteSpace(categoryName))
            {
                args["categoryName"] = categoryName.Trim();
                if (args["categoryList"] == null)
                    args["categoryList"] = new JArray(categoryName.Trim());
            }

            // Default to walls when agent asks for types without filter (common before create walls).
            if (args["categoryList"] == null && args["categoryName"] == null)
            {
                args["categoryName"] = "OST_Walls";
                args["categoryList"] = new JArray("OST_Walls");
            }

            if (args["limit"] == null)
                args["limit"] = 40;
        }

        private static bool IsCreateArrayTool(string n) =>
            n.Equals("create_line_based_element", StringComparison.OrdinalIgnoreCase)
            || n.Equals("create_point_based_element", StringComparison.OrdinalIgnoreCase)
            || n.Equals("create_surface_based_element", StringComparison.OrdinalIgnoreCase)
            || n.Equals("create_room", StringComparison.OrdinalIgnoreCase);

        private static void NormalizeDataArray(JObject args)
        {
            if (args["data"] == null)
            {
                // Common mistakes: walls / elements / rooms / items at root
                foreach (var alt in new[] { "walls", "elements", "rooms", "items", "segments" })
                {
                    if (args[alt] != null)
                    {
                        args["data"] = args[alt];
                        args.Remove(alt);
                        break;
                    }
                }
            }

            // Single object → array
            if (args["data"] is JObject single)
                args["data"] = new JArray(single);
        }

        private static void ValidateLineElements(JObject args)
        {
            var data = args["data"] as JArray;
            if (data == null || data.Count == 0)
            {
                args["_normalizeError"] =
                    "create_line_based_element требует data:[{category,typeId,locationLine:{p0,p1},height,baseLevel,baseOffset}]. " +
                    "Сначала get_available_family_types OST_Walls и возьми числовой typeId.";
                return;
            }

            for (var i = 0; i < data.Count; i++)
            {
                var item = data[i] as JObject;
                if (item == null)
                    continue;

                if (item["category"] == null)
                    item["category"] = "OST_Walls";

                if (item["baseOffset"] == null)
                    item["baseOffset"] = 0;

                if (item["height"] == null || item["height"].Type == JTokenType.Null)
                    item["height"] = 3000;

                // locationLine may arrive as start/end
                if (item["locationLine"] == null && item["start"] != null && item["end"] != null)
                {
                    item["locationLine"] = new JObject
                    {
                        ["p0"] = item["start"],
                        ["p1"] = item["end"]
                    };
                }

                var line = item["locationLine"] as JObject;
                if (line != null)
                {
                    EnsurePoint(line, "p0");
                    EnsurePoint(line, "p1");
                }
            }
        }

        private static void EnsurePoint(JObject line, string key)
        {
            var pt = line[key] as JObject;
            if (pt == null)
                return;
            if (pt["z"] == null || pt["z"].Type == JTokenType.Null)
                pt["z"] = 0;
        }

        private static void NormalizeRoomBoundingAlias(JObject args)
        {
            var name = args["parameterName"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                return;

            if (IsRoomBoundingName(name))
                args["parameterName"] = "Room Bounding";
        }

        private static bool IsRoomBoundingName(string name)
        {
            var n = name.Trim();
            return n.Equals("Граница помещения", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Room Bounding", StringComparison.OrdinalIgnoreCase)
                || n.Equals("WALL_ATTR_ROOM_BOUNDING", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("границ", StringComparison.OrdinalIgnoreCase) >= 0
                   && n.IndexOf("помещен", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void NormalizeRoomId(JObject args)
        {
            // Prefer elementId if model confused number with id
            if (args["roomId"] == null && args["elementId"] != null)
                args["roomId"] = args["elementId"];
        }

        private static void NormalizeFilledRegions(JObject args)
        {
            var roomIds = args["roomIds"] as JArray;
            if (roomIds != null && roomIds.Count > 0)
                return;

            var fromAudit = args["roomIdsFromAudit"] as JArray;
            if (fromAudit != null && fromAudit.Count > 0)
            {
                args["roomIds"] = fromAudit.DeepClone();
                return;
            }

            var findings = args["findings"] as JArray;
            if (findings == null || findings.Count == 0)
                return;

            NormViolationDisplayHelper.SplitFindings(findings, out var ids, out _, violationOnly: true);
            if (ids.Count > 0)
                args["roomIds"] = new JArray(ids);
        }

        private static void NormalizeOperateElement(JObject args)
        {
            var data = args["data"] as JObject;
            if (data == null)
            {
                data = new JObject();
                foreach (var prop in args.Properties())
                {
                    if (prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                        continue;
                    data[prop.Name] = prop.Value?.DeepClone();
                }
                args["data"] = data;
            }

            var action = data["action"]?.ToString() ?? "";
            if (!action.Equals("SetColor", StringComparison.OrdinalIgnoreCase))
                return;

            var ids = data["elementIds"] as JArray;
            if (ids != null && ids.Count > 0)
                return;

            var doorIds = args["doorElementIds"] as JArray ?? data["doorElementIds"] as JArray;
            if (doorIds != null && doorIds.Count > 0)
            {
                data["elementIds"] = doorIds.DeepClone();
                return;
            }

            var findings = args["findings"] as JArray ?? data["findings"] as JArray;
            if (findings == null || findings.Count == 0)
                return;

            NormViolationDisplayHelper.SplitFindings(findings, out _, out var doorList, violationOnly: true);
            if (doorList.Count > 0)
                data["elementIds"] = new JArray(doorList);
        }
    }
}
