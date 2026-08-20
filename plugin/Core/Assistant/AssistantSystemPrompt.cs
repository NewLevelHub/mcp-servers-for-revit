using System;
using System.Collections.Generic;
using System.Text;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// System instructions for the in-Revit agent — core + intent playbooks (REV-118).
    /// Aligned with .cursor/rules/revit-mcp.mdc for norm violation display.
    /// </summary>
    public static class AssistantSystemPrompt
    {
        /// <summary>Always-on instructions (target ≤800 characters).</summary>
        public const string Core =
            "Ты AI-ассистент архитектора в Autodesk Revit. Пользователь — проектировщик, не разработчик. " +
            "Отвечай кратко по-русски (1–3 предложения + «сделано»). Не называй имена инструментов, JSON, MCP, Cursor. " +
            "Единицы: мм, м², м³. Перед create_* — get_current_view_info; typeId обязателен для create_*_element. " +
            "Дефолты называй в ответе; уточнения — ask_user (блок ниже), ≤1 за запрос; " +
            "ошибка инструмента — не повторяй тот же вызов, исправь причину или скажи честно; " +
            "не выдумывай числа, id, типы и пункты норм — только из ответов инструментов; " +
            "сначала контекст; create_* после declare_plan (чтение — без плана). " +
            "get_current_view_info и типы семейств — не чаще одного раза за задачу. " +
            "send_code_to_revit — только с явным разрешением C#; новый .rvt — Файл→Новый→Проект вручную. " +
            "Вложения [Вложения] — ты их видишь.";
        // Про query_revit_ui сознательно не пишем здесь: в Core остаётся 3 символа запаса,
        // а инструкция есть в описании самого инструмента и в плейбуке наставника,
        // где она и нужна больше всего.

        /// <summary>Legacy full prompt length before REV-118 (for regression tests).</summary>
        public const int LegacyMonolithLength = 2500;

        /// <summary>Backward-compatible alias — core only (playbooks added via <see cref="Build"/>).</summary>
        public const string Text = Core;

        /// <summary>Assemble core + always-on clarification + profile playbooks.</summary>
        public static string Build(IReadOnlyList<string> profiles, string userText = null)
        {
            var playbooks = AssistantPlaybooks.Build(profiles, userText);
            var sb = new StringBuilder();
            sb.Append(Core);

            // REV-153: в режиме наставника блок уточнений неуместен — он про то,
            // как быстрее начать строить, а здесь строить нельзя вовсе.
            if (!ToolCatalog.IsLearningMode(profiles))
            {
                sb.Append("\n\n");
                sb.Append(AssistantPlaybooks.Clarification);
            }
            if (!string.IsNullOrWhiteSpace(playbooks))
            {
                sb.Append("\n\n");
                sb.Append(playbooks);
            }

            return sb.ToString();
        }

        /// <summary>All instruction fragments for schema-alignment guardian.</summary>
        public static IReadOnlyList<string> CollectAllInstructionTexts()
        {
            var list = new List<string> { Core, AssistantPlaybooks.Clarification };
            list.AddRange(AssistantPlaybooks.AllBodies);
            return list;
        }
    }
}
