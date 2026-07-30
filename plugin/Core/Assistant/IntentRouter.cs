using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Picks assistant tool profiles from user text (REV-112).
    /// Heuristic first (fast, CI-safe); optional cheap LLM call when ambiguous.
    /// </summary>
    public static class IntentRouter
    {
        private const string LlmSystemPrompt =
            "You route Revit assistant requests to tool profiles. " +
            "Reply with 1–3 profile names from this list only, space-separated, nothing else:\n" +
            "modeling annotation schedules sheets norms data\n" +
            "modeling=walls/doors/rooms/stairs; annotation=grids/dims/tags/colors; " +
            "schedules=TEP/door/window/floor schedules; sheets=place on sheet; " +
            "norms=norm audit/violations/checks; data=stats/exports/materials.\n" +
            "If the user asks to design a layout «по нормам», use modeling annotation — NOT norms.";

        /// <summary>
        /// Keyword / phrase routing. Returns non-core profiles (may be empty → core only).
        /// </summary>
        public static IReadOnlyList<string> ResolveHeuristic(string userText)
        {
            var text = (userText ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Routing trap: «планировка по нормам» → modeling, not norm audit.
            var isLayoutDesign =
                ContainsAny(text, "планировк", "спроектируй", "построй", "нарисуй стен", "layout");

            if (isLayoutDesign)
            {
                hits.Add(ToolCatalog.Profiles.Modeling);
                hits.Add(ToolCatalog.Profiles.Annotation);
                return hits.ToArray();
            }

            if (ContainsAny(text,
                    "нарушен", "нормоконтрол", "проверь этаж", "проверь норм", "по нормам",
                    "заливк", "выноск", "цветовой област", "run_norm", "эвакуац",
                    "противопожар", "коридор", "проверь ширин", "глубин", "балко", "лоджи",
                    "тамбур", "покажи нарушен", "подпиши нарушен", "check_"))
                hits.Add(ToolCatalog.Profiles.Norms);

            // «Сколько глубина» — metrics (norms profile), not audit-only path.
            if (ContainsAny(text, "глубин") && ContainsAny(text, "помещен", "комнат", "сколько"))
                hits.Add(ToolCatalog.Profiles.Norms);

            if (ContainsAny(text,
                    "тэп", "tep", "спецификац", "ведомост", "экспликац", "расписание",
                    "door_schedule", "window_schedule", "floor_schedule", "гост 21"))
                hits.Add(ToolCatalog.Profiles.Schedules);

            if (ContainsAny(text, "на лист", "штамп", "title block", "разлож", "place_view", "auto_layout"))
                hits.Add(ToolCatalog.Profiles.Sheets);

            if (ContainsAny(text,
                    "маркам", "марки ", "марку", "размеры", "размер ", "оси", "осей", "сетк",
                    "выноск", "подпиши комнат", "tag_room", "dimension", "color_splash",
                    "раскрась", "цветовой схем", "детализир"))
                hits.Add(ToolCatalog.Profiles.Annotation);

            if (ContainsAny(text,
                    "стен", "двер", "окон", "комнат", "помещен", "лестниц", "огражден",
                    "создай пол", "перекрыт", "шахт", "create_line", "create_room", "create_point"))
                hits.Add(ToolCatalog.Profiles.Modeling);

            if (ContainsAny(text,
                    "статистик", "сколько стен", "сколько двер", "материал", "объём", "объем",
                    "квартирограф", "analyze_model", "выделен", "selected"))
                hits.Add(ToolCatalog.Profiles.Data);

            // Spec + sheet phrasing often needs both.
            if (hits.Contains(ToolCatalog.Profiles.Schedules) && ContainsAny(text, "лист"))
                hits.Add(ToolCatalog.Profiles.Sheets);

            // «Сколько помещений / площади» — core export_room_data is enough; avoid modeling.
            if (ContainsAny(text, "сколько помещен", "какие площади", "площади помещен")
                && !ContainsAny(text, "создай", "построй", "марки"))
            {
                hits.Remove(ToolCatalog.Profiles.Modeling);
                hits.Remove(ToolCatalog.Profiles.Annotation);
            }

            return hits.ToArray();
        }

        /// <summary>True when heuristic found nothing useful — worth a cheap LLM call.</summary>
        public static bool IsAmbiguous(IReadOnlyList<string> heuristicProfiles) =>
            heuristicProfiles == null || heuristicProfiles.Count == 0;

        public static async Task<IReadOnlyList<string>> ResolveAsync(
            string userText,
            IChatCompletionsClient llm = null,
            CancellationToken cancellationToken = default)
        {
            var heuristic = ResolveHeuristic(userText);
            if (!IsAmbiguous(heuristic) || llm == null)
                return heuristic;

            try
            {
                var fromLlm = await ResolveWithLlmAsync(llm, userText, cancellationToken)
                    .ConfigureAwait(false);
                if (fromLlm != null && fromLlm.Count > 0)
                    return fromLlm;
            }
            catch
            {
                // Fall through to data fallback.
            }

            // Safe default when nothing matched: data (stats/exports) + keep core.
            return new[] { ToolCatalog.Profiles.Data };
        }

        public static async Task<IReadOnlyList<string>> ResolveWithLlmAsync(
            IChatCompletionsClient client,
            string userText,
            CancellationToken cancellationToken = default)
        {
            if (client == null)
                return Array.Empty<string>();

            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = LlmSystemPrompt },
                new JObject { ["role"] = "user", ["content"] = userText ?? "" },
            };

            var completion = await client.ChatCompletionsAsync(messages, null, cancellationToken)
                .ConfigureAwait(false);
            var content = completion["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
            return ParseProfileReply(content);
        }

        public static IReadOnlyList<string> ParseProfileReply(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return Array.Empty<string>();

            var known = new HashSet<string>(ToolCatalog.Profiles.AllNonCore, StringComparer.OrdinalIgnoreCase);
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in content.Split(new[] { ' ', ',', ';', '\n', '\r', '\t', '|', '/' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.Trim().Trim('.', ':', '"', '\'', '`').ToLowerInvariant();
                if (!known.Contains(t))
                    continue;
                if (seen.Add(t))
                    found.Add(t);
            }

            return found;
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
