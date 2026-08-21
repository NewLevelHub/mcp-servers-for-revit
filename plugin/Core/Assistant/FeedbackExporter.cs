using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>Outcome of one export, so the panel can say what actually happened.</summary>
    public sealed class FeedbackExportResult
    {
        /// <summary>Full path of the .zip written under Logs/Feedback.</summary>
        public string PackagePath;
        public int Count;
        /// <summary>True when the package reached <see cref="DropDir"/>.</summary>
        public bool Delivered;
        /// <summary>Configured collection folder, or null when the architect works offline.</summary>
        public string DropDir;
        /// <summary>Why the copy failed; null on success or when no drop dir is set.</summary>
        public string DeliveryError;
    }

    /// <summary>
    /// Reads assistant session logs, collects dislike entries, packs them into a single
    /// .zip (report + machine-readable jsonl + screenshots) and mirrors it to the shared
    /// collection folder. Exported entries are marked so re-export never duplicates them.
    /// </summary>
    public static class FeedbackExporter
    {
        private const string ExportedMarkerFile = "feedback-exported-ids.txt";
        private const string PackagesFolderName = "Feedback";
        private const int ShotRetentionDays = 30;

        /// <summary>
        /// Panel load and a submitted dislike can both kick off a flush at once; without
        /// this they would read the same pending set and race on the exported-ids file.
        /// </summary>
        private static readonly object ExportGate = new object();

        /// <summary>Where finished .zip packages accumulate on the architect's machine.</summary>
        public static string GetPackagesDirectory()
        {
            var dir = Path.Combine(PathManager.GetLogsDirectoryPath(), PackagesFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }

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
        /// Packs all pending dislikes into a .zip under Logs/Feedback, copies it to the
        /// configured drop folder, and marks the entries exported. Returns null when
        /// there was nothing to send.
        /// </summary>
        public static FeedbackExportResult Export()
        {
            lock (ExportGate)
            {
                var exported = LoadExportedIds();
                var dislikes = CollectDislikes(exported);
                if (dislikes.Count == 0)
                {
                    // Still worth a sweep: an earlier package may have been written while the
                    // share was down.
                    MirrorPendingPackages();
                    return null;
                }

                var packagePath = WritePackage(dislikes);

                foreach (var d in dislikes)
                    exported.Add(d.TurnId);
                SaveExportedIds(exported);

                PurgeOldShots();

                var result = new FeedbackExportResult
                {
                    PackagePath = packagePath,
                    Count = dislikes.Count,
                    DropDir = ResolveDropDir(),
                };

                if (!string.IsNullOrEmpty(result.DropDir))
                {
                    try
                    {
                        CopyToDropDir(packagePath, result.DropDir);
                        result.Delivered = true;
                    }
                    catch (Exception ex)
                    {
                        result.DeliveryError = ex.Message;
                    }
                }

                MirrorPendingPackages();
                return result;
            }
        }

        /// <summary>
        /// Runs an export without touching the UI. Only fires when a drop folder is set:
        /// packaging silently in the local-only case would clear the badge and leave the
        /// architect with no sign that anything is waiting to be handed over.
        /// </summary>
        public static void TryAutoFlush()
        {
            try
            {
                if (string.IsNullOrEmpty(ResolveDropDir())) return;
                Export();
            }
            catch { /* delivery must never surface as a chat error */ }
        }

        /// <summary>Collection folder from settings, or null when unset / unreachable-by-config.</summary>
        public static string ResolveDropDir()
        {
            try
            {
                var dir = (PluginSettingsStore.LoadSettings().AssistantFeedbackDropDir ?? "").Trim();
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch { return null; }
        }

        // ── Package writing ──────────────────────────────────────────────────────

        private static string WritePackage(List<DislikeEntry> dislikes)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var name = $"feedback_{Sanitize(AuthorName())}_{Sanitize(Environment.MachineName)}_{stamp}.zip";
            var path = Path.Combine(GetPackagesDirectory(), name);

            // A second export inside the same minute would otherwise overwrite the first.
            path = MakeUnique(path);

            var shotsDir = Path.Combine(PathManager.GetLogsDirectoryPath(), FeedbackScreenshot.ShotsFolderName);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteTextEntry(zip, "report.md", BuildReport(dislikes));
                WriteTextEntry(zip, "raw.jsonl", BuildRawJsonl(dislikes));

                foreach (var d in dislikes)
                {
                    foreach (var shot in d.Shots)
                    {
                        var src = Path.Combine(shotsDir, shot);
                        if (!File.Exists(src)) continue;
                        try { zip.CreateEntryFromFileSafe(src, "shots/" + shot); }
                        catch { /* one unreadable shot must not lose the whole package */ }
                    }
                }
            }

            return path;
        }

        private static void WriteTextEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.Write(content);
            }
        }

        private static string MakeUnique(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 2; i < 100; i++)
            {
                var candidate = Path.Combine(dir, $"{stem}-{i}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
        }

        // ── Delivery ─────────────────────────────────────────────────────────────

        private static void CopyToDropDir(string packagePath, string dropDir)
        {
            Directory.CreateDirectory(dropDir);
            var target = Path.Combine(dropDir, Path.GetFileName(packagePath));
            File.Copy(packagePath, target, overwrite: true);
        }

        /// <summary>
        /// Copies any local package the drop folder does not have yet. This is what makes
        /// a complaint filed while the VPN was down still arrive the next morning —
        /// filenames carry machine and timestamp, so the copy is idempotent.
        /// </summary>
        private static void MirrorPendingPackages()
        {
            var dropDir = ResolveDropDir();
            if (string.IsNullOrEmpty(dropDir)) return;

            try
            {
                Directory.CreateDirectory(dropDir);
                foreach (var local in Directory.GetFiles(GetPackagesDirectory(), "feedback_*.zip"))
                {
                    var target = Path.Combine(dropDir, Path.GetFileName(local));
                    if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(local).Length)
                        continue;
                    try { File.Copy(local, target, overwrite: true); }
                    catch { /* try the next one; this one retries on the next flush */ }
                }
            }
            catch { /* share unreachable — packages stay local and retry later */ }
        }

        // ── Internal data ────────────────────────────────────────────────────────

        /// <summary>
        /// Shot file names of one patch. Complaints written before REV-152 carry a single
        /// "shot" string, and those logs are still sitting on the architects' machines
        /// waiting to be exported — read both shapes.
        /// </summary>
        private static List<string> ReadShotNames(JObject patch)
        {
            var names = new List<string>();

            if (patch["shots"] is JArray arr)
            {
                foreach (var item in arr)
                {
                    var name = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }

            var legacy = patch["shot"]?.ToString();
            if (!string.IsNullOrWhiteSpace(legacy) && !names.Contains(legacy))
                names.Add(legacy);

            return names;
        }

        private sealed class DislikeEntry
        {
            public string TurnId;
            public DateTime Ts;
            public string Model;
            public string DocTitle;
            public string ViewName;
            public string UserText;
            public string Reply;
            public string Reason;
            public string Comment;
            public List<string> Shots = new List<string>();
            public List<string> ToolChain = new List<string>();
            public int Rounds;
            public long TotalMs;
            public string Outcome;
            public string FailureDetail;
            public JObject RawTurn;
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
                foreach (var line in ReadLinesSafe(file))
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
                    Ts = ParseTimestamp(turn?["ts"]),
                    Model = turn?["model"]?.ToString() ?? "?",
                    DocTitle = turn?["docTitle"]?.ToString(),
                    ViewName = turn?["viewName"]?.ToString(),
                    UserText = turn?["userText"]?.ToString() ?? "?",
                    Reply = turn?["reply"]?.ToString(),
                    Reason = patch["reason"]?.ToString(),
                    Comment = patch["comment"]?.ToString(),
                    Shots = ReadShotNames(patch),
                    Rounds = turn?["rounds"]?.Value<int>() ?? 0,
                    TotalMs = turn?["totalMs"]?.Value<long>() ?? 0,
                    Outcome = turn?["outcome"]?.ToString(),
                    FailureDetail = turn?["failureDetail"]?.ToString(),
                    RawTurn = turn,
                };

                if (turn?["toolCalls"] is JArray tc)
                {
                    foreach (var t in tc)
                    {
                        var name = t["name"]?.ToString() ?? "?";
                        var summary = t["summary"]?.ToString();
                        var ms = t["durationMs"]?.Value<long>() ?? 0;
                        if (!string.IsNullOrWhiteSpace(summary))
                        {
                            var sec = ms > 0 ? $" ({ms / 1000.0:F1} с)" : "";
                            entry.ToolChain.Add($"{name} → {Truncate(summary, 60)}{sec}");
                        }
                        else
                        {
                            entry.ToolChain.Add(name);
                        }
                    }
                }

                result.Add(entry);
            }

            return result.OrderBy(d => d.Ts).ToList();
        }

        /// <summary>
        /// The turn log stores UTC ("…Z"). Newtonsoft hands that back as a Kind=Utc DateTime
        /// whose ToString() drops the marker, so re-parsing the printed form used to date a
        /// complaint filed at 22:14 as 17:14 — five hours off, in the one field the architect
        /// would check first.
        /// </summary>
        private static DateTime ParseTimestamp(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return DateTime.MinValue;

            if (token.Type == JTokenType.Date)
            {
                var value = token.Value<DateTime>();
                return value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
            }

            var raw = token.ToString();
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var offset))
                return offset.LocalDateTime;

            return DateTime.TryParse(raw, out var plain) ? plain : DateTime.MinValue;
        }

        /// <summary>The log file is open for append while Revit runs, so plain ReadAllLines throws.</summary>
        private static IEnumerable<string> ReadLinesSafe(string file)
        {
            var lines = new List<string>();
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        lines.Add(line);
                }
            }
            catch { /* unreadable file contributes nothing */ }
            return lines;
        }

        // ── Markdown report ──────────────────────────────────────────────────────

        private static string BuildReport(List<DislikeEntry> dislikes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Жалобы на AI-ассистент · {DateTime.Now:dd.MM.yyyy HH:mm}");
            sb.AppendLine();
            sb.AppendLine($"- Автор: **{AuthorName()}**");
            sb.AppendLine($"- Компьютер: `{Environment.MachineName}`");
            // Без версии непонятно, баг это или машина, до которой исправление ещё не доехало.
            sb.AppendLine($"- Версия сборки: `{BuildVersion.Current}`");
            sb.AppendLine($"- Всего дизлайков в пакете: **{dislikes.Count}**");
            var shots = dislikes.Sum(d => d.Shots.Count);
            if (shots > 0)
                sb.AppendLine($"- Скриншотов: **{shots}** (папка `shots/`)");
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
                    sb.AppendLine($"- Когда: {(d.Ts == DateTime.MinValue ? "—" : d.Ts.ToString("dd.MM.yyyy HH:mm"))}");
                    sb.AppendLine($"- Проект: {d.DocTitle ?? "—"} · вид: {d.ViewName ?? "—"}");
                    sb.AppendLine($"- Модель {d.Model} · {d.Rounds} раунд(ов) · {d.TotalMs / 1000.0:F1} с");
                    if (d.ToolChain.Count > 0)
                        sb.AppendLine($"- Цепочка: {string.Join(" → ", d.ToolChain)}");
                    if (!string.IsNullOrEmpty(d.Comment))
                        sb.AppendLine($"- Комментарий: «{d.Comment}»");
                    if (!string.IsNullOrEmpty(d.Reply))
                        sb.AppendLine($"- Ответ ассистента: «{Truncate(d.Reply, 300)}»");
                    if (!string.IsNullOrEmpty(d.FailureDetail))
                        sb.AppendLine($"- Техническая ошибка: `{Truncate(d.FailureDetail, 300)}`");
                    foreach (var shot in d.Shots)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"![скрин]({"shots/" + shot})");
                    }
                    sb.AppendLine($"- turnId: `{d.TurnId}` · outcome: {d.Outcome}");
                    sb.AppendLine();
                    idx++;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// The full turn next to its rating, one JSON object per line — this is the copy
        /// meant to be read by a tool, not by a person.
        /// </summary>
        private static string BuildRawJsonl(List<DislikeEntry> dislikes)
        {
            var sb = new StringBuilder();
            foreach (var d in dislikes)
            {
                var jo = new JObject
                {
                    ["turnId"] = d.TurnId,
                    ["author"] = AuthorName(),
                    ["machine"] = Environment.MachineName,
                    ["build"] = BuildVersion.Current,
                    ["reason"] = d.Reason,
                    ["comment"] = d.Comment,
                    ["shots"] = new JArray(d.Shots.Select(s => "shots/" + s).ToArray()),
                    ["turn"] = d.RawTurn,
                };
                sb.AppendLine(jo.ToString(Formatting.None));
            }
            return sb.ToString();
        }

        private static string AuthorName()
        {
            try
            {
                var configured = (PluginSettingsStore.LoadSettings().AssistantFeedbackAuthor ?? "").Trim();
                if (!string.IsNullOrEmpty(configured)) return configured;
            }
            catch { /* fall through */ }
            return Environment.UserName;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == ' ')
                    chars[i] = '-';
            }
            return new string(chars);
        }

        // ── Housekeeping ─────────────────────────────────────────────────────────

        /// <summary>
        /// Shots abandoned by a cancelled complaint have no patch pointing at them, and the
        /// session-log purge does not know about them.
        /// </summary>
        private static void PurgeOldShots()
        {
            try
            {
                var dir = Path.Combine(PathManager.GetLogsDirectoryPath(), FeedbackScreenshot.ShotsFolderName);
                if (!Directory.Exists(dir)) return;
                var cutoff = DateTime.Now.AddDays(-ShotRetentionDays);
                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                        File.Delete(f);
                }
            }
            catch { /* best-effort */ }
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

    internal static class ZipArchiveExtensions
    {
        /// <summary>
        /// CreateEntryFromFile lives in System.IO.Compression.FileSystem, which this addin
        /// does not carry; opening the file with FileShare.Read also lets a shot be zipped
        /// while something else still holds it.
        /// </summary>
        internal static void CreateEntryFromFileSafe(this ZipArchive zip, string sourcePath, string entryName)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = entry.Open())
            {
                src.CopyTo(dst);
            }
        }
    }
}
