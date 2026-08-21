using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Structured log entry for a single assistant turn (user request → agent response).
    /// Written as one-line JSON to <c>Logs/assistant-sessions_{yyyyMMdd}.jsonl</c>.
    /// </summary>
    public sealed class TurnLogEntry
    {
        public string TurnId;
        public string SessionId;
        public DateTime Ts;
        public string Model;
        public string DocTitle;
        public string ViewName;
        public string LevelName;
        public string UserText;
        public string PresetId;
        public List<string> ToolProfiles = new List<string>();
        public List<string> ProfileEscalations = new List<string>();
        /// <summary>REV-124: reasons for fast→smart switches within the turn.</summary>
        public List<string> ModelEscalations = new List<string>();
        public List<AttachmentMeta> Attachments = new List<AttachmentMeta>();
        public int Rounds;
        public List<ToolCallLog> ToolCalls = new List<ToolCallLog>();
        public string Reply;
        public List<string> DoneSummary = new List<string>();
        public string Outcome; // ok | failed | cancelled | maxRounds
        /// <summary>
        /// Exception chain when Outcome is "failed". Reply carries what the architect
        /// was shown; this keeps the technical detail that used to be shown instead.
        /// </summary>
        public string FailureDetail;
        public long TotalMs;
        public int PromptTokens;
        public int CompletionTokens;
    }

    public sealed class AttachmentMeta
    {
        public string Kind;
        public string Name;
        public long Bytes;
    }

    public sealed class ToolCallLog
    {
        public int Round;
        public string Name;
        public string Args;
        public string NormalizedArgs;
        public bool Ok;
        public long DurationMs;
        public string Error;
        public int ResultBytes;
        /// <summary>REV-127: short result summary for UI journal / dislike export.</summary>
        public string Summary;
        /// <summary>True when create_*_element was blocked because typeId was omitted (REV-121 metric).</summary>
        public bool MissingTypeId;
        /// <summary>Legacy: silent typeId injection removed in REV-121; always false.</summary>
        public bool InjectedTypeId;
    }

    /// <summary>
    /// Writes turn log entries and rating patches to a daily JSONL file.
    /// All methods are safe to call from any thread — errors are swallowed.
    /// </summary>
    public static class AssistantTurnLogger
    {
        private static readonly object _lock = new object();
        private const int RetentionDays = 30;

        public static void Write(TurnLogEntry entry)
        {
            try
            {
                var jo = new JObject
                {
                    ["turnId"] = entry.TurnId,
                    ["sessionId"] = entry.SessionId,
                    ["ts"] = entry.Ts.ToString("O"),
                    ["model"] = entry.Model,
                    ["docTitle"] = entry.DocTitle,
                    ["viewName"] = entry.ViewName,
                    ["levelName"] = entry.LevelName,
                    ["userText"] = entry.UserText,
                    ["presetId"] = entry.PresetId,
                    ["rounds"] = entry.Rounds,
                    ["reply"] = Truncate(entry.Reply, 2000),
                    ["outcome"] = entry.Outcome,
                    // Read back by FeedbackExporter: a complaint about a failed turn is
                    // useless without the exception the architect was never shown.
                    ["failureDetail"] = Truncate(entry.FailureDetail, 1000),
                    ["totalMs"] = entry.TotalMs,
                    ["promptTokens"] = entry.PromptTokens,
                    ["completionTokens"] = entry.CompletionTokens,
                };

                if (entry.ToolProfiles != null && entry.ToolProfiles.Count > 0)
                    jo["toolProfiles"] = new JArray(entry.ToolProfiles.ToArray());
                if (entry.ProfileEscalations != null && entry.ProfileEscalations.Count > 0)
                    jo["profileEscalations"] = new JArray(entry.ProfileEscalations.ToArray());
                if (entry.ModelEscalations != null && entry.ModelEscalations.Count > 0)
                    jo["modelEscalations"] = new JArray(entry.ModelEscalations.ToArray());

                if (entry.Attachments != null && entry.Attachments.Count > 0)
                {
                    var arr = new JArray();
                    foreach (var a in entry.Attachments)
                        arr.Add(new JObject { ["kind"] = a.Kind, ["name"] = a.Name, ["bytes"] = a.Bytes });
                    jo["attachments"] = arr;
                }

                if (entry.ToolCalls != null && entry.ToolCalls.Count > 0)
                {
                    var arr = new JArray();
                    foreach (var tc in entry.ToolCalls)
                    {
                        arr.Add(new JObject
                        {
                            ["round"] = tc.Round,
                            ["name"] = tc.Name,
                            ["args"] = Truncate(tc.Args, 1000),
                            ["normalizedArgs"] = Truncate(tc.NormalizedArgs, 1000),
                            ["ok"] = tc.Ok,
                            ["durationMs"] = tc.DurationMs,
                            ["error"] = tc.Error,
                            ["resultBytes"] = tc.ResultBytes,
                            // A failed call's summary IS the diagnosis, and 400 chars
                            // cut it exactly where it started being useful: the
                            // dislike packages of 20.08.2026 all end mid-way through
                            // "Invalid arguments for tool …", one character before the
                            // line that names the argument the model should have used.
                            // A successful call's summary is a payload nobody reads in
                            // full, so only the failures get the longer allowance.
                            ["summary"] = Truncate(tc.Summary, tc.Ok ? 400 : 2000),
                            ["missingTypeId"] = tc.MissingTypeId,
                            ["injectedTypeId"] = tc.InjectedTypeId,
                        });
                    }
                    jo["toolCalls"] = arr;
                }

                if (entry.DoneSummary != null && entry.DoneSummary.Count > 0)
                    jo["doneSummary"] = new JArray(entry.DoneSummary.ToArray());

                AppendLine(jo.ToString(Formatting.None));
                PurgeOldFiles();
            }
            catch { /* logging must never break the UI */ }
        }

        /// <summary>
        /// Append a rating patch to the same JSONL file. Links to the original entry by turnId.
        /// Returns false when the write failed, so the UI can tell the architect their feedback
        /// was not actually saved instead of silently swallowing it.
        /// </summary>
        public static bool WriteRatingPatch(string turnId, int rating, string reason, string comment, IReadOnlyList<string> shotPaths = null)
        {
            try
            {
                var patch = new JObject
                {
                    ["rating"] = rating,
                    ["reason"] = reason,
                    ["comment"] = comment,
                    ["ts"] = DateTime.UtcNow.ToString("O"),
                };

                // File names only: the shots travel inside the export package, where the
                // architect's absolute path under %AppData% means nothing.
                if (shotPaths != null)
                {
                    var names = new JArray();
                    foreach (var p in shotPaths)
                    {
                        if (!string.IsNullOrWhiteSpace(p))
                            names.Add(Path.GetFileName(p));
                    }
                    if (names.Count > 0)
                        patch["shots"] = names;
                }

                var jo = new JObject
                {
                    ["turnId"] = turnId,
                    ["ratingPatch"] = patch,
                };
                AppendLine(jo.ToString(Formatting.None));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[AssistantTurnLogger] WriteRatingPatch failed: " + ex);
                return false;
            }
        }

        private static void AppendLine(string line)
        {
            var path = GetLogFilePath();
            lock (_lock)
            {
                File.AppendAllText(path, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }

        private static string GetLogFilePath()
        {
            var dir = PathManager.GetLogsDirectoryPath();
            return Path.Combine(dir, $"assistant-sessions_{DateTime.Now:yyyyMMdd}.jsonl");
        }

        private static void PurgeOldFiles()
        {
            try
            {
                var dir = PathManager.GetLogsDirectoryPath();
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (var f in Directory.GetFiles(dir, "assistant-sessions_*.jsonl"))
                {
                    if (File.GetCreationTime(f) < cutoff)
                        File.Delete(f);
                }
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
