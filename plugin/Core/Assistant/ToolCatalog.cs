using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Curated OpenAI-style tool schemas for the in-Revit agent (pilot scenarios).
    /// </summary>
    public static class ToolCatalog
    {
        public static JArray GetOpenAiTools()
        {
            var tools = new JArray();
            foreach (var def in Definitions)
            {
                tools.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = def.Name,
                        ["description"] = def.Description,
                        ["parameters"] = def.Parameters
                    }
                });
            }
            return tools;
        }

        public static bool RequiresConfirmation(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            var n = toolName.Trim();
            if (n.Equals("delete_element", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("operate_element", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.StartsWith("create_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.StartsWith("tag_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("annotate_norm_findings", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("apply_norm_result", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("create_text_notes", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("create_text_note", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("dimension_grids", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("dimension_room_walls", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("number_rooms", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("color_elements", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("auto_layout_sheet", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("place_view_on_sheet", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("fill_title_block", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        public static string HumanizeFailure(string toolName, string rawMessage)
        {
            var msg = rawMessage ?? "";
            if (msg.IndexOf("typeId", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("type id", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "В проекте не найден нужный тип семейства. Возьмите тип из шаблона организации.";
            }
            if (msg.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Для двери/окна нужна стена-хост. Укажите стену или создайте проём в существующей стене.";
            }
            if (msg.IndexOf("Method not found", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("未找到方法", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return $"Команда «{toolName}» недоступна. Включите её в Settings → Command Set и перезапустите сервер.";
            }
            if (msg.IndexOf("title block", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("рамк", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "В проекте нет подходящей рамки (title block). Добавьте семейство основной надписи из шаблона.";
            }
            if (string.IsNullOrWhiteSpace(msg))
                return $"Не удалось выполнить действие ({toolName}).";

            var firstLine = msg.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (firstLine.Length > 280)
                firstLine = firstLine.Substring(0, 277) + "…";
            return firstLine;
        }

        private sealed class ToolDef
        {
            public string Name;
            public string Description;
            public JObject Parameters;
        }

        private static readonly List<ToolDef> Definitions = BuildDefinitions();

        private static List<ToolDef> BuildDefinitions()
        {
            return new List<ToolDef>
            {
                T("get_current_view_info", "Active view: type, name, scale, level.", Empty()),
                T("get_current_view_elements", "Elements on the active view (mm).", P(
                    ("modelCategoryList", A("string"), "e.g. OST_Walls"),
                    ("limit", N(), "Max elements"))),
                T("say_hello", "Test connection with Revit.", Empty()),
                T("create_grid", "Create coordination grids. Prefer autoFromWalls=true.", P(
                    ("autoFromWalls", B(), "structural wall centerlines"),
                    ("bubbleEnd", S(), "bottomLeft"),
                    ("gridTypeName", S(), "Марка оси 5 мм"),
                    ("wallFilter", S(), "structural"),
                    ("minWallThicknessMm", N(), null))),
                T("configure_grid_display", "Adjust existing grid extents/bubbles.", P(
                    ("gridTypeName", S(), null), ("bubbleEnd", S(), null),
                    ("xExtentMin", N(), null), ("xExtentMax", N(), null),
                    ("yExtentMin", N(), null), ("yExtentMax", N(), null))),
                T("dimension_grids", "Exterior axial dimensions from building envelope.", P(
                    ("firstOffsetMm", N(), null), ("tierGapMm", N(), null),
                    ("numericSide", S(), null), ("letterSide", S(), null), ("dimensionType", S(), null))),
                T("dimension_room_walls", "Interior width×depth for a room.", P(
                    ("roomId", N(), "Room element id"), ("placement", S(), "interior"),
                    ("offsetMm", N(), null), ("dimensionType", S(), null))),
                T("create_room", "Place rooms inside enclosed contours.", P(
                    ("data", A("object"), "{name, number, location:{x,y,z}}"))),
                T("tag_rooms", "Place room tags on the active view.", P(
                    ("tagTypeId", S(), null), ("roomIds", A("string"), null))),
                T("export_room_data", "Export room ids, names, areas.", P(
                    ("includeUnplacedRooms", B(), null), ("includeNotEnclosedRooms", B(), null))),
                T("create_door_schedule", "Door schedule (no slopes).", Empty()),
                T("create_window_schedule", "Window schedule (no slopes).", Empty()),
                T("create_floor_schedule", "Floor finish schedule (полы)*.", Empty()),
                T("create_floor_explication", "Floor explication + sheet layout.", P(
                    ("sheetFormat", S(), "A2"), ("autoLayout", B(), null))),
                T("create_sheet", "Create sheet with project title block.", P(
                    ("sheetNumber", S(), null), ("sheetName", S(), null), ("titleBlockName", S(), null))),
                T("place_view_on_sheet", "Place view/schedule on sheet (mm).", P(
                    ("sheetId", N(), null), ("viewId", N(), null),
                    ("positionX", N(), null), ("positionY", N(), null))),
                T("auto_layout_sheet", "Auto-pack views/schedules on sheet.", P(
                    ("sheetId", N(), null), ("sheetNumber", S(), null),
                    ("createSheetIfMissing", B(), null), ("avoidExisting", B(), null), ("order", S(), null))),
                T("check_evacuation_width", "Check evacuation corridor widths vs norms.", P(
                    ("levelName", S(), null))),
                T("check_room_depth", "Check living room depth vs norms.", P(
                    ("levelName", S(), null))),
                T("check_min_dimensions", "Check balcony/loggia/pier min dimensions.", P(
                    ("levelName", S(), null))),
                T("check_fire_doors", "Check fire door parameters/marks.", P(
                    ("levelName", S(), null))),
                T("get_room_geometry_metrics", "Room width/depth/area metrics for checks.", Empty()),
                T("create_filled_regions", "Filled regions for room areas / violations.", P(
                    ("roomIds", A("string"), null), ("colorPreset", S(), "red"), ("clearPrevious", B(), null))),
                T("create_text_notes", "Text notes with optional leaders (for findings).", P(
                    ("notes", A("object"), null))),
                T("apply_norm_result", "Write norm status into model parameters/marks.", P(
                    ("elements", A("object"), null))),
                T("operate_element", "Delete / hide / color elements.", P(
                    ("data", O(), "{action, elementIds}"))),
                T("delete_element", "Delete elements by id.", P(
                    ("elementIds", A("string"), null))),
                T("get_available_family_types", "List family types.", P(
                    ("categoryName", S(), null))),
                T("ai_element_filter", "Filter elements by category.", P(
                    ("categoryName", S(), null), ("filter", O(), null))),
                T("get_selected_elements", "Currently selected elements.", Empty()),
                T("color_splash", "View color scheme by parameter.", P(
                    ("categoryName", S(), null), ("parameterName", S(), null))),
                T("get_document_styles", "Annotation styles: dims, grids, text, title blocks.", Empty()),
                T("analyze_model_statistics", "Model statistics.", Empty()),
            };
        }

        private static ToolDef T(string name, string description, JObject parameters) =>
            new ToolDef { Name = name, Description = description, Parameters = parameters };

        private static JObject Empty() =>
            new JObject { ["type"] = "object", ["properties"] = new JObject() };

        private static JObject P(params (string name, JObject schema, string desc)[] items)
        {
            var props = new JObject();
            foreach (var item in items)
            {
                var copy = (JObject)item.schema.DeepClone();
                if (!string.IsNullOrEmpty(item.desc))
                    copy["description"] = item.desc;
                props[item.name] = copy;
            }
            return new JObject { ["type"] = "object", ["properties"] = props };
        }

        private static JObject S() => new JObject { ["type"] = "string" };
        private static JObject N() => new JObject { ["type"] = "number" };
        private static JObject B() => new JObject { ["type"] = "boolean" };
        private static JObject O() => new JObject { ["type"] = "object" };
        private static JObject A(string itemType) =>
            new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = itemType } };
    }
}
