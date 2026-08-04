using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>One create_*/mutate batch recorded for session undo hints (REV-126).</summary>
    public sealed class CreationJournalEntry
    {
        public string ToolName { get; set; }
        public IList<long> ElementIds { get; set; } = new List<long>();
        public DateTime Utc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Compact session journal of element ids created by the assistant (REV-126).
    /// Enables «удали то, что ты создал» without re-scanning the model.
    /// </summary>
    public sealed class SessionCreationJournal
    {
        public const int MaxEntries = 40;
        public const int MaxIds = 200;

        private readonly List<CreationJournalEntry> _entries = new List<CreationJournalEntry>();
        private readonly object _lock = new object();

        public int EntryCount
        {
            get
            {
                lock (_lock) return _entries.Count;
            }
        }

        public void Clear()
        {
            lock (_lock) _entries.Clear();
        }

        public IReadOnlyList<CreationJournalEntry> Snapshot()
        {
            lock (_lock)
            {
                return _entries.Select(e => new CreationJournalEntry
                {
                    ToolName = e.ToolName,
                    ElementIds = e.ElementIds.ToList(),
                    Utc = e.Utc
                }).ToList();
            }
        }

        /// <summary>
        /// Record ids from a successful tool result when the tool mutates the model.
        /// Parses raw AIResult / shaped payloads.
        /// </summary>
        public bool TryRecord(string toolName, string rawOrShapedJson)
        {
            if (!ShouldTrack(toolName))
                return false;

            var ids = ExtractIds(rawOrShapedJson);
            if (ids.Count == 0)
                return false;

            lock (_lock)
            {
                _entries.Add(new CreationJournalEntry
                {
                    ToolName = toolName,
                    ElementIds = ids,
                    Utc = DateTime.UtcNow
                });

                while (_entries.Count > MaxEntries)
                    _entries.RemoveAt(0);

                var totalIds = _entries.Sum(e => e.ElementIds.Count);
                while (totalIds > MaxIds && _entries.Count > 0)
                {
                    totalIds -= _entries[0].ElementIds.Count;
                    _entries.RemoveAt(0);
                }
            }

            return true;
        }

        public string FormatForPrompt()
        {
            lock (_lock)
            {
                if (_entries.Count == 0)
                    return "";

                var sb = new StringBuilder();
                sb.Append("[ЖУРНАЛ] Создано в этой сессии (для «удали что создал» бери эти id): ");
                var parts = new List<string>();
                // Newest last in storage; show last few for the model.
                foreach (var e in _entries.Skip(Math.Max(0, _entries.Count - 8)))
                {
                    var ids = e.ElementIds;
                    string idPart;
                    if (ids.Count <= 6)
                        idPart = string.Join(", ", ids);
                    else
                        idPart = ids[0] + "…" + ids[ids.Count - 1] + " (" + ids.Count + ")";
                    parts.Add(e.ToolName + ": " + idPart);
                }

                sb.Append(string.Join("; ", parts));
                return sb.ToString();
            }
        }

        public static bool ShouldTrack(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;
            var n = toolName.Trim();
            if (n.StartsWith("create_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.StartsWith("dimension_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.StartsWith("tag_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(n, "annotate_norm_findings", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(n, "create_filled_regions", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(n, "create_text_notes", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(n, "create_text_note", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        public static List<long> ExtractIds(string json)
        {
            var ids = new List<long>();
            if (string.IsNullOrWhiteSpace(json))
                return ids;

            try
            {
                var token = JToken.Parse(json);
                CollectIds(token, ids);
            }
            catch
            {
                return ids;
            }

            // Dedupe preserving order.
            var seen = new HashSet<long>();
            var unique = new List<long>();
            foreach (var id in ids)
            {
                if (id <= 0) continue;
                if (!seen.Add(id)) continue;
                unique.Add(id);
            }

            return unique;
        }

        private static void CollectIds(JToken token, List<long> ids)
        {
            if (token == null) return;

            if (token is JObject jo)
            {
                // Prefer Response / CreatedElementIds / created payloads.
                foreach (var key in new[]
                         {
                             "Response", "response", "CreatedElementIds", "createdElementIds",
                             "created", "Created", "data"
                         })
                {
                    if (jo[key] != null)
                        CollectIds(jo[key], ids);
                }

                // Shaped tool result often nests under data.
                if (jo["data"] is JObject data)
                {
                    foreach (var key in new[] { "Response", "response", "CreatedElementIds", "items" })
                    {
                        if (data[key] != null)
                            CollectIds(data[key], ids);
                    }
                }

                if (jo["items"] is JArray itemsArr)
                    CollectIds(itemsArr, ids);

                var single = JTokenParsing.FirstLong(jo, "ElementId", "elementId", "Id", "id", "roomId",
                    "ScheduleId", "SheetId", "textNoteId");
                if (single.HasValue)
                    ids.Add(single.Value);

                return;
            }

            if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (item == null) continue;
                    if (item.Type == JTokenType.Integer || item.Type == JTokenType.Float)
                    {
                        var n = JTokenParsing.GetLong(item);
                        if (n.HasValue) ids.Add(n.Value);
                    }
                    else if (item is JObject)
                    {
                        CollectIds(item, ids);
                    }
                }
            }
        }
    }
}
