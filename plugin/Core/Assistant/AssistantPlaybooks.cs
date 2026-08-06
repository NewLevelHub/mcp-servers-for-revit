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
        /// <summary>All playbook bodies for schema-alignment tests (excluding always-on Clarification).</summary>
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

        /// <summary>
        /// Always appended (REV-125): when to call ask_user vs act on named defaults.
        /// </summary>
        public const string Clarification =
            "=== УТОЧНЕНИЯ (ask_user) ===\n" +
            "Действуй по разумным дефолтам и в итоге перечисли их " +
            "(«взял тип ADSK_Основной_2 мм — других нет»).\n" +
            "Спрашивай через ask_user только в этих случаях (один вызов за запрос, 2–6 options):\n" +
            "1) Нет контекста для создания — «сделай планировку» без типологии/площади/места.\n" +
            "2) Несколько равноправных типов в проекте, ни один не очевиден.\n" +
            "3) Запрос трогает больше одного этажа или всю модель, а активен один вид.\n" +
            "4) Удаление большого числа элементов или чужого (без тега MCP) — UI подтвердит сам.\n" +
            "5) Правка чужих оформленных планов / applyToAllFloorPlans=true без явного согласия.\n" +
            "Иначе не спрашивай. Не вызывай ask_user повторно в том же запросе.\n" +
            "После ответа ask_user (Школа/Офис/Жилой дом/Кафе…) — строй программу помещений этой типологии. " +
            "ЗАПРЕЩЕНО сводить любую типологию к двум комнатам с одной перегородкой.\n" +
            "Пример: «Сделай планировку» → ask_user question=Что проектируем? options=[Жилой дом,Офис,Школа,Кафе,Другое].\n" +
            "Пример: «Кафе 60 м² у оси А» → без ask_user, declare_plan и создание.\n" +
            "[КОНТЕКСТ]/[ЖУРНАЛ] в сообщении — вид/выделение и id сессии; «удали что создал» → id из журнала.";

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
            "0) Сначала declare_plan: goal + steps[{n,what,tool}] — чеклист до любых create_*. " +
            "Одно чтение («сколько комнат») — без плана.\n" +
            "A) get_current_view_info → elevation уровня (мм) = baseLevel.\n" +
            "A2) Перечерчивание по DWG/CAD: trace_walls_from_cad (wallTypeId обязателен) — " +
            "читает CAD + Model Lines (если DWG взорван), сливает двойные линии в центр, " +
            "меряет толщину по зазору граней и подбирает тип стены по мм в имени. " +
            "Сначала dryRun=true → thicknessClusters / recommendedWallTypes, потом создание. " +
            "Или вручную: get_cad_link_geometry → create_line_based_element по startMm/endMm " +
            "(не угадывай стены «на глаз»). layerFilter по слоям стен; bboxMm — отсечь лишние фрагменты листа. " +
            "Нет CAD на виде → скажи привязать DWG к уровню.\n" +
            "B) get_available_family_types categoryName=OST_Walls → typeId = suggestedWallTypeId " +
            "(Базовая стена / перегородка). Не Витраж/Curtain — они дают таймаут. " +
            "Без typeId create_*_element вернёт ошибку с candidates — автоподстановки нет.\n" +
            "C) create_line_based_element: все сегменты ОДНИМ вызовом; typeId обязателен; height≈3000; baseLevel из вида. " +
            "Состав и число помещений — из запроса/типологии (школа, офис, жилой, кафе…), не из демо-шаблона. " +
            "Шаблон «две комнаты» (прямоугольник + 1 перегородка, 5 сегментов) — ТОЛЬКО если пользователь явно сказал " +
            "«две комнаты» / «2 комнаты». Для школы/офиса/жилого — полный контур и несколько помещений. " +
            "Не строй поверх старых — сначала удали мусор или работай в чистой зоне.\n" +
            "D) Если стены не создались — остановись, сообщи ошибку; не вызывай create_room и размеры.\n" +
            "E) Двери: get_available_family_types OST_Doors → typeId. " +
            "Между комнатами: hostWallId = общая ПЕРЕГОРОДКА двух связанных по adjacency комнат " +
            "(не наружный контур и не «ближайшая стена вообще»), " +
            "locationPoint = середина Start/End этой перегородки (≥600 мм от углов/Т-стыка). " +
            "Вход с улицы — hostWallId наружной стены фасада, середина пролёта. " +
            "Ориентация: смести locationPoint на 50–150 мм В комнату, куда должна смотреть дверь " +
            "(авто flip по стороне точки), либо facingFlipped=true; кухня/раздача — внутрь кухни, не в зал; " +
            "WC — внутрь кабины/холла санузлов. " +
            "Не стык/угол — иначе дверь уезжает «поперёк» на примыкающую стену. " +
            "Никогда не подставляй typeId стены в create_point_based_element.\n" +
            "F) create_room по 1–2 за вызов; точка внутри ячейки; Id из ответа — ElementId, не номер.\n" +
            "G) tag_rooms; dimension_room_walls roomId=ElementId, placement=interior.\n" +
            "H) run_norm_audit — только после готовой планировки.\n" +
            "Помещения только внутри замкнутых стен. Room Bounding=true — на стенах, не на комнатах.\n" +
            "Пример: «Перечерти стены по DWG» → get_available_family_types → trace_walls_from_cad wallTypeId + layerFilter=wall.\n" +
            "Пример: «Построй две комнаты» → declare_plan → view → types → 5 сегментов → дверь в перегородке → create_room ×2.\n" +
            "Пример: «Школа» / ask_user=Школа → вестибюль, коридор, ≥3 класса, учительская, санузлы — не две комнаты.\n" +
            "Пример: «Сколько комнат на этаже?» → export_room_data filterByActiveView=true (без плана).";

        public const string Annotation =
            "=== ОСИ, РАЗМЕРЫ, МАРКИ ===\n" +
            "create_grid autoFromWalls — только если стены уже есть; марка оси 5 мм; bubbleEnd=bottomLeft. " +
            "Дефолт tool ищет несущие ≥400 мм — для перегородок 100–140 мм ставь wallFilter=all и minWallThicknessMm=100. " +
            "Пустой результат ≠ «стен нет»: снизь порог, не предлагай строить стены заново.\n" +
            "dimension_grids — от габарита здания, не от линии оси; 3 яруса (проёмы → межосевой → габарит).\n" +
            "«Размеры помещений/комнат» → dimension_room_walls placement=interior (дефолт). " +
            "exterior — только по явному запросу («снаружи», «по осям», «фасадные»). " +
            "Плотное ядро (лифт, коридоры, санузлы): выборочно крупные комнаты или offsetMm↑ — не пачкой на все.\n" +
            "Verify: после ≥3 dimension_room_walls → get_elements_parameters по Response ids " +
            "+ get_current_view_elements OST_Dimensions; при наложении — Delete лишних + переставить с большим offsetMm.\n" +
            "tag_rooms / tag_walls — тип марки из проекта (с площадью для комнат). color_splash — цветовая схема вида.\n" +
            "Пример: «Размеры внутри комнат» → export_room_data или get_current_view_info → dimension_room_walls placement=interior.\n" +
            "Пример: «Размеры без налезания» → interior выборочно → get_current_view_elements OST_Dimensions → offsetMm↑ при overlap.\n" +
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
            "=== ТИПОЛОГИИ (кафе, офис, СТО, автомойка, школа, жилой) ===\n" +
            "1) query_norm_rules по теме — мин. площади только из каталога.\n" +
            "2) Зонирование по программе типологии — не «две комнаты»; соблюдай adjacency из типологии.\n" +
            "3) Стены одним вызовом; двери batch_execute до 4 (hostWallId = стена между нужными комнатами).\n" +
            "4) Вход: тамбур/вестибюль на уличном фасаде → гостевая зона; служебный путь отдельно.\n" +
            "5) Гостевые санузлы — только из холла/коридора гостей; запрет входа WC через зал с одной стороны " +
            "и через кухню/техзону с другой.\n" +
            "6) tag_rooms; color_splash по «Назначение»; dimension_room_walls на ключевые.\n" +
            "7) run_norm_audit mode=report в конце.\n" +
            "Работай в чистой зоне плана.\n" +
            "Пример: «Кафе на 40 мест» → declare_plan → query_norm_rules → стены → … → run_norm_audit.\n" +
            "Пример: «Школа» → вестибюль+коридор+классы+учительская+санузлы, не 2 комнаты.";

        /// <summary>Rules referenced from <see cref="TypologyPrograms.BuildAgentInstruction"/>.</summary>
        public const string TypologyAgentRules =
            "Скорость: стены — один create_line_based_element. Двери — batch_execute до 4 (create_point_based_element). " +
            "Помещения — create_room по 2 за вызов. После комнат: tag_rooms, color_splash по «Назначение», dimension_room_walls. " +
            "Вход с улицы в тамбур на фасаде → гостевая зона. Гостевые WC только из холла санузлов (не из зала и не из кухни/служебного). " +
            "Дверь: hostWallId по adjacency; смещение точки / facingFlipped внутрь обслуживаемой комнаты. " +
            "Окна на основные помещения. run_norm_audit mode=report в конце.";

        public static bool ShouldIncludeTypology(string userText)
        {
            var text = (userText ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(text,
                "кафе", "общепит", "ресторан", "офис", "open space", "сто", "автосервис",
                "автомойк", "car wash", "типолог", "40 мест", "пищеблок",
                "школ", "класс", "жилой", "квартир", "апартамент");
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
