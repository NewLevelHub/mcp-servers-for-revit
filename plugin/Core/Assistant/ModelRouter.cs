using System;
using System.Collections.Generic;
using revit_mcp_plugin.Configuration;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Picks fast vs smart chat model from active tool profiles (REV-124).
    /// Never routes by user-text regex — profiles come from IntentRouter / chips.
    /// </summary>
    public static class ModelRouter
    {
        public const string DefaultFastModel = "gpt-4o-mini";
        public const string EscalationNotice = "Переключаюсь на более сильную модель…";

        /// <summary>
        /// Profiles that need a stronger model (layout, dimensions, norms, sheets).
        /// <c>data</c> / core-only stay on fast.
        /// </summary>
        public static bool RequiresSmart(IEnumerable<string> profiles)
        {
            if (profiles == null)
                return false;

            foreach (var raw in profiles)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var p = raw.Trim();
                if (p.Equals(ToolCatalog.Profiles.Modeling, StringComparison.OrdinalIgnoreCase)
                    || p.Equals(ToolCatalog.Profiles.Annotation, StringComparison.OrdinalIgnoreCase)
                    || p.Equals(ToolCatalog.Profiles.Norms, StringComparison.OrdinalIgnoreCase)
                    || p.Equals(ToolCatalog.Profiles.Schedules, StringComparison.OrdinalIgnoreCase)
                    || p.Equals(ToolCatalog.Profiles.Sheets, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static string NormalizeFast(string fastModel)
        {
            return string.IsNullOrWhiteSpace(fastModel) ? DefaultFastModel : fastModel.Trim();
        }

        public static string NormalizeSmart(string smartModel)
        {
            return string.IsNullOrWhiteSpace(smartModel) ? "" : smartModel.Trim();
        }

        /// <summary>
        /// Resolve model id for the turn. Empty smart → always fast (backward compatible).
        /// </summary>
        public static string Resolve(
            string fastModel,
            string smartModel,
            IEnumerable<string> profiles,
            out bool isSmart)
        {
            var fast = NormalizeFast(fastModel);
            var smart = NormalizeSmart(smartModel);
            if (smart.Length > 0 && RequiresSmart(profiles))
            {
                isSmart = true;
                return smart;
            }

            isSmart = false;
            return fast;
        }

        public static string Resolve(ServiceSettings settings, IEnumerable<string> profiles, out bool isSmart)
        {
            settings = settings ?? new ServiceSettings();
            return Resolve(settings.AssistantModel, settings.AssistantModelSmart, profiles, out isSmart);
        }

        /// <summary>True when a mid-turn upgrade from fast → smart is possible.</summary>
        public static bool CanEscalate(string smartModel, bool currentlySmart)
        {
            return !currentlySmart && NormalizeSmart(smartModel).Length > 0;
        }

        public static bool CanEscalate(ServiceSettings settings, bool currentlySmart)
        {
            return CanEscalate(settings?.AssistantModelSmart, currentlySmart);
        }
    }
}
