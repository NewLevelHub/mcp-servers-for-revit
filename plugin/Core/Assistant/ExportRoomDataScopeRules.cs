using System;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// When to auto-scope <c>export_room_data</c> to the active floor (REV-132).
    /// Project-wide queries must not get <c>filterByActiveView</c> injected.
    /// </summary>
    public static class ExportRoomDataScopeRules
    {
        public static bool WantsProjectWideExport(string userText)
        {
            var text = (userText ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(text,
                "в проект", "всего", "всём здан", "во всем здан", "по всему здан",
                "целиком", "whole project", "entire building", "в модел", "в модели",
                "по зданию", "в здании");
        }

        public static bool WantsFloorScopedExport(string userText)
        {
            var text = (userText ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(text,
                "на этаже", "на плане", "на этом этаже", "на виде", "на текущ",
                "на активн", "сколько помещен", "сколько комнат", "какие площади",
                "площади помещен");
        }

        public static bool ShouldInjectActiveViewFilter(string userText)
        {
            if (WantsProjectWideExport(userText))
                return false;
            return WantsFloorScopedExport(userText);
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
