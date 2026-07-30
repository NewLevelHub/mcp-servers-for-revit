using System.Collections.Generic;

namespace revit_mcp_plugin.Core.Assistant
{
    public sealed class ScenarioPreset
    {
        public string Id { get; set; }
        /// <summary>Текст на кнопке-чипе.</summary>
        public string Label { get; set; }
        public string Icon { get; set; }
        /// <summary>Что видит архитектор в чате (по-русски, без имён tools).</summary>
        public string Prompt { get; set; }
        /// <summary>Доп. инструкция только для модели (не показывается в пузыре).</summary>
        public string AgentInstruction { get; set; }
        /// <summary>Подсказка при наведении на чип — что будет сделано.</summary>
        public string Hint { get; set; }
        /// <summary>Assistant tool profiles for REV-112 (chip → exact profiles, no router).</summary>
        public string[] Profiles { get; set; }
    }

    public static class ScenarioPresets
    {
        public static string BuildAgentMessage(ScenarioPreset preset)
        {
            if (preset == null)
                return "";
            if (string.IsNullOrWhiteSpace(preset.AgentInstruction))
                return preset.Prompt ?? "";
            return (preset.Prompt ?? "").Trim() + "\n\n" + preset.AgentInstruction.Trim();
        }

        public static IReadOnlyList<ScenarioPreset> Pilot { get; } = new[]
        {
            new ScenarioPreset
            {
                Id = "axes_dims",
                Label = "Оси и размеры",
                Icon = "▦",
                Profiles = new[] { ToolCatalog.Profiles.Annotation },
                Hint = "Оси по несущим стенам → внешние размеры по габариту → размеры внутри помещений.",
                Prompt =
                    "На активном плане этажа: если осей ещё нет — создай координационные оси по несущим стенам " +
                    "(пузыри снизу и слева, тип марки из проекта). Затем проставь внешние осевые размеры от габарита здания " +
                    "и внутренние размеры помещений (ширина × глубина). Кратко отчитай, что сделано."
            },
            new ScenarioPreset
            {
                Id = "rooms_tags",
                Label = "Rooms и марки",
                Icon = "⌂",
                Profiles = new[] { ToolCatalog.Profiles.Modeling, ToolCatalog.Profiles.Annotation },
                Hint = "Помещения в замкнутых контурах → марки с площадью.",
                Prompt =
                    "На активном плане: создай недостающие помещения в замкнутых контурах " +
                    "и поставь марки помещений с площадью (тип из проекта, если есть). " +
                    "Кратко отчитай число помещений и марок."
            },
            new ScenarioPreset
            {
                Id = "schedules_sheet",
                Label = "Спеки / лист",
                Icon = "☰",
                Profiles = new[] { ToolCatalog.Profiles.Schedules, ToolCatalog.Profiles.Sheets },
                Hint = "ТЭП, спецификации или экспликация полов — на лист из рамки проекта.",
                Prompt =
                    "Подготовь ведомости/таблицы на листе проекта: ТЭП, спецификации дверей и окон " +
                    "или экспликацию полов — по смыслу запроса. Используй шаблоны и рамку из проекта. " +
                    "Если чего-то нет в шаблоне — скажи явно. Кратко отчитай результат.",
                AgentInstruction =
                    "Если просят ТЭП — только render_tep_table (не спецификацию дверей). " +
                    "Спеки: create_door_schedule / create_window_schedule / create_floor_explication / create_floor_schedule. " +
                    "Размещение: auto_layout_sheet или place_view_on_sheet только с реальным viewId из ответа create_*."
            },
            new ScenarioPreset
            {
                Id = "norm_audit",
                Label = "Проверить нормы",
                Icon = "✓",
                Profiles = new[] { ToolCatalog.Profiles.Norms },
                Hint = "Проверка по ГОСТ/СП → красные заливки, выноски, покраска дверей при нарушениях.",
                Prompt =
                    "Проверь активный этаж по нормам (СП РК). " +
                    "По нарушениям: красная заливка помещений, красные двери, выноски с пунктом нормы. " +
                    "В ответе — сколько нарушений и какие проверки сработали.",
                AgentInstruction =
                    "run_norm_audit mode=report. Нарушения: create_filled_regions roomIds из findings " +
                    "(violation + nearLimit), colorPreset=red, clearPrevious=true; двери — operate_element SetColor красным. " +
                    "Подписи: annotate_norm_findings style=leader после заливки. " +
                    "Каталог PDF не обязателен — есть встроенные нормы."
            },
            new ScenarioPreset
            {
                Id = "layout_from_scratch",
                Label = "Планировка с нуля",
                Icon = "▣",
                Profiles = new[]
                {
                    ToolCatalog.Profiles.Modeling,
                    ToolCatalog.Profiles.Annotation,
                    ToolCatalog.Profiles.Norms,
                },
                Hint = "Нормы → стены → двери → помещения по 1–2 → марки. Для блока, тестового этажа.",
                Prompt =
                    "На активном плане этажа спроектируй функциональную планировку по запросу: " +
                    "сначала нормы из каталога, затем контур стен, двери, помещения с марками и площадью. " +
                    "Кратко отчитай состав помещений и площади.",
                AgentInstruction =
                    "СТРОГО: get_current_view_info → get_available_family_types OST_Walls → " +
                    "create_line_based_element data=[{category:OST_Walls, typeId, locationLine:{p0,p1}, height:3000, baseLevel, baseOffset:0}]. " +
                    "Если стены упали — СТОП, без create_room. После стен: create_room по 1–2, location в ячейке; " +
                    "dimension_room_walls roomId=ElementId из ответа. НЕ create_grid для стен. " +
                    "Не проси пользователя добавить стены вручную."
            },
            new ScenarioPreset
            {
                Id = "clear_mcp_markup",
                Label = "Удалить разметку",
                Icon = "⌫",
                Profiles = new[] { ToolCatalog.Profiles.Norms },
                Hint = "Снять заливки, выноски и красную графику дверей/окон после нормоконтроля.",
                Prompt =
                    "Сними на активном виде разметку нормоконтроля: красные заливки помещений, " +
                    "выноски с замечаниями и красную графику дверей, окон и пандусов. " +
                    "Не удаляй элементы модели и не крась все помещения. Кратко отчитай, сколько снято.",
                AgentInstruction =
                    "Вызови по порядку: create_filled_regions clearOnly=true; " +
                    "create_text_notes clearOnly=true; " +
                    "operate_element data action ResetOverrides, elementIds [], " +
                    "categoryNames [\"Doors\",\"Windows\",\"Ramps\"] (массив строк). " +
                    "Не delete_element, не create_filled_regions с пустым roomIds."
            }
        };
    }
}
