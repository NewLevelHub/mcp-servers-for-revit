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
        public static string BuildAgentMessage(ScenarioPreset preset, string userPrompt = null)
        {
            if (preset == null)
                return userPrompt ?? "";
            var prompt = !string.IsNullOrWhiteSpace(userPrompt)
                ? userPrompt.Trim()
                : (preset.Prompt ?? "");
            if (string.IsNullOrWhiteSpace(preset.AgentInstruction))
                return prompt;
            return prompt + "\n\n" + preset.AgentInstruction.Trim();
        }

        public static IReadOnlyList<ScenarioPreset> Pilot { get; } = new[]
        {
            new ScenarioPreset
            {
                Id = "axes_dims",
                Label = "Оси и размеры",
                Icon = "▦",
                Profiles = new[] { ToolCatalog.Profiles.Annotation },
                Hint = "Оси по несущим стенам → внешние размеры по габариту. Внутренние — выборочно, не пачкой на плотном плане.",
                Prompt =
                    "На активном плане этажа: если осей ещё нет — создай координационные оси по несущим стенам " +
                    "(пузыри снизу и слева, тип марки из проекта). Затем проставь внешние осевые размеры от габарита здания. " +
                    "Внутренние размеры помещений (ширина × глубина) — только по крупным помещениям или с увеличенным отступом; " +
                    "на плотном ядре (лифт, коридоры, санузлы) не ставь все цепочки пачкой — они налезают. " +
                    "Кратко отчитай, что сделано.",
                AgentInstruction =
                    "create_grid autoFromWalls если осей нет; dimension_grids для внешних цепочек. " +
                    "dimension_room_walls placement=interior — выборочно, не на все комнаты подряд на плотном плане. " +
                    "После внутренних размеров: get_current_view_elements OST_Dimensions — при наложении offsetMm↑ или Delete + redo."
            },
            new ScenarioPreset
            {
                Id = "floor_dims_no_overlap",
                Label = "Размеры этажа (без налезания)",
                Icon = "↔",
                Profiles = new[] { ToolCatalog.Profiles.Annotation },
                Hint = "2–3 крупные комнаты внутри + внешние осевые; проверка, что цепочки не налезают.",
                Prompt =
                    "На активном плане этажа: проставь внутренние размеры (ширина × глубина) у 2–3 крупных помещений " +
                    "и внешние осевые размеры от габарита здания. " +
                    "Проверь, что размерные цепочки не налезают друг на друга; если налезают — увеличь отступ или убери лишние. " +
                    "Кратко отчитай, что сделано.",
                AgentInstruction =
                    "dimension_room_walls placement=interior — выборочно крупные комнаты (не пачкой на плотное ядро). " +
                    "После ≥3 комнат или по запросу «без налезания»: get_current_view_elements OST_Dimensions; " +
                    "при overlap — operate_element Delete лишних + redo с большим offsetMm. " +
                    "Затем dimension_grids от габарита здания."
            },
            new ScenarioPreset
            {
                Id = "rooms_tags",
                Label = "Rooms и марки",
                Icon = "⌂",
                Profiles = new[] { ToolCatalog.Profiles.Modeling, ToolCatalog.Profiles.Annotation },
                Hint = "Сначала «Граница помещения» у стен → помещения в контурах → марки с площадью.",
                Prompt =
                    "На активном плане: проверь у ограждающих стен параметр «Граница помещения» " +
                    "(если снят — включи, иначе площадь 0). Затем создай недостающие помещения в замкнутых контурах " +
                    "и поставь марки помещений с площадью (тип из проекта, если есть). " +
                    "Кратко отчитай число помещений и марок.",
                AgentInstruction =
                    "Перед create_room: set_element_parameter «Граница помещения»=true на несущих/ограждающих стенах при необходимости. " +
                    "Марки: tag_rooms с типом площади из проекта, если есть."
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
                Id = "learn_revit",
                Label = "Научи меня",
                Icon = "🎓",
                Profiles = new[] { ToolCatalog.Profiles.Learning },
                Hint = "Разбор по шагам: где кнопка, что нажать, почему так. Ассистент объясняет, делаете вы.",
                Prompt =
                    "Я только начинаю работать в Revit. Разбери со мной по шагам, что я хочу сделать: " +
                    "спроси, с чего начнём, и дальше веди по одному шагу за раз.",
                AgentInstruction =
                    "Режим обучения: не выполняй действия за человека. Один шаг за раз, " +
                    "жди подтверждения перед следующим. Термины расшифровывай при первом употреблении."
            },
        };
    }
}
