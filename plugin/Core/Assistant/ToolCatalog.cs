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
            "declare_plan",
            "ask_user",
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
                    "get_cad_link_geometry",
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
                    "tag_walls",
                    "create_text_notes",
                    "create_text_note",
                    "create_detail_lines",
                    "create_detail_view",
                    "place_detail_component",
                    "get_document_styles",
                    "color_splash",
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
                    "get_room_geometry_metrics",
                    "run_norm_audit",
                    "check_evacuation_width",
                    "check_room_depth",
                    "check_min_dimensions",
                    "check_fire_doors",
                    "create_filled_regions",
                    "annotate_norm_findings",
                    "apply_norm_result",
                    "create_text_notes",
                    "get_door_egress_info",
                    "get_opening_geometry_info",
                    "get_vertical_circulation_info",
                },
                [Profiles.Data] = new[]
                {
                    "get_room_geometry_metrics",
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
            var canonical = ResolveToolAlias(toolName);
            var allowed = new HashSet<string>(SelectToolNames(profiles, MaxToolsPerRequest), StringComparer.OrdinalIgnoreCase);
            return allowed.Contains(canonical);
        }

        /// <summary>
        /// Map legacy MCP-facing names to the single canonical Revit command (REV-116).
        /// Aliases are not listed in <see cref="Definitions"/> so the model sees one tool per action.
        /// </summary>
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

        /// <summary>
        /// Soft-error message when the model invents a tool outside the assistant catalog.
        /// Server-only MCP tools get a clearer «use Cursor» hint (REV-116).
        /// </summary>
        public static string DescribeUnavailableTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return "Инструмент недоступен в каталоге ассистента.";

            switch (toolName.Trim().ToLowerInvariant())
            {
                case "fill_title_block":
                    return "Заполнение штампа (fill_title_block) есть только в Cursor MCP, в чате Revit этой команды нет.";
                case "number_rooms":
                    return "Нумерация помещений (number_rooms) есть только в Cursor MCP, в чате Revit этой команды нет.";
                case "export_egress_graph":
                    return "Граф эвакуации — внутренний строительный блок. Используйте run_norm_audit или check_evacuation_width.";
                default:
                    return "Инструмент недоступен в каталоге ассистента.";
            }
        }

        /// <summary>Profiles that contain the tool (may include <see cref="Profiles.Core"/>).</summary>
        public static IReadOnlyList<string> ResolveProfilesForTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return Array.Empty<string>();
            var canonical = ResolveToolAlias(toolName);
            if (!ToolToProfiles.TryGetValue(canonical, out var list))
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

        /// <summary>Parameter property names declared for a tool (empty if unknown).</summary>
        public static IReadOnlyList<string> GetParameterPropertyNames(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return Array.Empty<string>();
            var canonical = ResolveToolAlias(toolName);
            if (!DefinitionsByName.TryGetValue(canonical, out var def))
                return Array.Empty<string>();
            var props = def.Parameters?["properties"] as JObject;
            if (props == null)
                return Array.Empty<string>();
            return props.Properties().Select(p => p.Name).ToList();
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
        /// Irreversible / high-risk tool families (delete, send_code). Threshold applied via
        /// <see cref="ShouldConfirm"/>.
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

        /// <summary>
        /// REV-125: when confirmations are enabled, send_code always asks; deletes ask only if
        /// target count ≥ <paramref name="deleteThreshold"/> (default 20).
        /// </summary>
        public static bool ShouldConfirm(
            string toolName,
            string argsJson,
            bool requireConfirmations,
            int deleteThreshold = 20)
        {
            if (!requireConfirmations)
                return false;
            if (!RequiresConfirmation(toolName, argsJson))
                return false;

            var n = (toolName ?? "").Trim();
            if (n.Equals("send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                return true;

            var threshold = deleteThreshold < 1 ? 1 : deleteThreshold;
            var count = DeleteConfirmSummary.CountTargets(toolName, argsJson);
            return count >= threshold;
        }

        /// <summary>Structured failure text for UI + model (REV-119): what broke vs how to fix.</summary>
        public struct FailureHint
        {
            public string Error;
            public string Fix;

            public FailureHint(string error, string fix = null)
            {
                Error = error ?? "ошибка";
                Fix = fix;
            }

            /// <summary>Single-line form for UI «Сделано» / logs.</summary>
            public string Combined
            {
                get
                {
                    if (string.IsNullOrWhiteSpace(Fix))
                        return Error;
                    return Error + " " + Fix;
                }
            }
        }

        /// <summary>Backward-compatible single string (UI). Prefer <see cref="DescribeFailure"/> for model payloads.</summary>
        public static string HumanizeFailure(string toolName, string rawMessage)
        {
            return DescribeFailure(toolName, rawMessage).Combined;
        }

        public static FailureHint DescribeFailure(string toolName, string rawMessage)
        {
            var label = FriendlyName(toolName);
            var msg = rawMessage ?? "";
            if (msg.IndexOf("typeId is required", StringComparison.OrdinalIgnoreCase) >= 0
                || (msg.IndexOf("typeId", StringComparison.OrdinalIgnoreCase) >= 0
                    && msg.IndexOf("required", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new FailureHint(
                    "Не передан typeId стены.",
                    "Сначала get_available_family_types (OST_Walls) и подставьте число typeId.");
            }
            if (msg.IndexOf("is not a WallType", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "typeId не является типом стены.",
                    "Вызовите get_available_family_types с categoryName=OST_Walls и возьмите typeId из списка.");
            }
            if (msg.IndexOf("typeId", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var cat = TypeIdCategoryHint(toolName);
                return new FailureHint(
                    "typeId не найден в проекте.",
                    "Возьмите актуальный typeId из get_available_family_types (" + cat + ").");
            }
            if (msg.IndexOf("typeId", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("type id", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var cat = TypeIdCategoryHint(toolName);
                return new FailureHint(
                    "Проблема с typeId.",
                    "Вызовите get_available_family_types categoryName=" + cat +
                    " и передайте числовой typeId.");
            }
            if (msg.IndexOf("locationPoint is too far from host", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("too far from host wall", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Точка двери не на стене-хосте.",
                    "Возьмите hostWallId и середину Start/End той же стены из элементов вида.");
            }
            if (msg.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Для двери/окна нужна стена-хост.",
                    "Укажите стену или создайте проём в существующей стене.");
            }
            if (msg.IndexOf("Method not found", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("未找到方法", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    $"Команда «{label}» недоступна.",
                    "Включите её в Настройки → Наборы команд и перезапустите сервер.");
            }
            if (msg.IndexOf("title block", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("рамк", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "В проекте нет подходящей рамки (основная надпись).",
                    "Добавьте семейство из шаблона.");
            }
            if (msg.IndexOf("viewId", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("viewUniqueId", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Не удалось разместить на лист: нет id вида/спецификации.",
                    "Сначала создайте вид или спеку и возьмите id из ответа, либо для ТЭП используйте таблицу ТЭП.");
            }
            if (msg.IndexOf("Create room operation timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || (msg.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                    && msg.IndexOf("room", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new FailureHint(
                    "Создание помещений заняло слишком долго.",
                    "Создавайте по 1–2 помещения за вызов, после замкнутого контура стен.");
            }
            if (msg.IndexOf("create_line_based_element timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || (msg.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                    && msg.IndexOf("line-based", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new FailureHint(
                    "Создание стен превысило 60 с.",
                    "Возьми suggestedWallTypeId (Базовая стена / перегородка), не Витраж; меньше сегментов за вызов; свободная зона на плане.");
            }
            if (msg.IndexOf("was not found", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("Room", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Помещение не найдено по id.",
                    "Для размеров нужен ElementId помещения из ответа create_room, а не номер 1/2/3.");
            }
            if (msg.IndexOf("Избыточн", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Redundant", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Несколько помещений в одной области.",
                    "Сначала постройте стены-перегородки, потом комнаты по ячейкам.");
            }
            if (msg.IndexOf("locationLine", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Значение не может быть неопределенным", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("data is required", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Некорректные аргументы создания стен.",
                    "Передайте data[{typeId, locationLine:{p0,p1}, height, baseLevel, baseOffset}]. " +
                    "typeId — число из get_available_family_types. Без стен помещения не создавайте.");
            }
            if (msg.IndexOf("Граница помещения", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Room Bounding", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "«Граница помещения» есть только у стен.",
                    "Укажите id стены, не помещения.");
            }
            if (msg.IndexOf("No grid positions", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("No wall centerlines matched", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "create_grid не нашёл стены под фильтр (часто: перегородки тоньше minWallThicknessMm).",
                    "Повтори autoFromWalls с wallFilter=all и minWallThicknessMm=100 (или 50). " +
                    "Не говори «стен нет», если на виде уже есть стены. create_grid не создаёт стены.");
            }
            if (msg.IndexOf("参数无效", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("没有指定要操作", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Не указаны elementIds", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Не указаны id элементов.",
                    "Для дверей — doorElementIds из run_norm_audit; для помещений — roomIds. Или run_norm_audit mode=highlight.");
            }
            if (msg.IndexOf("操作元素", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("Ошибка операции с элементом", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Не удалось изменить элементы на виде.",
                    "Проверьте id из аудита или вызовите run_norm_audit mode=highlight.");
            }
            if (msg.IndexOf("Укажите roomIds", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new FailureHint(
                    "Для заливки нужны roomIds из run_norm_audit.",
                    "Без них весь этаж не заливается — это защита.");
            }

            var firstLine = msg.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var text = firstLine.Length > 0 ? firstLine[0].Trim() : "ошибка";
            if (text.Length > 280)
                text = text.Substring(0, 277) + "…";
            return new FailureHint(text);
        }

        private static string TypeIdCategoryHint(string toolName)
        {
            var n = (toolName ?? "").Trim().ToLowerInvariant();
            if (n.Contains("point") || n.Contains("door") || n.Contains("window"))
                return "OST_Doors";
            if (n.Contains("surface") || n.Contains("floor"))
                return "OST_Floors";
            return "OST_Walls";
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
                ["declare_plan"] = "план",
                ["ask_user"] = "вопрос",
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
                // Legacy aliases (not in Definitions) — FriendlyName after ResolveToolAlias or raw model name.
                ["color_elements"] = "цветовая схема",
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
                ["get_cad_link_geometry"] = "геометрия DWG/CAD",
                ["get_vertical_circulation_info"] = "лестницы / пандусы",
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
            var xyz = XyzMm();
            var locationLine = P(R("p0", "p1"),
                ("p0", xyz, "start point, mm"),
                ("p1", xyz, "end point, mm"));
            var lineItem = P(R("category", "typeId", "locationLine"),
                ("category", S(), "OST_Walls"),
                ("typeId", N(), "from get_available_family_types"),
                ("locationLine", locationLine, "wall centerline, mm"),
                ("height", N(), "mm, e.g. 3000"),
                ("baseLevel", N(), "mm = level elevation"),
                ("baseOffset", N(), "mm, usually 0"));
            var pointItem = P(R("typeId", "hostWallId", "locationPoint"),
                ("typeId", N(), "from get_available_family_types"),
                ("hostWallId", N(), "host wall ElementId — shared partition of the two rooms being connected"),
                ("locationPoint", xyz, "on host wall centerline (±50–150 mm into the room the door should face)"),
                ("facingFlipped", B(), "optional; flip door facing if auto side from locationPoint is wrong"),
                ("baseLevel", N(), "mm"),
                ("baseOffset", N(), "mm"),
                ("height", N(), "mm"),
                ("width", N(), "mm"));
            var surfaceItem = P(R("typeId"),
                ("typeId", N(), "from get_available_family_types OST_Floors"),
                ("category", E("OST_Floors", "OST_Ceilings", "OST_Roofs"), "surface category"),
                ("boundary", O(), "outerLoop:[{p0,p1}...] in mm"),
                ("baseLevel", N(), "mm"),
                ("baseOffset", N(), "mm"));
            var roomItem = P(R("location"),
                ("name", S(), "room name"),
                ("number", S(), "room number label"),
                ("location", xyz, "point inside room cell, mm"));
            var operateData = P(R("action"),
                ("action", E(
                        "Select", "SelectionBox", "SetColor", "SetTransparency", "Delete", "Hide",
                        "TempHide", "Isolate", "Unhide", "ResetIsolate", "ResetOverrides", "Highlight"),
                    "operation"),
                ("elementIds", A("number"), "target element ids; may be empty with categoryNames"),
                ("categoryNames", A("string"), "e.g. Doors, Windows for ResetOverrides"),
                ("transparencyValue", N(), "0-100 for SetTransparency"),
                ("colorValue", A("number"), "RGB for SetColor, default [255,0,0]"));
            var reportMode = E("report", "highlight");
            var bubbleEnd = E("bottomLeft", "topRight", "both");

            var planStep = P(R("what"),
                ("n", N(), "step number 1..N"),
                ("what", S(), "short human description"),
                ("tool", S(), "intended tool name, e.g. create_line_based_element"));

            return new List<ToolDef>
            {
                T("declare_plan",
                    "Announce a checklist BEFORE changing the model. Call once at the start of multi-step " +
                    "create/layout/annotation work. Does NOT change Revit — only records intent. " +
                    "Skip for simple one-shot reads («сколько комнат», «статистика»). " +
                    "Then execute steps in order; do not wander on read tools.",
                    P(R("goal", "steps"),
                      ("goal", S(), "short goal, e.g. Планировка кафе 60 м²"),
                      ("steps", AItems(planStep), "ordered steps with what + tool"))),
                T("ask_user",
                    "Ask the architect ONE clarifying question with clickable options. " +
                    "Local meta-tool — pauses the agent until the user answers. " +
                    "Use ONLY for the 5 clarification cases (no layout context, ambiguous types, " +
                    "multi-floor scope, large/foreign delete, applyToAllFloorPlans). " +
                    "At most ONE ask_user per user request. Prefer 2–6 short options; allowFreeText for «Другое».",
                    P(R("question", "options"),
                      ("question", S(), "short question, e.g. Что проектируем?"),
                      ("options", A("string"), "2–6 button labels"),
                      ("allowFreeText", B(), "show a free-text field; default true"))),
                T("get_current_view_info",
                    "Active view: type, name, scale, level elevation (mm). Call first before create_* or dimensions.",
                    Empty()),
                T("get_current_view_elements", "Elements on the active view. Coordinates in mm.", P(
                    ("modelCategoryList", A("string"), "e.g. OST_Walls"),
                    ("annotationCategoryList", A("string"), "e.g. OST_Dimensions"),
                    ("limit", N(), "Max elements"))),
                T("say_hello",
                    "Test MCP/plugin connection. Returns a greeting — use when checking the link is alive.",
                    Empty()),
                T("query_norm_rules",
                    "Search the offline GOST/СП/СН РК norm catalog by topic (e.g. «ширина коридора», «глубина лоджии»). " +
                    "Returns document/clause/quote — cite only these, never invent norms. " +
                    "If catalogMissing=true, tell the architect to run seed + export:norm-catalog.",
                    P(R("topic"),
                      ("topic", S(), "natural language topic"),
                      ("limit", N(), "max rules, default 5"))),
                T("get_available_family_types",
                    "List family types. For walls MUST pass categoryName=\"OST_Walls\" (or categoryList:[\"OST_Walls\"]). " +
                    "Returns typeId numbers — copy typeId into create_line_based_element. Call once per category.",
                    P(("categoryName", S(), "OST_Walls | OST_Doors | OST_Windows | OST_Floors"),
                      ("categoryList", A("string"), "[\"OST_Walls\"]"),
                      ("limit", N(), "40"))),
                T("create_line_based_element",
                    "Create walls along lines (mm). Each item needs category, typeId, locationLine. " +
                    "Batch all wall segments in one call. If this fails — STOP, do not create rooms. NOT create_grid.",
                    P(R("data"),
                      ("data", AItems(lineItem), "wall segments"))),
                T("create_room",
                    "Place rooms ONLY after walls exist and enclose cells. Point must be inside its wall cell. " +
                    "1–2 rooms per call. Save response Id as ElementId for dimension_room_walls (NOT room number 1,2,3). " +
                    "If walls failed — do NOT call this.",
                    P(R("data"),
                      ("data", AItems(roomItem), "rooms to place"))),
                T("dimension_room_walls",
                    "Interior width×depth (default placement=interior; exterior only if user asks). " +
                    "roomId = Revit ElementId from create_room response (e.g. 1820053), NEVER room number 1/2/3. " +
                    "Dense plans: selective rooms or higher offsetMm. After ≥3 rooms verify via get_current_view_elements OST_Dimensions.",
                    P(R("roomId"),
                    ("roomId", N(), "ElementId from create_room"),
                    ("placement", E("interior", "exterior"), "interior = inside room (default)"),
                    ("offsetMm", N(), "mm offset from room boundary"),
                    ("dimensionType", S(), "dimension type name"))),
                T("set_element_parameter",
                    "Set parameter. «Room Bounding»/«Граница помещения»=true ONLY on Wall element ids, never on Room ids.",
                    P(R("elementId", "parameterName", "value"),
                      ("elementId", N(), "wall id"),
                      ("parameterName", S(), "Room Bounding"),
                      ("value", O(), "true"))),
                T("create_point_based_element",
                    "Create doors, windows, furniture at a point (mm). REQUIRED: typeId + hostWallId + locationPoint. " +
                    "hostWallId = partition BETWEEN the two rooms linked by adjacency (guest WC → hall wall, not kitchen). " +
                    "locationPoint on that wall centerline, ≥600 mm from corners; offset 50–150 mm into the room " +
                    "the leaf should face (kitchen service → into kitchen; WC → into cabin). " +
                    "Optional facingFlipped if auto orientation is wrong. Do not pass rotation for doors. " +
                    "Wrong hostWallId → door sits perpendicular / outside.",
                    P(R("data"),
                      ("data", AItems(pointItem), "doors/windows/furniture"))),
                T("create_surface_based_element",
                    "Create floors, ceilings, roofs from boundary loops (mm). REQUIRED: typeId from get_available_family_types.",
                    P(R("data"),
                      ("data", AItems(surfaceItem), "floors/ceilings/roofs"))),
                T("create_grid",
                    "Create coordination GRIDS (оси) — NOT walls. Only when walls already exist; autoFromWalls=true.",
                    P(
                    ("autoFromWalls", B(), "structural wall centerlines"),
                    ("bubbleEnd", bubbleEnd, "bubble side"),
                    ("gridTypeName", S(), "Марка оси 5 мм"),
                    ("wallFilter", S(), "structural"),
                    ("minWallThicknessMm", N(), "mm min wall thickness"))),
                T("configure_grid_display", "Adjust existing grid extents/bubbles. Extents in mm.", P(
                    ("gridTypeName", S(), null), ("bubbleEnd", bubbleEnd, null),
                    ("xExtentMin", N(), "mm"), ("xExtentMax", N(), "mm"),
                    ("yExtentMin", N(), "mm"), ("yExtentMax", N(), "mm"))),
                T("dimension_grids", "Exterior dimensions from building envelope: openings/piers + inter-axis + overall.", P(
                    ("firstOffsetMm", N(), "mm inter-axis from envelope"),
                    ("tierGapMm", N(), "mm between dimension tiers"),
                    ("includeOpeningTier", B(), "innermost openings/piers tier, default true"),
                    ("numericSide", S(), null), ("letterSide", S(), null), ("dimensionType", S(), null))),
                T("tag_rooms",
                    "Place room tags (марки помещений) on the active view; prefer type with area. " +
                    "Alias tag_all_rooms is accepted by the host.",
                    P(("tagTypeId", S(), null), ("roomIds", A("string"), null))),
                T("export_room_data",
                    "Export room ElementIds, names, numbers, areas (м²). Counts and floor areas only — " +
                    "NOT width/depth; for «глубина помещения» / «сколько глубина» use get_room_geometry_metrics. " +
                    "«На этаже» → filterByActiveView=true or levelName from get_current_view_info. " +
                    "Whole building → omit filter. Response may include totalInProject when filtered.",
                    P(("includeUnplacedRooms", B(), null),
                      ("includeNotEnclosedRooms", B(), null),
                      ("filterByActiveView", B(), "scope to active floor plan level"),
                      ("levelName", S(), "filter by level name"),
                      ("levelId", N(), "filter by level ElementId"))),
                T("create_door_schedule",
                    "Create door schedule (спецификация дверей / ведомость заполнения проёмов по ГОСТ 21.501). " +
                    "Use for «ведомость по ГОСТ 21.501» on the active view. " +
                    "Returns schedule ElementId for place_view_on_sheet / auto_layout_sheet. " +
                    "NOT TEP — for ТЭП use render_tep_table. No args; uses project template.",
                    Empty()),
                T("create_window_schedule",
                    "Create window schedule (спецификация окон). Returns schedule ElementId for sheet placement. " +
                    "NOT TEP. No args; uses project template.",
                    Empty()),
                T("create_floor_schedule",
                    "Create floor finish schedule / экспликация полов for (полы)* finishes only (м²). " +
                    "Returns schedule ElementId. No args; uses project template. Not structural slabs.",
                    Empty()),
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
                      ("positionX", N(), "mm from sheet lower-left"),
                      ("positionY", N(), "mm from sheet lower-left"),
                      ("includeLevels", B(), "true"), ("includeRoomsByPurpose", B(), "true"))),
                T("create_sheet", "Create sheet with project title block.", P(
                    ("sheetNumber", S(), null), ("sheetName", S(), null), ("titleBlockName", S(), null))),
                T("place_view_on_sheet",
                    "Place an existing floor plan or schedule on a sheet. " +
                    "Do not call without viewId. Not for TEP — use render_tep_table.",
                    P(R("sheetId", "viewId"),
                      ("sheetId", N(), "sheet element id"),
                      ("viewId", N(), "view or schedule element id"),
                      ("positionX", N(), "mm from sheet lower-left"),
                      ("positionY", N(), "mm from sheet lower-left"),
                      ("placement", O(), "optional nested {sheetId,viewId,positionX,positionY}"))),
                T("auto_layout_sheet",
                    "Auto-pack existing views/schedules on a sheet. Pass real view ids in items. Not for TEP table.",
                    P(R("items"),
                      ("items", AItems(P(null,
                          ("viewId", N(), "view or schedule ElementId"),
                          ("viewUniqueId", S(), "view UniqueId"),
                          ("viewName", S(), "view or schedule name"))),
                        "views/schedules to place"),
                      ("sheetId", N(), null), ("sheetNumber", S(), null),
                      ("createSheetIfMissing", B(), null), ("avoidExisting", B(), null),
                      ("order", E("input", "heightDesc", "areaDesc"), "packing order"))),
                T("check_evacuation_width",
                    "Norm check: evacuation corridor width vs minWidthMm (auto 1200 mm / СП РК if omitted). " +
                    "Returns violators with ids + source citation. Part of floor norm-audit.",
                    P(("levelName", S(), null),
                      ("levelId", N(), "level ElementId"),
                      ("viewId", N(), "floor plan view ElementId"),
                      ("filterByActiveView", B(), "scope to active view, default true"),
                      ("minWidthMm", N(), "mm, default 1200"),
                      ("mode", reportMode, "report = data only"))),
                T("check_room_depth",
                    "Norm check: living room depth vs maxDepthMm (auto 6000 mm / СП РК п.4.4.10.22 if omitted). " +
                    "Returns violators with ids + source. Part of floor norm-audit.",
                    P(("levelName", S(), null),
                      ("levelId", N(), "level ElementId"),
                      ("viewId", N(), "floor plan view ElementId"),
                      ("filterByActiveView", B(), "scope to active view, default true"),
                      ("maxDepthMm", N(), "mm, default 6000"),
                      ("roomScope", S(), "living"), ("mode", reportMode, "report = data only"))),
                T("check_min_dimensions",
                    "Norm check: balcony/loggia/pier min size. " +
                    "For ordinary housing do NOT pass minBalconyWidthMm/minLoggiaWidthMm=1400 " +
                    "(1.4 m is МГН / п.4.6.5 only). Default catalog uses fire-path/pier limits (~1200). " +
                    "Pass housingType=mgn only when user asks МГН. Returns violators + source.",
                    P(("levelName", S(), null),
                      ("levelId", N(), "level ElementId"),
                      ("viewId", N(), "floor plan view ElementId"),
                      ("filterByActiveView", B(), "scope to active view, default true"),
                      ("housingType", E("ordinary", "mgn"), "mgn = п.4.6.5 limits (1.4 m)"),
                      ("minFirePathOutdoorWidthMm", N(), "mm, 1200 for Н1 path only"),
                      ("minFirePierToOpeningMm", N(), "mm, default 1200"),
                      ("minFirePierBetweenOpeningsMm", N(), "mm"),
                      ("minBalconyWidthMm", N(), "mm — only if МГН"),
                      ("minLoggiaWidthMm", N(), "mm — only if МГН"),
                      ("minLoggiaDepthMm", N(), "mm"),
                      ("mode", reportMode, "report = data only"))),
                T("check_fire_doors",
                    "Norm check: fire doors. Returns doors with requiresFireDoor/compliant/reason/source " +
                    "and annotationHints[{elementId,text,leader}] for create_text_notes. " +
                    "Paint non-compliant doors red AND place leaders — never color without callouts.",
                    P(("levelName", S(), null),
                      ("levelId", N(), "level ElementId"),
                      ("viewId", N(), "floor plan view ElementId"),
                      ("filterByActiveView", B(), "scope to active view, default true"))),
                T("get_room_geometry_metrics",
                    "REQUIRED for «глубина помещения», «сколько глубина», width/depth questions. " +
                    "Returns per-room widthMm/depthMm/area for the active floor. " +
                    "Do NOT use export_room_data or run_norm_audit for depth-only questions. No args.",
                    Empty()),
                T("create_filled_regions",
                    "Paint room areas as Filled Region (цветовая область). " +
                    "After successful norm checks ONLY: pass roomIds of violators, colorPreset=red, clearPrevious=true. " +
                    "Do not call with empty roomIds (that paints all rooms). " +
                    "To remove prior MCP markup without painting: clearOnly=true.",
                    P(("roomIds", A("string"), "violating room element ids"),
                      ("colorPreset", E("red", "green", "blue", "grey", "gray"), "fill color"),
                      ("clearPrevious", B(), "true"),
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
                    P(R("data"),
                      ("data", operateData, "action + elementIds"))),
                T("delete_element", "Delete elements by id.", P(R("elementIds"),
                    ("elementIds", A("string"), "element id strings"))),
                T("ai_element_filter", "Filter elements by category.", P(
                    ("categoryName", S(), null), ("filter", O(), null))),
                T("get_selected_elements",
                    "Returns currently selected elements (ids, category, name). Use when user says «выделенное» / selection.",
                    Empty()),
                T("color_splash",
                    "View color scheme by parameter (NOT filled regions for violations — use create_filled_regions). " +
                    "Alias color_elements is accepted by the host.",
                    P(("categoryName", S(), "Помещения"), ("parameterName", S(), "Имя"),
                      ("useGradient", B(), null))),
                T("get_document_styles",
                    "Returns annotation styles available in the project: dimension types, grid types, text types, title blocks. " +
                    "Call before picking dimensionType / gridTypeName / textTypeName.",
                    Empty()),
                T("analyze_model_statistics",
                    "Project-wide element counts by category (walls, doors, rooms, etc.) for the active document. " +
                    "Use for «статистика модели» / «сколько стен, дверей, помещений» — room count from categories[], " +
                    "not export_room_data. Do not pair with export_room_data for counts. Floor room areas → export_room_data separately.",
                    Empty()),

                // --- Full MCP parity (Revit commands from command.json) ---
                T("run_norm_audit",
                    "Unified norm audit for active floor: evacuation width, room depth, min dimensions, fire doors. " +
                    "Returns findings[] + skippedRules. mode=highlight paints violations; annotate=true adds leader callouts (default true). " +
                    "Prefer over calling many check_* separately when user says «проверь этаж по нормам».",
                    P(("levelName", S(), null),
                      ("levelId", N(), "level ElementId"),
                      ("viewId", N(), "floor plan view ElementId"),
                      ("filterByActiveView", B(), "scope to active view, default true"),
                      ("mode", reportMode, "report = data; highlight = report+paint"),
                      ("annotate", B(), "leader callouts when mode=highlight, default true"),
                      ("includeCompliant", B(), null), ("topics", A("string"), null))),
                T("annotate_norm_findings",
                    "Place leader callouts for run_norm_audit findings. Template: name: actual < required · document clause.",
                    P(("findings", A("object"), "from run_norm_audit violation+nearLimit"),
                      ("style", S(), "leader"), ("textTypeName", S(), "ADSK_Замечания"),
                      ("clearPrevious", B(), "true"))),
                T("tag_walls",
                    "Place wall tags on all walls of the active floor plan. Returns created tag ids. No args. " +
                    "Alias tag_all_walls is accepted by the host.",
                    Empty()),
                T("create_stair",
                    "Create stair: layout straight/L/U, typeId (StairsType), widthMm, shaftRect or path. Units mm.",
                    P(R("typeId"),
                      ("typeId", N(), "StairsType ElementId"),
                      ("layout", E("straight", "L", "U"), "stair plan layout"),
                      ("widthMm", N(), "mm run width"),
                      ("baseLevelId", N(), null), ("topLevelId", N(), null),
                      ("shaftRect", O(), "shaft bounds in mm"))),
                T("create_railing",
                    "Create railing by path or host stair. typeId required (RailingType).",
                    P(R("typeId"),
                      ("typeId", N(), "RailingType ElementId"),
                      ("hostElementId", N(), "host stair id"),
                      ("pathPoints", A("object"), "path points mm"),
                      ("levelId", N(), null))),
                T("create_floor_opening",
                    "Floor cut or vertical shaft. rect or boundaryPoints in mm.",
                    P(R("mode"),
                      ("mode", E("floor", "shaft"), "floor cut vs vertical shaft"),
                      ("hostFloorId", N(), null), ("levelId", N(), null),
                      ("baseLevelId", N(), null), ("topLevelId", N(), null),
                      ("rect", O(), "bounds in mm"))),
                T("create_level", "Create level at elevation mm with optional floor plan view.", P(
                    R("name", "elevationMm"),
                    ("name", S(), "level name"),
                    ("elevationMm", N(), "mm elevation"))),
                T("create_dimensions", "Create dimension chains between points or elements.", P(
                    ("data", A("object"), null))),
                T("create_structural_framing_system", "Beam system with spacing and direction.", P(
                    ("data", O(), null))),
                T("get_element_parameters", "Read all parameters of one element by id.", P(
                    R("elementId"),
                    ("elementId", N(), "element id"))),
                T("get_elements_parameters", "Batch read parameters (max 100 ids).", P(R("elementIds"),
                    ("elementIds", A("number"), "up to 100 element ids"))),
                T("get_material_quantities",
                    "Returns material takeoffs: areas (м²) and volumes (м³) by material. " +
                    "Use for quantity surveys / ведомости материалов. No args.",
                    Empty()),
                T("export_apartment_data",
                    "Returns apartment grouping with СП РК area coefficients (м²). " +
                    "Use for квартирография / ТЭП living area breakdown. No args.",
                    Empty()),
                T("export_room_finish_data", "Room finish parameters (walls/floors/ceilings).", P(
                    ("levelName", S(), null), ("limit", N(), null), ("offset", N(), null))),
                T("validate_schedule", "Compare schedule counts vs model.", P(
                    R("category"),
                    ("category", E("Doors", "Windows", "Floors", "CurtainWalls"), "schedule category"))),
                T("create_schedule", "Create ViewSchedule from template. " +
                    "NOT for «ведомость по ГОСТ 21.501» — use create_door_schedule (no categoryName needed).",
                    P(
                    ("scheduleName", S(), null), ("templateName", S(), null))),
                T("configure_schedule", "Edit existing schedule columns/filters.", P(
                    ("scheduleId", N(), null), ("changes", O(), null))),
                T("fit_schedule_to_sheet", "Fit schedule to sheet width.", P(
                    ("scheduleId", N(), null), ("sheetId", N(), null))),
                T("create_finish_schedule", "Room finish schedule chain (ADSK).", P(
                    ("templateName", S(), null))),
                T("create_curtain_wall_schedule",
                    "Create curtain-wall schedule data / ViewSchedule. Returns schedule id for sheet placement. No args.",
                    Empty()),
                T("get_schedule_definition", "Read schedule fields/filters.", P(
                    ("scheduleId", N(), null), ("scheduleName", S(), null))),
                T("get_door_egress_info", "Door widths, egress paths, ramp slopes.", P(
                    ("levelName", S(), null))),
                T("get_opening_geometry_info", "Window sill height, opening height (mm).", P(
                    ("levelName", S(), null))),
                T("get_cad_link_geometry",
                    "Read DWG/CAD ImportInstance lines in mm (startMm/endMm/layer). Before tracing walls from CAD.",
                    P(
                        ("cadLinkName", S(), null),
                        ("layerFilter", S(), "layer name or substring"),
                        ("viewId", N(), null),
                        ("minLengthMm", N(), null),
                        ("limit", N(), null))),
                T("get_vertical_circulation_info", "Stair/ramp/railing geometry for norms (mm).", P(
                    ("levelName", S(), null))),
                T("create_detail_lines", "Detail polylines on plan/detail view (mm).", P(
                    ("points", A("object"), "points in mm"), ("viewId", N(), null))),
                T("create_detail_view", "Detail callout or drafting view.", P(
                    ("name", S(), null), ("parentViewId", N(), null), ("scale", N(), null))),
                T("place_detail_component", "Place 2D detail component on detail view.", P(
                    ("data", A("object"), null))),
                T("create_text_note", "Single text note with optional leader.", P(
                    R("text"),
                    ("text", S(), "note text"),
                    ("location", O(), "point mm"),
                    ("leader", B(), null))),
                T("batch_execute",
                    "Run up to 20 Revit commands in one request. commands=[{method, params}].",
                    P(R("commands"),
                      ("commands", A("object"), "[{method, params}]"))),
                T("send_code_to_revit",
                    "Execute C# in Revit. ONLY if user explicitly allowed. Prefer create_* tools.",
                    P(R("code"),
                      ("code", S(), "C# source"),
                      ("description", S(), null))),
            };
        }

        private static ToolDef T(string name, string description, JObject parameters) =>
            new ToolDef { Name = name, Description = description, Parameters = parameters };

        private static string[] R(params string[] names) => names;

        private static JObject Empty() =>
            new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["additionalProperties"] = false
            };

        private static JObject P(params (string name, JObject schema, string desc)[] items) =>
            P(null, items);

        private static JObject P(string[] required, params (string name, JObject schema, string desc)[] items)
        {
            var props = new JObject();
            foreach (var item in items)
            {
                var copy = (JObject)item.schema.DeepClone();
                if (!string.IsNullOrEmpty(item.desc))
                    copy["description"] = item.desc;
                props[item.name] = copy;
            }

            var obj = new JObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["additionalProperties"] = false
            };
            if (required != null && required.Length > 0)
                obj["required"] = new JArray(required);
            return obj;
        }

        private static JObject E(params string[] values) =>
            new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values)
            };

        private static JObject XyzMm() =>
            P(R("x", "y", "z"),
                ("x", N(), "mm"),
                ("y", N(), "mm"),
                ("z", N(), "mm"));

        private static JObject S() => new JObject { ["type"] = "string" };
        private static JObject N() => new JObject { ["type"] = "number" };
        private static JObject B() => new JObject { ["type"] = "boolean" };
        private static JObject O() =>
            new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = true
            };

        private static JObject A(string itemType) =>
            new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = itemType } };

        private static JObject AItems(JObject itemSchema) =>
            new JObject { ["type"] = "array", ["items"] = itemSchema };
    }
}
