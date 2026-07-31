using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Intent-specific instruction blocks appended to <see cref="AssistantSystemPrompt.Core"/> (REV-118).
    /// </summary>
    public static class AssistantPlaybooks
    {
        /// <summary>All playbook bodies for schema-alignment tests.</summary>
        public static IReadOnlyList<string> AllBodies { get; } = new[]
        {
            Modeling,
            Annotation,
            Schedules,
            Sheets,
            Norms,
            Typology,
            Data,
        };

        public const string Data =
            "=== ЧТЕНИЕ ДАННЫХ ===\n" +
            "«Статистика модели» → только analyze_model_statistics (categories[]); не export_room_data.\n" +
            "«Глубина помещения» / «сколько глубина» → get_room_geometry_metrics (depthMm по комнатам); " +
            "не export_room_data, не run_norm_audit.\n" +
            "«На этаже» / площади на плане → export_room_data filterByActiveView=true.\n" +
            "«В проекте» / «всего» → export_room_data без filterByActiveView; count/totalRooms, не totalInProject.\n" +
            "Пример: «Сколько помещений на этаже?» → export_room_data filterByActiveView=true.\n" +
            "Пример: «Сколько глубина помещения на этаже?» → get_room_geometry_metrics.";

        public const string Modeling =
            "=== ПЛАНИРОВКА (только при создании геометрии) ===\n" +
            "A) get_current_view_info → elevation уровня (мм) = baseLevel.\n" +
            "B) get_available_family_types categoryName=OST_Walls → typeId из types[] или suggestedWallTypeId.\n" +
            "C) create_line_based_element: все сегменты стен одним вызовом; typeId обязателен; height≈3000; baseLevel из вида.\n" +
            "D) Если стены не создались — остановись, сообщи ошибку; не вызывай create_room и размеры.\n" +
            "E) После успеха стен: create_point_based_element (двери/окна: typeId + hostWallId).\n" +
            "F) create_room по 1–2 за вызов; точка внутри ячейки; Id из ответа — ElementId, не номер.\n" +
            "G) tag_rooms; dimension_room_walls roomId=ElementId, placement=interior.\n" +
            "H) run_norm_audit — только после готовой планировки.\n" +
            "Помещения только внутри замкнутых стен. Room Bounding=true — на стенах, не на комнатах.\n" +
            "Пример: «Построй 2-комнатную» → get_current_view_info → get_available_family_types → " +
            "create_line_based_element → create_point_based_element → create_room → tag_rooms.\n" +
            "Пример: «Сколько комнат на этаже?» → export_room_data filterByActiveView=true (не весь проект).";

        public const string Annotation =
            "=== ОСИ, РАЗМЕРЫ, МАРКИ ===\n" +
            "create_grid autoFromWalls — только если стены уже есть; марка оси 5 мм; bubbleEnd=bottomLeft.\n" +
            "dimension_grids — от габарита здания, не от линии оси. dimension_room_walls placement=interior по умолчанию.\n" +
            "tag_rooms / tag_walls — тип марки из проекта (с площадью для комнат). color_splash — цветовая схема вида.\n" +
            "Пример: «Размеры внутри комнат» → export_room_data или get_current_view_info → dimension_room_walls placement=interior.\n" +
            "Пример: «Марки с площадью» → tag_rooms (не color_splash).";

        public const string Schedules =
            "=== ТЭП И ВЕДОМОСТИ ===\n" +
            "ТЭП — render_tep_table. Спеки: create_door_schedule, create_window_schedule, " +
            "create_floor_schedule, create_floor_explication. Шаблоны и рамка — только из проекта.\n" +
            "«Ведомость по ГОСТ 21.501» → create_door_schedule (спецификация заполнения проёмов); " +
            "не create_schedule/configure_schedule и не query_norm_rules.\n" +
            "Пример: «Сделай ТЭП» → render_tep_table.\n" +
            "Пример: «Спецификация дверей» → create_door_schedule (не render_tep_table).\n" +
            "Пример: «Ведомость по ГОСТ 21.501» → create_door_schedule.";

        public const string Sheets =
            "=== ЛИСТЫ ===\n" +
            "Размещение: auto_layout_sheet или place_view_on_sheet только с реальным viewId из ответа create_* / render_*.\n" +
            "Пример: «Разложи спеки на лист» → create_*_schedule → auto_layout_sheet items с viewId из ответа.";

        /// <summary>
        /// Norm violation display matches .cursor/rules/revit-mcp.mdc (plugin assistant path).
        /// mode=highlight is optional shortcut in MCP Cursor agent only — here use report + regions.
        /// </summary>
        public const string Norms =
            "=== НОРМОКОНТРОЛЬ ===\n" +
            "Нормы только из ответов инструментов — не выдумывай пункты и числа.\n" +
            "«Проверь нормы» / «покажи нарушения»: run_norm_audit mode=report → " +
            "create_filled_regions roomIds из findings (violation + nearLimit), colorPreset=red, clearPrevious=true. " +
            "Двери и не-Room — operate_element SetColor красным. " +
            "Подписи с выноской: annotate_norm_findings style=leader (после заливки).\n" +
            "Не заливай этаж без roomIds из аудита. Не используй color_splash вместо заливки нарушений.\n" +
            "Пример: «Покажи нарушения» → run_norm_audit mode=report → create_filled_regions roomIds=[…].\n" +
            "Пример: «Какая норма на коридор?» → query_norm_rules topic=ширина коридора (без run_norm_audit).\n" +
            "Пример: «Сколько глубина помещения?» → get_room_geometry_metrics (не check_room_depth без запроса проверки).";

        public const string Typology =
            "=== КОММЕРЧЕСКИЕ ТИПОЛОГИИ (кафе, офис, СТО, автомойка) ===\n" +
            "1) query_norm_rules по теме — мин. площади только из каталога.\n" +
            "2) Зонирование: вход→тамбур→зал; санузлы блоком; производство отдельно; МГН доступен из зала.\n" +
            "3) Стены одним вызовом; двери batch_execute до 4 (деревянные на базовых стенах, hostWallId обязателен).\n" +
            "4) Вход с улицы на наружной стене; окна на зал/фасад; не линейная «коробка в ряд».\n" +
            "5) tag_rooms своих помещений; color_splash по «Назначение»; dimension_room_walls на ключевые.\n" +
            "6) run_norm_audit mode=report в конце. Вложение — ориентир по зонам, не слепое копирование.\n" +
            "Работай в чистой зоне плана — не размечай весь жилой дом.\n" +
            "Пример: «Кафе на 40 мест» → query_norm_rules → create_line_based_element → … → run_norm_audit mode=report.";

        /// <summary>Rules referenced from <see cref="TypologyPrograms.BuildAgentInstruction"/>.</summary>
        public const string TypologyAgentRules =
            "Скорость: стены — один create_line_based_element. Двери — batch_execute до 4 (create_point_based_element). " +
            "Помещения — create_room по 2 за вызов. После комнат: tag_rooms, color_splash по «Назначение», dimension_room_walls. " +
            "Вход с улицы (hostWallId наружной стены). Окна на зал. run_norm_audit mode=report в конце.";

        public static bool ShouldIncludeTypology(string userText)
        {
            var text = (userText ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(text,
                "кафе", "общепит", "ресторан", "офис", "open space", "сто", "автосервис",
                "автомойк", "car wash", "типолог", "40 мест", "пищеблок");
        }

        public static bool ShouldIncludeReadHints(string userText)
        {
            var text = (userText ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(text,
                "сколько помещен", "сколько комнат", "площади помещен", "на этаже", "какие площади",
                "статистик модел", "статистика модел", "сколько стен", "сколько дверей",
                "сколько глубин", "глубина помещен", "глубин помещен", "гост 21.501", "ведомость по гост");
        }

        public static string Build(IReadOnlyList<string> profiles, string userText = null)
        {
            var normalized = ToolCatalog.NormalizeProfiles(profiles);
            var parts = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string body)
            {
                if (string.IsNullOrWhiteSpace(body) || !seen.Add(body))
                    return;
                parts.Add(body.Trim());
            }

            foreach (var profile in normalized)
            {
                if (profile.Equals(ToolCatalog.Profiles.Modeling, StringComparison.OrdinalIgnoreCase))
                    Add(Modeling);
                else if (profile.Equals(ToolCatalog.Profiles.Annotation, StringComparison.OrdinalIgnoreCase))
                    Add(Annotation);
                else if (profile.Equals(ToolCatalog.Profiles.Schedules, StringComparison.OrdinalIgnoreCase))
                    Add(Schedules);
                else if (profile.Equals(ToolCatalog.Profiles.Sheets, StringComparison.OrdinalIgnoreCase))
                    Add(Sheets);
                else if (profile.Equals(ToolCatalog.Profiles.Norms, StringComparison.OrdinalIgnoreCase))
                    Add(Norms);
                else if (profile.Equals(ToolCatalog.Profiles.Data, StringComparison.OrdinalIgnoreCase))
                    Add(Data);
            }

            if (normalized.Any(p => p.Equals(ToolCatalog.Profiles.Modeling, StringComparison.OrdinalIgnoreCase))
                && ShouldIncludeTypology(userText))
                Add(Typology);

            if (parts.Count == 0 && ShouldIncludeReadHints(userText))
                Add(Data);

            if (parts.Count == 0)
                return "";

            var sb = new StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                    sb.AppendLine().AppendLine();
                sb.Append(parts[i]);
            }

            return sb.ToString();
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (var n in needles)
            {
                if (!string.IsNullOrEmpty(n) && text.IndexOf(n, StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }
    }
}
