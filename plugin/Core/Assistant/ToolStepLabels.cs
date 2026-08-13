using System;
using System.Collections.Generic;
using System.Globalization;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Russian captions for the tool journal. Cursor reports raw MCP tool ids,
    /// which read badly in the architect-facing panel (REV-155).
    /// </summary>
    public static class ToolStepLabels
    {
        private static readonly Dictionary<string, string> Names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Чтение модели
                { "get_current_view_info", "Читаю активный вид" },
                { "get_current_view_elements", "Читаю элементы вида" },
                { "get_selected_elements", "Читаю выделение" },
                { "get_element_parameters", "Читаю параметры элемента" },
                { "get_elements_parameters", "Читаю параметры элементов" },
                { "get_available_family_types", "Ищу типы семейств" },
                { "get_document_styles", "Читаю стили документа" },
                { "get_room_geometry_metrics", "Считаю геометрию помещений" },
                { "get_material_quantities", "Считаю объёмы материалов" },
                { "analyze_model_statistics", "Анализирую модель" },
                { "ai_element_filter", "Отбираю элементы" },
                { "get_schedule_definition", "Читаю состав спецификации" },
                { "get_opening_geometry_info", "Читаю геометрию проёмов" },
                { "get_door_egress_info", "Читаю данные по эвакуации через двери" },
                { "get_vertical_circulation_info", "Читаю лестницы и лифты" },

                // Построение
                { "create_grid", "Строю оси" },
                { "create_level", "Создаю уровень" },
                { "create_room", "Создаю помещение" },
                { "create_stair", "Строю лестницу" },
                { "create_railing", "Строю ограждение" },
                { "create_floor_opening", "Делаю проём в перекрытии" },
                { "create_point_based_element", "Размещаю элемент" },
                { "create_line_based_element", "Черчу элемент по линии" },
                { "create_surface_based_element", "Строю поверхностный элемент" },
                { "create_structural_framing_system", "Строю несущий каркас" },
                { "operate_element", "Изменяю элемент" },
                { "set_element_parameter", "Меняю параметр" },
                { "delete_element", "Удаляю элемент" },
                { "load_family", "Загружаю семейство" },
                { "ensure_wall_type", "Готовлю тип стены" },
                { "ensure_opening_type", "Готовлю тип проёма" },

                // Оформление
                { "create_dimensions", "Ставлю размеры" },
                { "dimension_grids", "Образмериваю оси" },
                { "dimension_room_walls", "Образмериваю помещение" },
                { "create_text_note", "Добавляю текст" },
                { "create_text_notes", "Добавляю тексты" },
                { "tag_all_rooms", "Маркирую помещения" },
                { "tag_all_walls", "Маркирую стены" },
                { "number_rooms", "Нумерую помещения" },
                { "color_elements", "Крашу элементы" },
                { "create_detail_lines", "Черчу линии детали" },
                { "create_detail_regions", "Строю области детали" },
                { "create_filled_regions", "Строю заливки" },
                { "create_detail_view", "Создаю вид детали" },
                { "create_node_detail", "Собираю узел" },
                { "place_detail_component", "Размещаю компонент детали" },
                { "configure_grid_display", "Настраиваю отображение осей" },

                // Листы и спецификации
                { "create_sheet", "Создаю лист" },
                { "place_view_on_sheet", "Размещаю вид на листе" },
                { "auto_layout_sheet", "Компоную лист" },
                { "fill_title_block", "Заполняю штамп" },
                { "create_schedule", "Создаю спецификацию" },
                { "configure_schedule", "Настраиваю спецификацию" },
                { "validate_schedule", "Проверяю спецификацию" },
                { "fit_schedule_to_sheet", "Вписываю спецификацию в лист" },
                { "create_door_schedule", "Спецификация дверей" },
                { "create_window_schedule", "Спецификация окон" },
                { "create_floor_schedule", "Спецификация перекрытий" },
                { "create_finish_schedule", "Спецификация отделки" },
                { "create_curtain_wall_schedule", "Спецификация витражей" },
                { "create_floor_explication", "Экспликация помещений" },
                { "render_tep_table", "Таблица ТЭП" },

                // Нормы
                { "run_norm_audit", "Проверяю нормы" },
                { "check_room_norms", "Проверяю нормы помещений" },
                { "check_door_width", "Проверяю ширину дверей" },
                { "check_evacuation_distance", "Проверяю пути эвакуации" },
                { "check_evacuation_width", "Проверяю ширину эвакуации" },
                { "check_fire_doors", "Проверяю противопожарные двери" },
                { "check_accessibility", "Проверяю доступность (МГН)" },
                { "check_min_dimensions", "Проверяю габариты" },
                { "check_room_depth", "Проверяю глубину помещений" },
                { "check_tambour_size", "Проверяю тамбуры" },
                { "check_window_openings", "Проверяю окна" },
                { "check_vertical_circulation", "Проверяю вертикальные связи" },
                { "annotate_norm_findings", "Подписываю нарушения" },
                { "apply_norm_result", "Применяю результат проверки" },
                { "query_norm_rules", "Ищу правила в базе норм" },
                { "save_norm_rule", "Сохраняю правило" },
                { "extract_norm_rules_from_pdf", "Читаю нормы из PDF" },

                // DWG
                { "get_cad_link_geometry", "Читаю геометрию DWG" },
                { "trace_walls_from_cad", "Обвожу стены по DWG" },
                { "trace_openings_from_cad", "Обвожу проёмы по DWG" },
                { "trace_columns_from_cad", "Обвожу колонны по DWG" },

                // Экспорт и прочее
                { "export_room_data", "Выгружаю данные помещений" },
                { "export_apartment_data", "Выгружаю данные квартир" },
                { "export_room_finish_data", "Выгружаю отделку" },
                { "export_tep_data", "Выгружаю ТЭП" },
                { "batch_execute", "Выполняю пакет операций" },
                { "send_code_to_revit", "Выполняю код в Revit" },
                { "say_hello", "Проверяю связь" },

                // Служебное
                { "revit_confirm", "Жду подтверждения" },
                { "declare_plan", "Планирую шаги" },
                { "ask_user", "Уточняю у вас" },
                { "export_egress_graph", "Выгружаю схему эвакуации" },
                { "color_splash", "Крашу элементы" },
                { "tag_rooms", "Маркирую помещения" },
                { "tag_walls", "Маркирую стены" },
            };

        /// <summary>Short note appended after the step caption; null when it ran fine.</summary>
        public static string StatusNote(string status)
        {
            if (status == ToolStepEvent.StatusError)
                return "ошибка";
            if (status == ToolStepEvent.StatusCancelled)
                return "остановлено";
            return null;
        }

        /// <summary>
        /// Caption for a row that folded several calls of one tool. "×5" reads as a code
        /// artefact to an architect, so spell the repeat out: «Удаляю элемент · 5 раз».
        /// </summary>
        public static string WithRepeat(string label, int count)
        {
            if (count <= 1)
                return label;

            return label + " · " + count + " " + TimesWord(count);
        }

        private static string TimesWord(int count)
        {
            var tail = count % 100;
            if (tail >= 11 && tail <= 14)
                return "раз";

            switch (count % 10)
            {
                case 1: return "раз";
                case 2:
                case 3:
                case 4: return "раза";
                default: return "раз";
            }
        }

        /// <summary>Human caption for a tool id, e.g. "Строю оси".</summary>
        public static string Humanize(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return "Инструмент";

            var name = toolName.Trim();

            // Cursor may prefix MCP calls with the server id.
            var lastDot = name.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < name.Length - 1)
                name = name.Substring(lastDot + 1);

            string label;
            if (Names.TryGetValue(name, out label))
                return label;

            // Unknown tool: "create_wall_thing" -> "Create wall thing".
            var words = name.Replace('_', ' ').Trim();
            if (words.Length == 0)
                return "Инструмент";

            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(words.ToLowerInvariant());
        }
    }
}
