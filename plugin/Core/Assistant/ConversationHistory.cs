using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>Snapshot of history fill for the chat header meter (REV-126).</summary>
    public sealed class HistoryBudget
    {
        public int UserTurns { get; set; }
        public int MaxPreviousUserTurns { get; set; }
        public int EstimatedChars { get; set; }
        public int MaxHistoryChars { get; set; }

        /// <summary>Allowed user turns including the current one (= MaxPrevious + 1).</summary>
        public int MaxUserTurnsInclusive => MaxPreviousUserTurns + 1;

        public int FillPercent
        {
            get
            {
                var turnPct = MaxUserTurnsInclusive <= 0
                    ? 0
                    : (int)Math.Round(100.0 * UserTurns / MaxUserTurnsInclusive);
                var charPct = MaxHistoryChars <= 0
                    ? 0
                    : (int)Math.Round(100.0 * EstimatedChars / MaxHistoryChars);
                return Math.Max(0, Math.Min(100, Math.Max(turnPct, charPct)));
            }
        }

        public string MeterLabel =>
            $"Контекст: {UserTurns}/{MaxUserTurnsInclusive}";
    }

    /// <summary>
    /// OpenAI-shaped chat history with atomic turn trim and old-turn summarization (REV-126).
    /// Never splits assistant <c>tool_calls</c> from their tool results.
    /// </summary>
    public sealed class ConversationHistory
    {
        public const int DefaultMaxPreviousUserTurns = 12;
        public const int DefaultMaxHistoryChars = 120000;
        public const int MaxSummaries = 5;
        public const int MaxSummaryChars = 600;

        private readonly List<JObject> _messages = new List<JObject>();
        private readonly List<string> _summaries = new List<string>();
        private readonly object _lock = new object();

        public int MaxPreviousUserTurns { get; set; } = DefaultMaxPreviousUserTurns;
        public int MaxHistoryChars { get; set; } = DefaultMaxHistoryChars;

        public int Count
        {
            get
            {
                lock (_lock) return _messages.Count;
            }
        }

        public int SummaryCount
        {
            get
            {
                lock (_lock) return _summaries.Count;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _messages.Clear();
                _summaries.Clear();
            }
        }

        public HistoryBudget GetBudget()
        {
            lock (_lock)
                return GetBudgetUnlocked();
        }

        public void EnsureSystemPrompt(string content)
        {
            lock (_lock)
                EnsureSystemPromptUnlocked(content);
        }

        public void Add(JObject message)
        {
            if (message == null) return;
            lock (_lock)
                _messages.Add(message);
        }

        /// <summary>Clone messages for the API, injecting [СВОДКА] blocks after the system prompt.</summary>
        public JArray CloneForApi()
        {
            lock (_lock)
                return CloneForApiUnlocked();
        }

        /// <summary>Test helper: shallow copy of current messages (no summaries).</summary>
        public IReadOnlyList<JObject> SnapshotMessages()
        {
            lock (_lock)
                return _messages.Select(m => (JObject)m.DeepClone()).ToList();
        }

        public IReadOnlyList<string> SnapshotSummaries()
        {
            lock (_lock)
                return _summaries.ToList();
        }

        public bool CompactMultimodal(bool keepLastUserIntact)
        {
            lock (_lock)
                return CompactMultimodalUnlocked(keepLastUserIntact);
        }

        public void CompactAllMultimodal(bool keepLastUserIntact)
        {
            lock (_lock)
            {
                while (CompactMultimodalUnlocked(keepLastUserIntact)) { /* keep going */ }
            }
        }

        public void SanitizeToolPairs()
        {
            lock (_lock)
                SanitizeToolPairsUnlocked();
        }

        /// <summary>
        /// Drop oldest complete user turns (summarizing first) so the prompt stays within budgets.
        /// Returns true when at least one user turn was removed.
        /// </summary>
        public bool TrimIfNeeded()
        {
            lock (_lock)
                return TrimIfNeededUnlocked();
        }

        private HistoryBudget GetBudgetUnlocked()
        {
            var userTurns = 0;
            var chars = 0;
            foreach (var m in _messages)
            {
                if (IsRole(m, "user"))
                    userTurns++;
                chars += EstimateChars(m);
            }

            foreach (var s in _summaries)
                chars += s?.Length ?? 0;

            return new HistoryBudget
            {
                UserTurns = userTurns,
                MaxPreviousUserTurns = MaxPreviousUserTurns,
                EstimatedChars = chars,
                MaxHistoryChars = MaxHistoryChars
            };
        }

        private bool TrimIfNeededUnlocked()
        {
            if (_messages.Count == 0)
                return false;

            EnsureSystemFirstUnlocked();
            var droppedUserTurns = false;

            while (NeedsTrimUnlocked())
            {
                if (CompactMultimodalUnlocked(keepLastUserIntact: true))
                    continue;

                var userIndexes = new List<int>();
                for (var i = 0; i < _messages.Count; i++)
                {
                    if (IsRole(_messages[i], "user"))
                        userIndexes.Add(i);
                }

                if (userIndexes.Count <= 1)
                {
                    if (!CompactOldestToolPayloadUnlocked())
                        break;
                    continue;
                }

                var dropFrom = userIndexes[0];
                var dropToExclusive = userIndexes[1];
                var removeCount = dropToExclusive - dropFrom;
                if (removeCount <= 0)
                    break;

                RememberSummaryUnlocked(dropFrom, dropToExclusive);
                _messages.RemoveRange(dropFrom, removeCount);
                droppedUserTurns = true;
            }

            SanitizeToolPairsUnlocked();
            EnsureSystemFirstUnlocked();
            return droppedUserTurns;
        }

        private bool NeedsTrimUnlocked()
        {
            var budget = GetBudgetUnlocked();
            return budget.UserTurns > MaxPreviousUserTurns + 1
                   || budget.EstimatedChars > MaxHistoryChars;
        }

        private void RememberSummaryUnlocked(int from, int toExclusive)
        {
            var summary = SummarizeTurnUnlocked(from, toExclusive);
            if (string.IsNullOrWhiteSpace(summary))
                return;

            _summaries.Add(summary);
            while (_summaries.Count > MaxSummaries)
            {
                if (_summaries.Count <= 1)
                    break;
                // Merge two oldest into one compact line.
                var merged = MergeSummaries(_summaries[0], _summaries[1]);
                _summaries.RemoveAt(0);
                _summaries[0] = merged;
            }
        }

        internal static string MergeSummaries(string a, string b)
        {
            var combined = (a ?? "").Trim() + " · " + (b ?? "").Trim();
            if (combined.Length <= MaxSummaryChars)
                return combined;
            return combined.Substring(0, MaxSummaryChars - 1) + "…";
        }

        private string SummarizeTurnUnlocked(int from, int toExclusive)
        {
            var userText = "";
            var toolBits = new List<string>();

            for (var i = from; i < toExclusive && i < _messages.Count; i++)
            {
                var msg = _messages[i];
                if (IsRole(msg, "user") && string.IsNullOrEmpty(userText))
                    userText = ExtractUserText(msg);
                else if (IsRole(msg, "assistant"))
                {
                    var calls = msg["tool_calls"] as JArray;
                    if (calls == null) continue;
                    foreach (var c in calls)
                    {
                        var name = c?["function"]?["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                            toolBits.Add(name);
                    }
                }
                else if (IsRole(msg, "tool"))
                {
                    var content = msg["content"]?.ToString() ?? "";
                    var bit = CompactToolBit(content);
                    if (!string.IsNullOrEmpty(bit) && toolBits.Count > 0)
                        toolBits[toolBits.Count - 1] = toolBits[toolBits.Count - 1] + " " + bit;
                }
            }

            var sb = new StringBuilder();
            sb.Append("[СВОДКА] ");
            if (!string.IsNullOrWhiteSpace(userText))
            {
                var clipped = ClipUserRequest(userText);
                sb.Append("Пользователь: «").Append(clipped).Append('»');
            }
            else
            {
                sb.Append("Пользователь: (без текста)");
            }

            if (toolBits.Count > 0)
            {
                sb.Append(". ");
                sb.Append(string.Join("; ", toolBits.Take(8)));
                if (toolBits.Count > 8)
                    sb.Append("…");
            }

            var result = sb.ToString();
            if (result.Length > MaxSummaryChars)
                result = result.Substring(0, MaxSummaryChars - 1) + "…";
            return result;
        }

        private static string ClipUserRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // Prefer the [Запрос] section if present.
            const string marker = "[Запрос]";
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                text = text.Substring(idx + marker.Length).Trim();

            text = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            while (text.Contains("  "))
                text = text.Replace("  ", " ");

            if (text.Length > 180)
                text = text.Substring(0, 179) + "…";
            return text;
        }

        private static string CompactToolBit(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "";
            try
            {
                var token = JToken.Parse(content);
                if (token is JObject jo)
                {
                    var summary = jo["summary"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(summary))
                        return "(" + Clip(summary, 80) + ")";
                    if (jo["ok"]?.Type == JTokenType.Boolean && jo.Value<bool>("ok") == false)
                        return "(ошибка)";
                    var count = jo["count"];
                    if (count != null)
                        return "(n=" + count + ")";
                }
            }
            catch
            {
                // ignore
            }

            return content.Length > 60 ? "(ok)" : "";
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max - 1) + "…";
        }

        private static string ExtractUserText(JObject message)
        {
            var content = message?["content"];
            if (content is JArray parts)
            {
                foreach (var part in parts)
                {
                    if (string.Equals(part?["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                        return part["text"]?.ToString() ?? "";
                }
                return "";
            }

            return content?.ToString() ?? "";
        }

        private void EnsureSystemPromptUnlocked(string content)
        {
            if (_messages.Count == 0)
            {
                _messages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = content ?? ""
                });
                return;
            }

            if (IsRole(_messages[0], "system"))
                _messages[0]["content"] = content ?? "";
            else
            {
                _messages.Insert(0, new JObject
                {
                    ["role"] = "system",
                    ["content"] = content ?? ""
                });
            }
        }

        /// <summary>
        /// Keep the main system prompt at index 0. Mid-turn system nudges stay in place
        /// (they are not moved; only a stray leading non-system before the prompt is fixed).
        /// </summary>
        private void EnsureSystemFirstUnlocked()
        {
            var systemIdx = -1;
            for (var i = 0; i < _messages.Count; i++)
            {
                if (IsRole(_messages[i], "system"))
                {
                    systemIdx = i;
                    break;
                }
            }

            if (systemIdx <= 0)
                return;

            var system = _messages[systemIdx];
            _messages.RemoveAt(systemIdx);
            _messages.Insert(0, system);
        }

        private JArray CloneForApiUnlocked()
        {
            var arr = new JArray();
            var i = 0;
            if (_messages.Count > 0 && IsRole(_messages[0], "system"))
            {
                arr.Add((JObject)_messages[0].DeepClone());
                i = 1;
            }

            foreach (var summary in _summaries)
            {
                if (string.IsNullOrWhiteSpace(summary)) continue;
                arr.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = summary
                });
            }

            for (; i < _messages.Count; i++)
                arr.Add((JObject)_messages[i].DeepClone());

            return arr;
        }

        private void SanitizeToolPairsUnlocked()
        {
            var i = 0;
            while (i < _messages.Count)
            {
                var msg = _messages[i];
                if (!IsRole(msg, "tool"))
                {
                    i++;
                    continue;
                }

                var prev = i - 1;
                while (prev >= 0 && IsRole(_messages[prev], "tool"))
                    prev--;

                var ok = false;
                if (prev >= 0 && IsRole(_messages[prev], "assistant"))
                {
                    var calls = _messages[prev]["tool_calls"] as JArray;
                    var callId = msg["tool_call_id"]?.ToString();
                    if (calls != null && !string.IsNullOrEmpty(callId))
                    {
                        foreach (var c in calls)
                        {
                            if (string.Equals(c?["id"]?.ToString(), callId, StringComparison.Ordinal))
                            {
                                ok = true;
                                break;
                            }
                        }
                    }
                }

                if (!ok)
                {
                    _messages.RemoveAt(i);
                    continue;
                }

                i++;
            }

            for (var idx = _messages.Count - 1; idx >= 0; idx--)
            {
                var assistantMsg = _messages[idx];
                if (!IsRole(assistantMsg, "assistant"))
                    continue;
                var calls = assistantMsg["tool_calls"] as JArray;
                if (calls == null || calls.Count == 0)
                    continue;

                var needed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in calls)
                {
                    var id = c?["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        needed.Add(id);
                }

                if (needed.Count == 0)
                {
                    _messages.RemoveAt(idx);
                    continue;
                }

                var end = idx + 1;
                while (end < _messages.Count && IsRole(_messages[end], "tool"))
                {
                    var callId = _messages[end]["tool_call_id"]?.ToString();
                    if (!string.IsNullOrEmpty(callId))
                        needed.Remove(callId);
                    end++;
                }

                if (needed.Count > 0)
                    _messages.RemoveRange(idx, end - idx);
            }
        }

        private bool CompactOldestToolPayloadUnlocked()
        {
            for (var i = 0; i < _messages.Count; i++)
            {
                if (!IsRole(_messages[i], "tool"))
                    continue;
                var content = _messages[i]["content"]?.ToString() ?? "";
                if (content.Length <= 400)
                    continue;
                _messages[i]["content"] = "{\"ok\":true,\"truncated\":true}";
                return true;
            }
            return false;
        }

        private bool CompactMultimodalUnlocked(bool keepLastUserIntact)
        {
            var lastUser = -1;
            if (keepLastUserIntact)
            {
                for (var i = _messages.Count - 1; i >= 0; i--)
                {
                    if (IsRole(_messages[i], "user"))
                    {
                        lastUser = i;
                        break;
                    }
                }
            }

            for (var i = 0; i < _messages.Count; i++)
            {
                if (i == lastUser) continue;
                if (!IsRole(_messages[i], "user")) continue;
                if (!(_messages[i]["content"] is JArray parts)) continue;

                var labels = new List<string>();
                string textPart = null;
                foreach (var part in parts)
                {
                    var type = part?["type"]?.ToString();
                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        textPart = part["text"]?.ToString() ?? "";
                    else if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
                        labels.Add("изображение");
                    else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                        labels.Add(part["file"]?["filename"]?.ToString() ?? "файл");
                }

                if (labels.Count == 0)
                    continue;

                var stub = textPart ?? "";
                if (!string.IsNullOrWhiteSpace(stub))
                    stub += "\n\n";
                stub += "[Вложения предыдущего сообщения, данные убраны из памяти: " +
                        string.Join(", ", labels) + "]";
                _messages[i]["content"] = stub;
                return true;
            }

            return false;
        }

        public static int EstimateChars(JObject message)
        {
            if (message == null) return 0;
            var n = 0;
            var content = message["content"];
            if (content is JArray parts)
            {
                foreach (var part in parts)
                {
                    var type = part?["type"]?.ToString() ?? "";
                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        n += part["text"]?.ToString()?.Length ?? 0;
                    else if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
                        n += 3000;
                    else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                        n += 8000;
                    else
                        n += 500;
                }
            }
            else if (content != null)
            {
                n += content.ToString().Length;
            }

            var toolCalls = message["tool_calls"]?.ToString();
            if (toolCalls != null) n += Math.Min(toolCalls.Length, 4000);
            return n;
        }

        public static bool IsRole(JObject message, string role)
        {
            return string.Equals(message?["role"]?.ToString(), role, StringComparison.OrdinalIgnoreCase);
        }
    }
}
