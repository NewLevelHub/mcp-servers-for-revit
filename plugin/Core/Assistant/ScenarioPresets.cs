using System.Collections.Generic;

namespace revit_mcp_plugin.Core.Assistant
{
    public sealed class ScenarioPreset
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Icon { get; set; }
        public string Prompt { get; set; }
        public bool RequiresConfirmation { get; set; }
    }

    public static class ScenarioPresets
    {
        public static IReadOnlyList<ScenarioPreset> Pilot { get; } = new[]
        {
            new ScenarioPreset
            {
                Id = "axes_dims",
                Label = "Оси и размеры",
                Icon = "▦",
                Prompt =
                    "На активном плане этажа: если осей ещё нет — создай координационные оси по несущим стенам " +
                    "(пузыри снизу/слева, тип марки из проекта). Затем проставь внешние осевые размеры от габарита здания " +
                    "и внутренние размеры помещений (ширина × глубина). Кратко отчитай, что сделано."
            },
            new ScenarioPreset
            {
                Id = "rooms_tags",
                Label = "Rooms и марки",
                Icon = "⌂",
                Prompt =
                    "На активном плане: создай недостающие помещения (Rooms) в замкнутых контурах, " +
                    "поставь марки помещений с площадью через tag_rooms (тип из проекта, если есть). " +
                    "Кратко отчитай число помещений и марок."
            },
            new ScenarioPreset
            {
                Id = "schedules_sheet",
                Label = "Спеки / лист",
                Icon = "☰",
                Prompt =
                    "Подготовь документацию: спецификации дверей и окон (без откосов) и/или экспликацию полов " +
                    "по правилам проекта; при возможности создай лист из рамки проекта и размести результат. " +
                    "Если чего-то не хватает в шаблоне — скажи явно. Кратко отчитай результат."
            },
            new ScenarioPreset
            {
                Id = "norm_audit",
                Label = "Проверить нормы",
                Icon = "✓",
                Prompt =
                    "Проверь активный этаж по нормам через доступные проверки Revit: " +
                    "check_evacuation_width, check_room_depth, check_min_dimensions, check_fire_doors. " +
                    "По нарушениям помещений сделай заливку create_filled_regions (colorPreset red, clearPrevious true), " +
                    "двери покрась через operate_element SetColor красным, подпиши кратко через create_text_notes с выноской. " +
                    "В ответе: сколько нарушений; если проверка недоступна — скажи об этом, не выдумывай нормы."
            },
            new ScenarioPreset
            {
                Id = "clear_mcp_markup",
                Label = "Удалить разметку",
                Icon = "⌫",
                Prompt =
                    "Удали ранее созданную MCP-разметку на активном виде: цветовые области (Filled Region) " +
                    "и текстовые замечания нормоконтроля (MCP-ANN), если они есть. " +
                    "Перед удалением перечисли, что будет удалено, и дождись подтверждения.",
                RequiresConfirmation = true
            }
        };
    }
}
