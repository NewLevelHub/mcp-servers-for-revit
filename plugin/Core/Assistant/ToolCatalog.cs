using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Curated OpenAI-style tool schemas for the in-Revit agent (pilot scenarios).
    /// Filtered by intent profiles (REV-112) so the model never sees the full catalog.
    /// </summary>
    public static class ToolCatalog
    {
        public const int MaxToolsPerRequest = 30;

        public static class Profiles
        {
            public const string Core = "core";
            public const string Modeling = "modeling";
            public const string Annotation = "annotation";
            public const string Schedules = "schedules";
            public const string Sheets = "sheets";
            public const string Norms = "norms";
            public const string Data = "data";

            public static readonly string[] AllNonCore =
            {
                Modeling, Annotation, Schedules, Sheets, Norms, Data
            };
        }

        /// <summary>
        /// Tools always visible. Intent profiles add more (union capped at <see cref="MaxToolsPerRequest"/>).
        /// </summary>
        public static readonly IReadOnlyList<string> CoreTools = new[]
        {
            "get_current_view_info",
            "get_current_view_elements",
            "get_selected_elements",
            "get_available_family_types",
            "get_element_parameters",
            "set_element_parameter",
            "export_room_data",
            "operate_element",
            "delete_element",
            "query_norm_rules",
        };

        /// <summary>Profile name → tool names (excluding core; core is always merged).</summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ProfileTools =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [Profiles.Modeling] = new[]
                {
                    "create_line_based_element",
                    "create_point_based_element",
                    "create_surface_based_element",
                    "create_room",
                    "create_level",
                    "create_stair",
                    "create_railing",
                    "create_floor_opening",
                    "create_structural_framing_system",
                },
                [Profiles.Annotation] = new[]
                {
                    "create_grid",
                    "configure_grid_display",
                    "dimension_grids",
                    "dimension_room_walls",
                    "create_dimensions",
                    "tag_rooms",
                    "tag_all_rooms",
                    "tag_walls",
                    "tag_all_walls",
                    "create_text_notes",
                    "create_text_note",
                    "create_detail_lines",
                    "create_detail_view",
                    "place_detail_component",
                    "get_document_styles",
                    "color_splash",
                    "color_elements",
                },
                [Profiles.Schedules] = new[]
                {
                    "create_door_schedule",
                    "create_window_schedule",
                    "create_floor_schedule",
                    "create_floor_explication",
                    "create_schedule",
                    "configure_schedule",
                    "validate_schedule",
                    "create_finish_schedule",
                    "create_curtain_wall_schedule",
                    "get_schedule_definition",
                    "render_tep_table",
                    "export_tep_data",
                },
                [Profiles.Sheets] = new[]
                {
                    "create_sheet",
                    "place_view_on_sheet",
                    "auto_layout_sheet",
                    "fit_schedule_to_sheet",
                },
                [Profiles.Norms] = new[]
                {
                    "run_norm_audit",
                    "check_evacuation_width",
                    "check_room_depth",
                    "check_min_dimensions",
                    "check_fire_doors",
                    "create_filled_regions",
                    "annotate_norm_findings",
                    "apply_norm_result",
                    "create_text_notes",
                    "get_room_geometry_metrics",
                    "get_door_egress_info",
                    "get_opening_geometry_info",
                    "get_vertical_circulation_info",
                    "export_egress_graph",
                },
                [Profiles.Data] = new[]
                {
                    "export_apartment_data",
                    "export_room_finish_data",
                    "export_tep_data",
                    "get_material_quantities",
                    "analyze_model_statistics",
                    "ai_element_filter",
                    "get_elements_parameters",
                    "say_hello",
                    "batch_execute",
                    "send_code_to_revit",
                },
            };

        private static Dictionary<string, ToolDef> _definitionsByName;
        private static Dictionary<string, List<string>> _toolToProfiles;

        private static Dictionary<string, ToolDef> DefinitionsByName
        {
            get
            {
                EnsureLookups();
                return _definitionsByName;
            }
        }

        private static Dictionary<string, List<string>> ToolToProfiles
        {
            get
            {
                EnsureLookups();
                return _toolToProfiles;
            }
        }

        private static void EnsureLookups()
        {
            if (_definitionsByName != null)
                return;
            var byName = new Dictionary<string, ToolDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in Definitions)
                byName[def.Name] = def;

            var toProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Add(string toolName, string profile)
            {
                if (!toProfiles.TryGetValue(toolName, out var list))
                {
                    list = new List<string>();
                    toProfiles[toolName] = list;
                }
                if (!list.Exists(p => p.Equals(profile, StringComparison.OrdinalIgnoreCase)))
                    list.Add(profile);
            }

            foreach (var name in CoreTools)
                Add(name, Profiles.Core);
            foreach (var kv in ProfileTools)
            {
                foreach (var name in kv.Value)
                    Add(name, kv.Key);
            }

            _toolToProfiles = toProfiles;
            _definitionsByName = byName;
        }

        /// <summary>Full unfiltered catalog (tests / diagnostics only). Prefer <see cref="GetOpenAiTools(IEnumerable{string})"/>.</summary>
        public static JArray GetOpenAiTools()
        {
            return BuildToolsArray(Definitions.Select(d => d.Name), int.MaxValue);
        }

        /// <summary>
        /// Core ∪ requested profiles, stable order, capped at <see cref="MaxToolsPerRequest"/>.
        /// Null/empty profiles → core only.
        /// </summary>
        public static JArray GetOpenAiTools(IEnumerable<string> profiles)
        {
            var names = SelectToolNames(profiles, MaxToolsPerRequest);
            return BuildToolsArray(names, MaxToolsPerRequest);
        }

        public static int CountTools(IEnumerable<string> profiles) =>
            SelectToolNames(profiles, MaxToolsPerRequest).Count;

        public static IReadOnlyList<string> SelectToolNames(IEnumerable<string> profiles, int maxTools = MaxToolsPerRequest)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string name)
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    return;
                if (!DefinitionsByName.ContainsKey(name))
                    return;
                if (ordered.Count >= maxTools)
                    return;
                ordered.Add(name);
            }

            foreach (var name in CoreTools)
                TryAdd(name);

            if (profiles == null)
                return ordered;

            foreach (var profile in NormalizeProfiles(profiles))
            {
                if (profile.Equals(Profiles.Core, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ProfileTools.TryGetValue(profile, out var tools))
                    continue;
                foreach (var name in tools)
                    TryAdd(name);
            }

            return ordered;
        }

        public static bool IsToolAllowed(string toolName, IEnumerable<string> profiles)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;
            var allowed = new HashSet<string>(SelectToolNames(profiles, MaxToolsPerRequest), StringComparer.OrdinalIgnoreCase);
            return allowed.Contains(toolName.Trim());
        }

        /// <summary>Profiles that contain the tool (may include <see cref="Profiles.Core"/>).</summary>
        public static IReadOnlyList<string> ResolveProfilesForTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return Array.Empty<string>();
            if (!ToolToProfiles.TryGetValue(toolName.Trim(), out var list))
                return Array.Empty<string>();
            return list.ToArray();
        }

        /// <summary>
        /// Non-core profiles needed so <paramref name="toolName"/> becomes available,
        /// given currently active profiles.
        /// </summary>
        public static IReadOnlyList<string> GetMissingProfiles(string toolName, IEnumerable<string> activeProfiles)
        {
            var owning = ResolveProfilesForTool(toolName);
            if (owning.Count == 0)
                return Array.Empty<string>();
            if (owning.Any(p => p.Equals(Profiles.Core, StringComparison.OrdinalIgnoreCase)))
                return Array.Empty<string>();

            var active = new HashSet<string>(NormalizeProfiles(activeProfiles), StringComparer.OrdinalIgnoreCase);
            if (IsToolAllowed(toolName, active))
                return Array.Empty<string>();

            return owning
                .Where(p => !p.Equals(Profiles.Core, StringComparison.OrdinalIgnoreCase))
                .Where(p => !active.Contains(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// When a tool's profile is already active but the tool was truncated by the 30-tool cap,
        /// move those profiles to the front so <see cref="SelectToolNames"/> includes the tool.
        /// </summary>
        public static IReadOnlyList<string> PrioritizeProfilesForTool(
            string toolName,
            IEnumerable<string> activeProfiles)
        {
            var owning = ResolveProfilesForTool(toolName)
                .Where(p => !p.Equals(Profiles.Core, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (owning.Length == 0)
                return NormalizeProfiles(activeProfiles);

            return MergeProfiles(owning, activeProfiles);
        }

        public static IReadOnlyList<string> MergeProfiles(IEnumerable<string> current, IEnumerable<string> add)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in NormalizeProfiles(current).Concat(NormalizeProfiles(add)))
            {
                if (seen.Add(p))
                    list.Add(p);
            }
            return list;
        }

        public static IReadOnlyList<string> NormalizeProfiles(IEnumerable<string> profiles)
        {
            if (profiles == null)
                return Array.Empty<string>();
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in profiles)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var p = raw.Trim().ToLowerInvariant();
                if (p == Profiles.Core)
                    continue;
                if (!ProfileTools.ContainsKey(p) && !p.Equals("full", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p == "full")
                {
                    foreach (var all in Profiles.AllNonCore)
                    {
                        if (seen.Add(all))
                            list.Add(all);
                    }
                    continue;
                }
                if (seen.Add(p))
                    list.Add(p);
            }
            return list;
        }

        private static JArray BuildToolsArray(IEnumerable<string> names, int maxTools)
        {
            var tools = new JArray();
            foreach (var name in names)
            {
                if (tools.Count >= maxTools)
                    break;
                if (!DefinitionsByName.TryGetValue(name, out var def))
                    continue;
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

        /// <summary>
        /// Only irreversible / high-risk actions ask for a click.
        /// Create, dimension, tag, schedules, highlight — run immediately (pilot UX).
        /// </summary>
        public static bool RequiresConfirmation(string toolName, string argsJson = null)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            var n = toolName.Trim();

            if (n.Equals("delete_element", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.Equals("send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                return true;

            if (n.Equals("operate_element", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var args = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                    var action = args["data"]?["action"]?.ToString()
                        ?? args["action"]?.ToString()
                        ?? "";
                    return action.Equals("Delete", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }

        public static string HumanizeFailure(string toolName, string rawMessage)
        {
            var label = FriendlyName(toolName);
            var msg = rawMessage ?? "";
            if (msg.IndexOf("typeId is required", StringComparison.OrdinalIgnoreCase) >= 0
                || (msg.IndexOf("typeId", StringComparison.OrdinalIgnoreCase) >= 0
                    && msg.IndexOf("required", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return "Не передан typeId стены. Сначала get_available_family_types (OST_Walls) и подставьте число typeId.";
            }
            if (msg.IndexOf("is not a WallType", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "typeId не является типом стены. Вызовите get_available_family_types с categoryName=OST_Walls и возьмите typeId из списка.";
            }
            if (msg.IndexOf("typeId", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "typeId не найден в проекте. Возьмите актуальный typeId из get_available_family_types (OST_Walls).";
            }
            if (msg.IndexOf("typeId", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("type id", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Проблема с typeId. Вызовите get_available_family_types categoryName=OST_Walls и передайте числовой typeId в create_line_based_element.";
            }
            if (msg.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Для двери/окна нужна стена-хост. Укажите стену или создайте проём в существующей стене.";
            }
            if (msg.IndexOf("Method not found", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("未找到方法", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return $"Команда «{label}» недоступна. Включите её в Настройки → Наборы команд и перезапустите сервер.";
            }
            if (msg.IndexOf("title block", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("рамк", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "В проекте нет подходящей рамки (основная надпись). Добавьте семейство из шаблона.";
            }
            if (msg.IndexOf("viewId", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("viewUniqueId", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Не удалось разместить на лист: нет id вида/спецификации. " +
                       "Сначала создайте вид или спеку и возьмите id из ответа, либо для ТЭП используйте таблицу ТЭП.";
            }
            if (msg.IndexOf("Create room operation timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                    && msg.IndexOf("room", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Создание помещений заняло слишком долго. Создавайте по 1–2 помещения за вызов, " +
                       "после замкнутого контура стен.";
            }
            if (msg.IndexOf("was not found", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("Room", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Для размеров нужен ElementId помещения из ответа create_room, а не номер 1/2/3.";
            }
            if (msg.IndexOf("Избыточн", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Redundant", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Несколько помещений в одной области — сначала постройте стены-перегородки, потом комнаты по ячейкам.";
            }
            if (msg.IndexOf("locationLine", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Значение не может быть неопределенным", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("data is required", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Стены: передайте data[{typeId, locationLine:{p0,p1}, height, baseLevel, baseOffset}]. " +
                       "typeId — число из get_available_family_types. Без стен помещения не создавайте.";
            }
            if (msg.IndexOf("Граница помещения", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Room Bounding", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "«Граница помещения» есть только у стен. Укажите id стены, не помещения.";
            }
            if (msg.IndexOf("No grid positions", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("No wall centerlines matched", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Оси ставятся по уже существующим стенам. Чтобы построить новые стены — сначала типы из проекта, " +
                       "затем линейные элементы (стены) по контуру; create_grid для этого не подходит.";
            }
            if (msg.IndexOf("参数无效", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("没有指定要操作", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Не указаны elementIds", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Не указаны id элементов. Для дверей — doorElementIds из run_norm_audit; " +
                       "для помещений — roomIds. Или run_norm_audit mode=highlight.";
            }
            if (msg.IndexOf("操作元素", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Ошибка операции с элементом", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Не удалось изменить элементы на виде. Проверьте id из аудита или вызовите run_norm_audit mode=highlight.";
            }
            if (msg.IndexOf("Укажите roomIds", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Для заливки нужны roomIds из run_norm_audit. Без них весь этаж не заливается — это защита.";
            }

            var firstLine = msg.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (firstLine.Length > 280)
                firstLine = firstLine.Substring(0, 277) + "…";
            return firstLine;
        }

        /// <summary>
        /// Architect-facing RU label for UI (confirmations, «Сделано»). Internal tool ids stay English.
        /// </summary>
        public static string FriendlyName(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return "операция";

            var key = toolName.Trim();
            if (DisplayNamesRu.TryGetValue(key, out var ru))
                return ru;

            // Unknown / future tools: readable fallback without exposing snake_case jargon.
            return "действие";
        }

        private static readonly Dictionary<string, string> DisplayNamesRu =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["say_hello"] = "проверка связи",
                ["query_norm_rules"] = "поиск норм",
                ["get_current_view_info"] = "сведения о виде",
                ["get_current_view_elements"] = "элементы вида",
                ["get_selected_elements"] = "выбранные элементы",
                ["get_available_family_types"] = "типы семейств",
                ["get_document_styles"] = "стили документа",
                ["get_room_geometry_metrics"] = "геометрия помещений",
                ["analyze_model_statistics"] = "статистика модели",
                ["ai_element_filter"] = "фильтр элементов",
                ["export_room_data"] = "данные помещений",
                ["create_grid"] = "оси",
                ["create_line_based_element"] = "стены / линейные элементы",
                ["create_point_based_element"] = "двери / окна",
                ["create_surface_based_element"] = "полы / потолки",
                ["set_element_parameter"] = "параметр элемента",
                ["configure_grid_display"] = "настройка осей",
                ["dimension_grids"] = "осевые размеры",
                ["dimension_room_walls"] = "размеры помещений",
                ["create_room"] = "помещения",
                ["tag_rooms"] = "марки помещений",
                ["create_door_schedule"] = "спецификация дверей",
                ["create_window_schedule"] = "спецификация окон",
                ["create_floor_schedule"] = "экспликация полов",
                ["create_floor_explication"] = "лист экспликации полов",
                ["export_tep_data"] = "данные ТЭП",
                ["render_tep_table"] = "таблица ТЭП на листе",
                ["create_sheet"] = "лист",
                ["place_view_on_sheet"] = "размещение на листе",
                ["auto_layout_sheet"] = "раскладка на листе",
                ["fill_title_block"] = "штамп",
                ["check_evacuation_width"] = "ширина эвак. коридора",
                ["check_room_depth"] = "глубина помещений",
                ["check_min_dimensions"] = "мин. размеры лоджий/балконов",
                ["check_fire_doors"] = "противопожарные двери",
                ["create_filled_regions"] = "цветовая область",
                ["create_text_notes"] = "текстовые замечания",
                ["create_text_note"] = "текстовая заметка",
                ["annotate_norm_findings"] = "подписи нарушений",
                ["apply_norm_result"] = "запись статуса норм",
                ["operate_element"] = "изменение элементов",
                ["delete_element"] = "удаление",
                ["color_splash"] = "цветовая схема",
                ["color_elements"] = "цветовая схема",
                ["number_rooms"] = "нумерация помещений",
                ["send_code_to_revit"] = "выполнение кода",
                ["run_norm_audit"] = "проверка норм (сводная)",
                ["tag_all_rooms"] = "марки помещений",
                ["tag_all_walls"] = "марки стен",
                ["tag_walls"] = "марки стен",
                ["create_stair"] = "лестница",
                ["create_railing"] = "ограждение",
                ["create_floor_opening"] = "проём / шахта",
                ["create_level"] = "уровень",
                ["create_dimensions"] = "размеры",
                ["get_element_parameters"] = "параметры элемента",
                ["get_elements_parameters"] = "параметры элементов",
                ["get_material_quantities"] = "объёмы материалов",
                ["batch_execute"] = "пакет команд",
                ["export_apartment_data"] = "квартирография",
                ["export_room_finish_data"] = "отделка помещений",
                ["validate_schedule"] = "проверка спецификации",
                ["create_schedule"] = "спецификация",
                ["configure_schedule"] = "настройка спецификации",
                ["create_finish_schedule"] = "ведомость отделки",
                ["create_curtain_wall_schedule"] = "спецификация витражей",
                ["get_schedule_definition"] = "определение спецификации",
                ["fit_schedule_to_sheet"] = "подгонка спеки на лист",
                ["get_door_egress_info"] = "эвак. данные дверей",
                ["get_opening_geometry_info"] = "геометрия проёмов",
                ["get_vertical_circulation_info"] = "лестницы / пандусы",
                ["export_egress_graph"] = "граф эвакуации",
                ["create_detail_lines"] = "линии деталировки",
                ["create_detail_view"] = "вид детали",
                ["place_detail_component"] = "узел деталировки",
                ["create_structural_framing_system"] = "балочная система",
            };

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
                T("query_norm_rules",
                    "Search the offline GOST/СП/СН РК norm catalog by topic (e.g. «ширина коридора», «глубина лоджии»). " +
                    "Returns document/clause/quote — cite only these, never invent norms. " +
                    "If catalogMissing=true, tell the architect to run seed + export:norm-catalog.",
                    P(("topic", S(), "natural language topic"),
                      ("limit", N(), "max rules, default 5"))),
                T("get_available_family_types",
                    "List family types. For walls MUST pass categoryName=\"OST_Walls\" (or categoryList:[\"OST_Walls\"]). " +
                    "Returns typeId numbers — copy typeId into create_line_based_element. Call once per category.",
                    P(("categoryName", S(), "OST_Walls | OST_Doors | OST_Windows | OST_Floors"),
                      ("categoryList", A("string"), "[\"OST_Walls\"]"),
                      ("limit", N(), "40"))),
                T("create_line_based_element",
                    "Create walls along lines (mm). REQUIRED data array. Each item MUST have: " +
                    "category=\"OST_Walls\", typeId (number from get_available_family_types), " +
                    "locationLine:{p0:{x,y,z}, p1:{x,y,z}} (mm, z usually 0), height (mm, e.g. 3000), " +
                    "baseLevel (mm = level elevation from get_current_view_info), baseOffset (usually 0). " +
                    "Batch all wall segments in one call. If this fails — STOP, do not create rooms. NOT create_grid.",
                    P(("data", A("object"),
                        "REQUIRED array [{category:\"OST_Walls\", typeId:123, locationLine:{p0:{x,y,z:0}, p1:{x,y,z:0}}, height:3000, baseLevel:0, baseOffset:0}]"))),
                T("create_room",
                    "Place rooms ONLY after walls exist and enclose cells. Point must be inside its wall cell. " +
                    "1–2 rooms per call. Save response Id as ElementId for dimension_room_walls (NOT room number 1,2,3). " +
                    "If walls failed — do NOT call this.",
                    P(
                    ("data", A("object"), "{name, number, location:{x,y,z}}"))),
                T("dimension_room_walls",
                    "Interior width×depth. roomId = Revit ElementId from create_room response (e.g. 1820053), NEVER room number 1/2/3.",
                    P(
                    ("roomId", N(), "ElementId from create_room"), ("placement", S(), "interior"),
                    ("offsetMm", N(), null), ("dimensionType", S(), null))),
                T("set_element_parameter",
                    "Set parameter. «Room Bounding»/«Граница помещения»=true ONLY on Wall element ids, never on Room ids.",
                    P(("elementId", N(), "wall id"),
                      ("parameterName", S(), "Room Bounding"),
                      ("value", O(), "true"))),
                T("create_point_based_element",
                    "Create doors, windows, furniture at a point (mm). REQUIRED: typeId + hostWallId for doors/windows. " +
                    "Get hostWallId from create_line_based_element / get_current_view_elements OST_Walls after walls exist.",
                    P(("data", A("object"),
                        "{typeId, locationPoint:{x,y,z}, hostWallId, baseLevel, baseOffset, height, width}"))),
                T("create_surface_based_element",
                    "Create floors, ceilings, roofs from boundary loops (mm). REQUIRED: typeId from get_available_family_types (OST_Floors).",
                    P(("data", A("object"),
                        "{typeId, category:OST_Floors, boundary:{outerLoop:[{p0,p1}...]}, baseLevel, baseOffset}"))),
                T("create_grid",
                    "Create coordination GRIDS (оси) — NOT walls. Only when walls already exist; autoFromWalls=true.",
                    P(
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
                T("tag_rooms", "Place room tags (марки помещений) on the active view; prefer type with area.", P(
                    ("tagTypeId", S(), null), ("roomIds", A("string"), null))),
                T("export_room_data", "Export room ids, names, areas.", P(
                    ("includeUnplacedRooms", B(), null), ("includeNotEnclosedRooms", B(), null))),
                T("create_door_schedule",
                    "Door schedule (спецификация дверей). NOT TEP / not ТЭП. Returns schedule id for place_view_on_sheet / auto_layout_sheet.",
                    Empty()),
                T("create_window_schedule",
                    "Window schedule (спецификация окон). NOT TEP / not ТЭП.",
                    Empty()),
                T("create_floor_schedule", "Floor finish schedule (полы)*.", Empty()),
                T("create_floor_explication", "Floor explication + sheet layout.", P(
                    ("sheetFormat", S(), "A2"), ("autoLayout", B(), null))),
                T("export_tep_data",
                    "Read technical-economic indicators (ТЭП) numbers from the model. " +
                    "Prefer render_tep_table to draw ТЭП on a sheet in one step.",
                    P(("includeLevels", B(), null), ("includeRoomsByPurpose", B(), null))),
                T("render_tep_table",
                    "REQUIRED for «ТЭП», «тэп проекта», «технико-экономические показатели». " +
                    "Draws TEP table on a sheet (creates sheet if missing). " +
                    "Do NOT use create_door_schedule or place_view_on_sheet for TEP. " +
                    "Pass sheetName/sheetNumber of the target sheet (e.g. existing «Короткий блок» / A2).",
                    P(("sheetName", S(), "target sheet name"),
                      ("sheetNumber", S(), "sheet number if creating"),
                      ("createSheetIfMissing", B(), "true"),
                      ("title", S(), "Технико-экономические показатели"),
                      ("templateScheduleName", S(), "e.g. О_АР_Квартиры_ТЭП if exists"),
                      ("positionX", N(), "50"), ("positionY", N(), "40"),
                      ("includeLevels", B(), "true"), ("includeRoomsByPurpose", B(), "true"))),
                T("create_sheet", "Create sheet with project title block.", P(
                    ("sheetNumber", S(), null), ("sheetName", S(), null), ("titleBlockName", S(), null))),
                T("place_view_on_sheet",
                    "Place an existing floor plan or schedule on a sheet. " +
                    "REQUIRED: viewId (or viewUniqueId) from create_* / get_* result AND sheetId. " +
                    "Do not call without viewId. Not for TEP — use render_tep_table.",
                    P(("sheetId", N(), "required sheet element id"),
                      ("viewId", N(), "REQUIRED view or schedule element id"),
                      ("positionX", N(), "mm from sheet lower-left"),
                      ("positionY", N(), "mm from sheet lower-left"),
                      ("placement", O(), "optional nested {sheetId,viewId,positionX,positionY}"))),
                T("auto_layout_sheet",
                    "Auto-pack existing views/schedules on a sheet. Pass real view ids in items. Not for TEP table.",
                    P(("sheetId", N(), null), ("sheetNumber", S(), null),
                      ("createSheetIfMissing", B(), null), ("avoidExisting", B(), null), ("order", S(), null))),
                T("check_evacuation_width",
                    "Norm check: evacuation corridor width vs minWidthMm (auto 1200 mm / СП РК if omitted). " +
                    "Returns violators with ids + source citation. Part of floor norm-audit.",
                    P(("levelName", S(), null), ("minWidthMm", N(), "1200"),
                      ("mode", S(), "report"))),
                T("check_room_depth",
                    "Norm check: living room depth vs maxDepthMm (auto 6000 mm / СП РК п.4.4.10.22 if omitted). " +
                    "Returns violators with ids + source. Part of floor norm-audit.",
                    P(("levelName", S(), null), ("maxDepthMm", N(), "6000"),
                      ("roomScope", S(), "living"), ("mode", S(), "report"))),
                T("check_min_dimensions",
                    "Norm check: balcony/loggia/pier min size. " +
                    "For ordinary housing do NOT pass minBalconyWidthMm/minLoggiaWidthMm=1400 " +
                    "(1.4 m is МГН / п.4.6.5 only). Default catalog uses fire-path/pier limits (~1200). " +
                    "Pass housingType=mgn only when user asks МГН. Returns violators + source.",
                    P(("levelName", S(), null),
                      ("minFirePathOutdoorWidthMm", N(), "1200 for Н1 path only"),
                      ("minFirePierToOpeningMm", N(), "1200"),
                      ("minFirePierBetweenOpeningsMm", N(), null),
                      ("minBalconyWidthMm", N(), "only if МГН — do not use 1400 for ordinary"),
                      ("minLoggiaWidthMm", N(), "only if МГН"),
                      ("mode", S(), "report"))),
                T("check_fire_doors",
                    "Norm check: fire doors. Returns doors with requiresFireDoor/compliant/reason/source " +
                    "and annotationHints[{elementId,text,leader}] for create_text_notes. " +
                    "Paint non-compliant doors red AND place leaders — never color without callouts.",
                    P(("levelName", S(), null))),
                T("get_room_geometry_metrics", "Room width/depth/area metrics for checks.", Empty()),
                T("create_filled_regions",
                    "Paint room areas as Filled Region (цветовая область). " +
                    "After successful norm checks ONLY: pass roomIds of violators, colorPreset=red, clearPrevious=true. " +
                    "Do not call with empty roomIds (that paints all rooms). " +
                    "To remove prior MCP markup without painting: clearOnly=true.",
                    P(("roomIds", A("string"), "violating room element ids"),
                      ("colorPreset", S(), "red"), ("clearPrevious", B(), "true"),
                      ("clearOnly", B(), "true = only remove prior MCP-FR regions"))),
                T("create_text_notes",
                    "Text notes with leaders (выноски) for rooms AND doors. " +
                    "Prefer annotationHints from check_fire_doors / other checks as notes. " +
                    "notes=[{text, elementId, leader:true}], clearPrevious=true, textTypeName=ADSK_Замечания. " +
                    "To remove prior MCP callouts without creating notes: clearOnly=true. " +
                    "Required for every red-painted door — SetColor alone is incomplete.",
                    P(("notes", A("object"), "[{text, elementId, leader}] or annotationHints"),
                      ("clearPrevious", B(), "true"),
                      ("clearOnly", B(), "true = only remove prior MCP notes/leaders"),
                      ("textTypeName", S(), "ADSK_Замечания"))),
                T("apply_norm_result", "Write norm status into model parameters/marks.", P(
                    ("elements", A("object"), null))),
                T("operate_element",
                    "Delete / hide / color / reset view overrides. After fire-door check: SetColor red on compliant=false doors, " +
                    "then always create_text_notes for the same ids. " +
                    "To clear norm markup: action=ResetOverrides, elementIds=[], categoryNames=[Doors,Windows,Ramps].",
                    P(("data", O(), "{action, elementIds, categoryNames?}"))),
                T("delete_element", "Delete elements by id.", P(
                    ("elementIds", A("string"), null))),
                T("ai_element_filter", "Filter elements by category.", P(
                    ("categoryName", S(), null), ("filter", O(), null))),
                T("get_selected_elements", "Currently selected elements.", Empty()),
                T("color_splash", "View color scheme by parameter.", P(
                    ("categoryName", S(), null), ("parameterName", S(), null))),
                T("get_document_styles", "Annotation styles: dims, grids, text, title blocks.", Empty()),
                T("analyze_model_statistics", "Model statistics.", Empty()),

                // --- Full MCP parity (Revit commands from command.json) ---
                T("run_norm_audit",
                    "Unified norm audit for active floor: evacuation width, room depth, min dimensions, fire doors. " +
                    "Returns findings[] + skippedRules. For violations display: create_filled_regions + operate_element + annotate_norm_findings. " +
                    "Prefer over calling many check_* separately when user says «проверь этаж по нормам».",
                    P(("levelName", S(), null), ("mode", S(), "report|highlight"),
                      ("includeCompliant", B(), null), ("topics", A("string"), null))),
                T("annotate_norm_findings",
                    "Place leader callouts for run_norm_audit findings. Template: name: actual < required · document clause.",
                    P(("findings", A("object"), "from run_norm_audit violation+nearLimit"),
                      ("style", S(), "leader"), ("textTypeName", S(), "ADSK_Замечания"),
                      ("clearPrevious", B(), "true"))),
                T("tag_all_rooms", "Alias for tag_rooms — place room tags with area.", P(
                    ("tagTypeId", S(), null), ("roomIds", A("string"), null))),
                T("tag_walls", "Tag all walls on the active view.", Empty()),
                T("tag_all_walls", "Alias for tag_walls.", Empty()),
                T("color_elements",
                    "View color scheme by parameter (NOT filled regions for violations — use create_filled_regions).",
                    P(("categoryName", S(), "Помещения"), ("parameterName", S(), "Имя"),
                      ("useGradient", B(), null))),
                T("create_stair",
                    "Create stair: layout straight/L/U, typeId (StairsType), widthMm, shaftRect or path. Units mm.",
                    P(("typeId", N(), null), ("layout", S(), "straight"), ("widthMm", N(), null),
                      ("baseLevelId", N(), null), ("topLevelId", N(), null), ("shaftRect", O(), null))),
                T("create_railing",
                    "Create railing by path or host stair. typeId required (RailingType).",
                    P(("typeId", N(), null), ("hostElementId", N(), null), ("pathPoints", A("object"), null),
                      ("levelId", N(), null))),
                T("create_floor_opening",
                    "Floor cut or vertical shaft. mode=floor|shaft, rect or boundaryPoints mm.",
                    P(("mode", S(), "floor|shaft"), ("hostFloorId", N(), null), ("levelId", N(), null),
                      ("baseLevelId", N(), null), ("topLevelId", N(), null), ("rect", O(), null))),
                T("create_level", "Create level at elevation mm with optional floor plan view.", P(
                    ("name", S(), null), ("elevationMm", N(), null))),
                T("create_dimensions", "Create dimension chains between points or elements.", P(
                    ("data", A("object"), null))),
                T("create_structural_framing_system", "Beam system with spacing and direction.", P(
                    ("data", O(), null))),
                T("get_element_parameters", "Read all parameters of one element by id.", P(
                    ("elementId", N(), "required"))),
                T("get_elements_parameters", "Batch read parameters (max 100 ids).", P(
                    ("elementIds", A("number"), null))),
                T("get_material_quantities", "Material takeoffs with areas and volumes.", Empty()),
                T("export_apartment_data", "Apartment grouping + СП РК area coefficients.", Empty()),
                T("export_room_finish_data", "Room finish parameters (walls/floors/ceilings).", P(
                    ("levelName", S(), null), ("limit", N(), null), ("offset", N(), null))),
                T("validate_schedule", "Compare schedule counts vs model (Doors/Windows/Floors).", P(
                    ("category", S(), "Doors|Windows|Floors|CurtainWalls"))),
                T("create_schedule", "Create ViewSchedule from template.", P(
                    ("scheduleName", S(), null), ("templateName", S(), null))),
                T("configure_schedule", "Edit existing schedule columns/filters.", P(
                    ("scheduleId", N(), null), ("changes", O(), null))),
                T("fit_schedule_to_sheet", "Fit schedule to sheet width.", P(
                    ("scheduleId", N(), null), ("sheetId", N(), null))),
                T("create_finish_schedule", "Room finish schedule chain (ADSK).", P(
                    ("templateName", S(), null))),
                T("create_curtain_wall_schedule", "Curtain wall schedule data.", Empty()),
                T("get_schedule_definition", "Read schedule fields/filters.", P(
                    ("scheduleId", N(), null), ("scheduleName", S(), null))),
                T("get_door_egress_info", "Door widths, egress paths, ramp slopes.", P(
                    ("levelName", S(), null))),
                T("get_opening_geometry_info", "Window sill height, opening height.", P(
                    ("levelName", S(), null))),
                T("get_vertical_circulation_info", "Stair/ramp/railing geometry for norms.", P(
                    ("levelName", S(), null))),
                T("export_egress_graph", "Egress walkability graph for floor.", P(
                    ("levelName", S(), null))),
                T("create_detail_lines", "Detail polylines on plan/detail view (mm).", P(
                    ("points", A("object"), null), ("viewId", N(), null))),
                T("create_detail_view", "Detail callout or drafting view.", P(
                    ("name", S(), null), ("parentViewId", N(), null), ("scale", N(), null))),
                T("place_detail_component", "Place 2D detail component on detail view.", P(
                    ("data", A("object"), null))),
                T("create_text_note", "Single text note with optional leader.", P(
                    ("text", S(), null), ("location", O(), null), ("leader", B(), null))),
                T("batch_execute",
                    "Run up to 20 Revit commands in one request. commands=[{method, params}].",
                    P(("commands", A("object"), "[{method, params}]"))),
                T("send_code_to_revit",
                    "Execute C# in Revit. ONLY if user explicitly allowed. Prefer create_* tools.",
                    P(("code", S(), "C# source"), ("description", S(), null))),
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
