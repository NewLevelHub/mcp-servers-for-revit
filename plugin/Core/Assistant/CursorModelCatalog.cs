using System;
using System.Collections.Generic;
using System.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Cursor model ids offered in settings. Verified against Cursor.models.list()
    /// on 2026-08-12 — "auto-smart" does not exist; the auto-router is "default".
    /// </summary>
    public static class CursorModelCatalog
    {
        public sealed class Entry
        {
            public string Id { get; set; }
            public string Label { get; set; }
        }

        public static readonly IReadOnlyList<Entry> Entries = new List<Entry>
        {
            new Entry { Id = AssistantBridgeLauncher.DefaultModelId, Label = "Авто — модель под сложность задачи (рекомендуется)" },
            new Entry { Id = "composer-2.5", Label = "Composer 2.5 — быстрый" },
            new Entry { Id = "claude-sonnet-5", Label = "Claude Sonnet 5" },
            new Entry { Id = "claude-opus-5", Label = "Claude Opus 5 — самый сильный" },
            new Entry { Id = "gpt-5.6-sol", Label = "GPT-5.6" },
            new Entry { Id = "gemini-3.1-pro", Label = "Gemini 3.1 Pro" },
        };

        /// <summary>Ids written by earlier builds that the Cursor API rejects.</summary>
        private static readonly HashSet<string> LegacyAutoIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "auto",
                "auto-smart",
                "auto-smart:balanced",
                "auto-smart:intelligence",
            };

        /// <summary>Maps a stored id onto something the API accepts.</summary>
        public static string Normalize(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || LegacyAutoIds.Contains(id.Trim()))
                return AssistantBridgeLauncher.DefaultModelId;
            return id.Trim();
        }

        public static string LabelFor(string id)
        {
            var normalized = Normalize(id);
            var entry = Entries.FirstOrDefault(e =>
                string.Equals(e.Id, normalized, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return normalized;

            // Settings labels carry a hint after the dash; the chat chip wants the short name.
            var dash = entry.Label.IndexOf(" — ", StringComparison.Ordinal);
            return dash > 0 ? entry.Label.Substring(0, dash) : entry.Label;
        }
    }
}
