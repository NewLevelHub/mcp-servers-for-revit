using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Offline norm catalog (exported from server/revit-data.db) for the in-Revit agent.
    /// </summary>
    public static class NormCatalogStore
    {
        private static readonly object Gate = new object();
        private static NormCatalogFile _cached;
        private static string _loadedPath;
        private static bool _loadAttempted;

        public static bool IsAvailable
        {
            get
            {
                var cat = GetCatalog();
                return cat != null && cat.RuleCount > 0 && cat.Rules != null && cat.Rules.Count > 0;
            }
        }

        public static string LoadedPath
        {
            get
            {
                GetCatalog();
                return _loadedPath;
            }
        }

        public static NormCatalogFile GetCatalog()
        {
            lock (Gate)
            {
                if (_loadAttempted)
                    return _cached;
                _loadAttempted = true;
                _cached = TryLoad();
                return _cached;
            }
        }

        /// <summary>Force reload (e.g. after IT drops a new catalog next to the DLL).</summary>
        public static void Reload()
        {
            lock (Gate)
            {
                _loadAttempted = false;
                _cached = null;
                _loadedPath = null;
            }
        }

        public static JObject GetResolved(string checkToolName)
        {
            var cat = GetCatalog();
            if (cat?.Resolved == null || string.IsNullOrWhiteSpace(checkToolName))
                return null;
            var key = checkToolName.Trim();
            var token = cat.Resolved[key];
            if (token == null || token.Type == JTokenType.Null)
                return null;
            return token as JObject ?? JObject.FromObject(token);
        }

        public static JObject Query(string topic, int limit = 5)
        {
            var cat = GetCatalog();
            if (cat == null || cat.Rules == null || cat.Rules.Count == 0)
            {
                return new JObject
                {
                    ["ok"] = true,
                    ["catalogMissing"] = true,
                    ["message"] =
                        "Каталог PDF не загружен — поиск по теме недоступен. " +
                        "Проверка этажа run_norm_audit всё равно работает (встроенные нормы СП РК).",
                    ["rules"] = new JArray()
                };
            }

            limit = Math.Max(1, Math.Min(limit <= 0 ? 5 : limit, 20));
            var terms = TopicToTerms(topic ?? "");
            var scored = new List<(NormCatalogRule rule, int score)>();
            foreach (var rule in cat.Rules)
            {
                var score = ScoreRule(rule, topic ?? "", terms);
                if (score > 0)
                    scored.Add((rule, score));
            }

            scored.Sort((a, b) =>
            {
                var c = b.score.CompareTo(a.score);
                if (c != 0) return c;
                return string.Compare(a.rule.Source?.Document, b.rule.Source?.Document, StringComparison.OrdinalIgnoreCase);
            });

            var take = scored.Take(limit).Select(x => ToCompact(x.rule)).ToList();
            return new JObject
            {
                ["ok"] = true,
                ["catalogMissing"] = false,
                ["ruleCount"] = cat.RuleCount,
                ["documentCount"] = cat.DocumentCount,
                ["topic"] = topic ?? "",
                ["count"] = take.Count,
                ["rules"] = new JArray(take)
            };
        }

        public static string ExecuteQueryTool(string argsJson)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(argsJson)
                    ? new JObject()
                    : JObject.Parse(argsJson);
                var topic = args["topic"]?.ToString() ?? "";
                var limit = args["limit"]?.Value<int?>() ?? 5;
                var payload = Query(topic, limit);
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "local-norm",
                    ["result"] = payload
                }.ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "local-norm",
                    ["error"] = new JObject { ["message"] = ex.Message }
                }.ToString(Formatting.None);
            }
        }

        private static NormCatalogFile TryLoad()
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        continue;
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var cat = JsonConvert.DeserializeObject<NormCatalogFile>(json);
                    if (cat == null)
                        continue;
                    _loadedPath = path;
                    return cat;
                }
                catch
                {
                    /* try next path */
                }
            }

            _loadedPath = null;
            return null;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            string asmDir = null;
            try
            {
                asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                /* ignore */
            }

            if (!string.IsNullOrEmpty(asmDir))
            {
                yield return Path.Combine(asmDir, "Resources", "norm-catalog.json");
                yield return Path.Combine(asmDir, "norm-catalog.json");
            }

            // Dev fallback: repo plugin/Resources next to solution layout
            if (!string.IsNullOrEmpty(asmDir))
            {
                var dir = new DirectoryInfo(asmDir);
                for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "Resources", "norm-catalog.json");
                    yield return candidate;
                    candidate = Path.Combine(dir.FullName, "plugin", "Resources", "norm-catalog.json");
                    yield return candidate;
                }
            }
        }

        private static JObject ToCompact(NormCatalogRule rule)
        {
            var source = rule.Source ?? new NormCatalogSource();
            var quote = source.Quote ?? "";
            if (quote.Length > 480)
                quote = quote.Substring(0, 479) + "…";
            return new JObject
            {
                ["id"] = rule.Id,
                ["type"] = rule.Type,
                ["object"] = rule.Object,
                ["value"] = rule.Value != null ? JToken.FromObject(rule.Value) : null,
                ["unit"] = rule.Unit,
                ["normalized"] = rule.Normalized != null ? JToken.FromObject(rule.Normalized) : null,
                ["source"] = new JObject
                {
                    ["document"] = source.Document ?? "",
                    ["clause"] = source.Clause ?? "",
                    ["quote"] = quote,
                    ["page"] = source.Page
                }
            };
        }

        private static List<string> TopicToTerms(string topic)
        {
            var norm = (topic ?? "").ToLowerInvariant();
            var words = System.Text.RegularExpressions.Regex
                .Split(norm, @"[^a-zа-яё0-9.\-]+")
                .Where(w => !string.IsNullOrEmpty(w))
                .Select(w =>
                {
                    if (w.Length > 5) return w.Substring(0, w.Length - 2);
                    if (w.Length > 3) return w.Substring(0, w.Length - 1);
                    return w;
                })
                .ToList();
            return words.Distinct().ToList();
        }

        private static int ScoreRule(NormCatalogRule rule, string topic, List<string> terms)
        {
            if (terms == null || terms.Count == 0)
                return 0;

            var blob = string.Join(" ",
                rule.Object ?? "",
                rule.Source?.Document ?? "",
                rule.Source?.Clause ?? "",
                rule.Source?.Quote ?? "",
                rule.Tags != null ? string.Join(" ", rule.Tags) : "").ToLowerInvariant();

            var score = 0;
            foreach (var term in terms)
            {
                if (term.Length == 0) continue;
                if (blob.IndexOf(term, StringComparison.Ordinal) >= 0)
                    score += term.Length >= 4 ? 3 : 1;
            }

            if (rule.Normalized != null &&
                (rule.Normalized.Min != null || rule.Normalized.Max != null || rule.Normalized.Exact != null))
                score += 2;

            if (!string.IsNullOrEmpty(topic) &&
                blob.IndexOf(topic.Trim().ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                score += 5;

            return score;
        }
    }

    public sealed class NormCatalogFile
    {
        [JsonProperty("exportedAt")]
        public string ExportedAt { get; set; }

        [JsonProperty("ruleCount")]
        public int RuleCount { get; set; }

        [JsonProperty("documentCount")]
        public int DocumentCount { get; set; }

        [JsonProperty("resolved")]
        public JObject Resolved { get; set; }

        [JsonProperty("rules")]
        public List<NormCatalogRule> Rules { get; set; }
    }

    public sealed class NormCatalogRule
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("object")]
        public string Object { get; set; }

        [JsonProperty("value")]
        public object Value { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("normalized")]
        public NormCatalogNormalized Normalized { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; }

        [JsonProperty("source")]
        public NormCatalogSource Source { get; set; }
    }

    public sealed class NormCatalogNormalized
    {
        [JsonProperty("min")]
        public double? Min { get; set; }

        [JsonProperty("max")]
        public double? Max { get; set; }

        [JsonProperty("exact")]
        public double? Exact { get; set; }
    }

    public sealed class NormCatalogSource
    {
        [JsonProperty("document")]
        public string Document { get; set; }

        [JsonProperty("clause")]
        public string Clause { get; set; }

        [JsonProperty("quote")]
        public string Quote { get; set; }

        [JsonProperty("page")]
        public int? Page { get; set; }
    }
}
