using System;
using System.Collections.Generic;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// When the in-Revit assistant may bypass the LLM for deterministic norm audit (REV-122).
    /// Free text always goes to the model; only the «Проверить нормы» chip runs the preset runner.
    /// </summary>
    public static class AssistantNormAuditRouting
    {
        public const string NormAuditChipId = "norm_audit";

        /// <summary>Golden set «Ловушки роутинга» — must never trigger direct audit from free text.</summary>
        public static IReadOnlyList<string> RoutingTrapQueries { get; } = new[]
        {
            "Спроектируй планировку по нормам на этаже",
            "Какая норма на ширину коридора? Покажи пункт",
            "Сколько глубина этого помещения на этаже?",
            "На активном виде сделай ведомость по ГОСТ 21.501",
        };

        /// <summary>Only the norm-audit scenario chip bypasses the LLM.</summary>
        public static bool ShouldRunDirectNormAudit(string scenarioPresetId) =>
            string.Equals(scenarioPresetId, NormAuditChipId, StringComparison.OrdinalIgnoreCase);

        /// <summary>Free-text user messages always continue to the LLM (attachments included).</summary>
        public static bool ShouldBypassLlmForUserText(string text, bool hasAttachments) => false;

        /// <summary>
        /// Pre-REV-122 substring heuristic — retained for golden regression tests only.
        /// </summary>
        internal static bool LegacySubstringHeuristicWouldMatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var t = text.ToLowerInvariant();
            if (t.Contains("удали разметку") || t.Contains("сними разметку") || t.Contains("убери разметку"))
                return false;

            var norm =
                t.Contains("наруш") || t.Contains("норм") || t.Contains("гост") || t.Contains("сп ")
                || t.Contains("санпин") || t.Contains("эвак") || t.Contains("глубин");
            var action =
                t.Contains("провер") || t.Contains("покаж") || t.Contains("покрас")
                || t.Contains("залей") || t.Contains("подсвет") || t.Contains("этаж")
                || t.Contains("активн");

            return norm && action;
        }
    }
}
