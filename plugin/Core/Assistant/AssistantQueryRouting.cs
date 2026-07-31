using System;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Shared phrase detection for assistant routing traps (REV-134).
    /// </summary>
    public static class AssistantQueryRouting
    {
        /// <summary>
        /// «Сколько глубина помещения» — raw geometry, not export_room_data / norm audit.
        /// </summary>
        public static bool WantsRoomDepthMetrics(string userText)
        {
            var text = (userText ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (!ContainsAny(text, "глубин", "depth", "терең"))
                return false;

            // Norm audit phrasing — check_room_depth / run_norm_audit, not raw metrics.
            if (ContainsAny(text, "провер", "нарушен", "норм", "check_", "audit", "соответств"))
                return false;

            var asksMeasure = ContainsAny(text,
                "сколько", "какая", "какой", "какую", "каков", "какие");
            var hasRoomContext = ContainsAny(text,
                "помещен", "комнат", "этаж", "этого", "этом", "план");

            return asksMeasure && hasRoomContext;
        }

        /// <summary>
        /// «Ведомость по ГОСТ 21.501» — door schedule template, not generic create_schedule.
        /// </summary>
        public static bool WantsGostDoorSchedule(string userText)
        {
            var text = (userText ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var hasScheduleIntent = ContainsAny(text,
                "ведомост", "спецификац", "schedule");
            var hasGost21501 = ContainsAny(text,
                "гост 21.501", "гост 21501", "21.501", "21 501", "21-501");

            return hasScheduleIntent && hasGost21501;
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
