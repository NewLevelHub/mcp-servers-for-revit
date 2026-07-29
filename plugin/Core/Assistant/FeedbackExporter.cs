using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Reads assistant session logs, collects dislike entries, generates a Markdown report,
    /// and marks exported entries so they aren't duplicated on re-export.
    /// </summary>
    public static class FeedbackExporter
    {
        private const string ExportedMarkerFile = "feedback-exported-ids.txt";

        /// <summary>Returns the number of un-exported dislikes across all session logs.</summary>
        public static int CountPendingDislikes()
        {
            try
            {
                var exported = LoadExportedIds();
                return CollectDislikes(exported).Count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Generates a Markdown report of all pending dislikes, saves it to the given directory
        /// (or Logs/ by default), marks entries as exported, and returns the file path.
        /// </summary>
        public static string Export(string targetDir = null)
        {
            var exported = LoadExportedIds();
            var dislikes = CollectDislikes(exported);
            if (dislikes.Count == 0) return null;

            var dir = targetDir ?? PathManager.GetLogsDirectoryPath();
            Directory.CreateDirectory(dir);

            var md = BuildReport(dislikes);
            var fileName = $"assistant-feedback_{DateTime.Now:yyyyMMdd-HHmm}.md";
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, md, Encoding.UTF8);

            // Mark as exported
            foreach (var d in dislikes)
                exported.Add(d.TurnId);
            SaveExportedIds(exported);

            try { System.Windows.Clipboard.SetText(path); } catch { /* clipboard may be locked */ }

            return path;
        }

        // ── Internal data ────────────────────────────────────────────────────────

        private sealed class DislikeEntry
        {
            public string TurnId;
            public DateTime Ts;
            public string Model;
            public string DocTitle;
            public string ViewName;
            public string UserText;
            public string Reason;
            public string Comment;
            public List<string> ToolChain = new List<string>();
            public int Rounds;
            public long TotalMs;
            public string Outcome;
        }

        // ── Collect dislikes by joining session entries + rating patches ─────────

        private static List<DislikeEntry> CollectDislikes(HashSet<string> alreadyExported)
        {
            var dir = PathManager.GetLogsDirectoryPath();
            if (!Directory.Exists(dir)) return new List<DislikeEntry>();

            // Pass 1: collect all turn entries and rating patches
            var turns = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var patches = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(dir, "assistant-sessions_*.jsonl").OrderBy(f => f))
            {
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var jo = JObject.Parse(line);
                        var tid = jo["turnId"]?.ToString();
                        if (string.IsNullOrEmpty(tid)) continue;

                        if (jo["ratingPatch"] != null)
                            patches[tid] = jo;
                        else
                            turns[tid] = jo;
                    }
                    catch { /* skip malformed */ }
                }
            }

            // Pass 2: join patches with turns, filter to dislikes, exclude exported
            var result = new List<DislikeEntry>();
            foreach (var kv in patches)
            {
                var tid = kv.Key;
                if (alreadyExported.Contains(tid)) continue;

                var patch = kv.Value["ratingPatch"] as JObject;
                if (patch == null) continue;
                var rating = patch["rating"]?.Value<int>() ?? 0;
                if (rating >= 0) continue; // only dislikes

                turns.TryGetValue(tid, out var turn);
                var entry = new DislikeEntry
                {
                    TurnId = tid,
                    Ts = turn != null && DateTime.TryParse(turn["ts"]?.ToString(), out var dt) ? dt : DateTime.MinValue,
                    Model = turn?["model"]?.ToString() ?? "?",
                    DocTitle = turn?["docTitle"]?.ToString(),
                    ViewName = turn?["viewName"]?.ToString(),
                    UserText = turn?["userText"]?.ToString() ?? "?",
                    Reason = patch["reason"]?.ToString(),
                    Comment = patch["comment"]?.ToString(),
                    Rounds = turn?["rounds"]?.Value<int>() ?? 0,
                    TotalMs = turn?["totalMs"]?.Value<long>() ?? 0,
                    Outcome = turn?["outcome"]?.ToString(),
                };

                if (turn?["toolCalls"] is JArray tc)
                {
                    foreach (var t in tc)
                        entry.ToolChain.Add(t["name"]?.ToString() ?? "?");
                }

                result.Add(entry);
            }

            return result.OrderBy(d => d.Ts).ToList();
        }

        // ── Markdown report ──────────────────────────────────────────────────────

        private static string BuildReport(List<DislikeEntry> dislikes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Жалобы на AI-ассистент · {DateTime.Now:dd.MM.yyyy HH:mm}");
            sb.AppendLine();
            sb.AppendLine($"Всего дизлайков: **{dislikes.Count}**");
            sb.AppendLine();

            var byReason = dislikes.GroupBy(d => d.Reason ?? "(без тега)").OrderByDescending(g => g.Count());
            foreach (var group in byReason)
            {
                sb.AppendLine($"## {group.Key} — {group.Count()}");
                sb.AppendLine();

                int idx = 1;
                foreach (var d in group)
                {
                    sb.AppendLine($"### {idx}. «{Truncate(d.UserText, 80)}»");
                    sb.AppendLine($"- Вид: {d.ViewName ?? "—"} · модель {d.Model} · {d.Rounds} раунд(ов) · {d.TotalMs / 1000.0:F1} с");
                    if (d.ToolChain.Count > 0)
                        sb.AppendLine($"- Цепочка: {string.Join(" → ", d.ToolChain)}");
                    if (!string.IsNullOrEmpty(d.Comment))
                        sb.AppendLine($"- Комментарий: «{d.Comment}»");
                    sb.AppendLine($"- turnId: `{d.TurnId}` · outcome: {d.Outcome}");
                    sb.AppendLine();
                    idx++;
                }
            }

            return sb.ToString();
        }

        // ── Exported IDs persistence ─────────────────────────────────────────────

        private static HashSet<string> LoadExportedIds()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(PathManager.GetLogsDirectoryPath(), ExportedMarkerFile);
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            set.Add(line.Trim());
                    }
                }
            }
            catch { /* best-effort */ }
            return set;
        }

        private static void SaveExportedIds(HashSet<string> ids)
        {
            try
            {
                var path = Path.Combine(PathManager.GetLogsDirectoryPath(), ExportedMarkerFile);
                File.WriteAllLines(path, ids, Encoding.UTF8);
            }
            catch { /* best-effort */ }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }
    }
}
