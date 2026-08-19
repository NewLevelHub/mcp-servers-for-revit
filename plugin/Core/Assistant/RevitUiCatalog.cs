using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Поиск по кнопкам ленты того Revit, который стоит у пользователя (REV-151).
    ///
    /// Список снимается плагином при запуске (REV-150) и лежит файлом. Ассистент отвечает
    /// «где кнопка» только отсюда: у пользователя своя версия, свой язык и свои аддоны,
    /// а выдуманный путь для новичка хуже честного «не нашёл» — он уходит искать
    /// несуществующую кнопку и перестаёт верить панели.
    /// </summary>
    public static class RevitUiCatalog
    {
        private static List<UiCommand> _commands;
        private static string _loadedPath;
        private static DateTime _loadedStamp;
        private static string _version;
        private static string _language;

        public static string CatalogDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mcp-servers-for-revit",
                "ui-catalog");

        /// <summary>Подменяется в тестах.</summary>
        public static string OverrideDirectory { get; set; }

        public sealed class UiCommand
        {
            public string Id;
            public string Text;
            public string Tab;
            public string Panel;
            public string KeyTip;
            public string TooltipTitle;
            public string TooltipContent;
            public bool IsButton;

            public string Where => $"{Tab} → {Panel}";
        }

        /// <summary>Точка входа инструмента ассистента.</summary>
        public static string ExecuteQueryTool(string argsJson)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson);
                var query = args["query"]?.ToString() ?? args["topic"]?.ToString() ?? "";
                var limit = args["limit"]?.Value<int?>() ?? 5;
                return Wrap(Query(query, limit));
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "local-revit-ui",
                    ["error"] = new JObject { ["message"] = ex.Message },
                }.ToString(Formatting.None);
            }
        }

        public static JObject Query(string query, int limit = 5)
        {
            var commands = Load();
            if (commands == null || commands.Count == 0)
            {
                return new JObject
                {
                    ["catalogMissing"] = true,
                    ["found"] = 0,
                    ["message"] =
                        "Список кнопок этого Revit ещё не снят. Скажи, что не можешь назвать точное место " +
                        "кнопки, и предложи перезапустить Revit — список снимается при запуске.",
                };
            }

            var terms = Tokenize(query);
            if (terms.Count == 0)
            {
                return new JObject
                {
                    ["found"] = 0,
                    ["revitVersion"] = _version,
                    ["language"] = _language,
                    ["message"] = "Пустой запрос — уточни, какую кнопку ищем.",
                };
            }

            var scored = new List<KeyValuePair<int, UiCommand>>();
            foreach (var command in commands)
            {
                var score = Score(command, terms);
                if (score > 0)
                    scored.Add(new KeyValuePair<int, UiCommand>(score, command));
            }

            if (limit < 1)
                limit = 1;
            if (limit > 20)
                limit = 20;

            var best = scored
                .OrderByDescending(p => p.Key)
                .ThenBy(p => p.Value.Text?.Length ?? int.MaxValue)
                .Take(limit)
                .ToList();

            var result = new JObject
            {
                ["found"] = best.Count,
                ["revitVersion"] = _version,
                ["language"] = _language,
            };

            if (best.Count == 0)
            {
                result["message"] =
                    "В ленте этого Revit такой кнопки не нашлось. Скажи об этом прямо и предложи " +
                    "описать задачу другими словами — не выдумывай вкладку и панель.";
                return result;
            }

            var arr = new JArray();
            foreach (var pair in best)
            {
                var c = pair.Value;
                var node = new JObject
                {
                    ["button"] = c.Text,
                    ["tab"] = c.Tab,
                    ["panel"] = c.Panel,
                    ["where"] = c.Where,
                };
                if (!string.IsNullOrWhiteSpace(c.KeyTip))
                    node["hotkey"] = c.KeyTip;
                if (!string.IsNullOrWhiteSpace(c.TooltipTitle) && c.TooltipTitle != c.Text)
                    node["title"] = c.TooltipTitle;
                if (!string.IsNullOrWhiteSpace(c.TooltipContent))
                    node["hint"] = Shorten(c.TooltipContent, 240);

                // REV-152: то, что говорит старший коллега через плечо, а не справка Revit.
                var explain = RevitUiHints.For(c.Id, c.Text);
                if (explain != null)
                {
                    if (!string.IsNullOrWhiteSpace(explain.Why))
                        node["why"] = explain.Why;
                    if (!string.IsNullOrWhiteSpace(explain.Before))
                        node["before"] = explain.Before;
                    if (!string.IsNullOrWhiteSpace(explain.Mistake))
                        node["commonMistake"] = explain.Mistake;
                }

                arr.Add(node);
            }

            result["commands"] = arr;
            return result;
        }

        /// <summary>Список кнопок; null, если каталог ещё не снят.</summary>
        public static IReadOnlyList<UiCommand> Load()
        {
            var path = NewestCatalogPath();
            if (path == null)
                return null;

            var stamp = File.GetLastWriteTimeUtc(path);
            if (_commands != null && path == _loadedPath && stamp == _loadedStamp)
                return _commands;

            try
            {
                var doc = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var list = new List<UiCommand>();
                foreach (var tab in doc["tabs"] as JArray ?? new JArray())
                {
                    var tabTitle = tab["title"]?.ToString();
                    foreach (var panel in tab["panels"] as JArray ?? new JArray())
                    {
                        var panelTitle = panel["title"]?.ToString();
                        Collect(panel["items"] as JArray, tabTitle, panelTitle, list, depth: 0);
                    }
                }

                _commands = list;
                _loadedPath = path;
                _loadedStamp = stamp;
                _version = doc["revitVersion"]?.ToString();
                _language = doc["language"]?.ToString();
                return _commands;
            }
            catch
            {
                // Битый файл каталога — лучше честное «не знаю», чем выдуманный путь.
                return null;
            }
        }

        private static void Collect(JArray items, string tab, string panel, List<UiCommand> into, int depth)
        {
            if (items == null || depth > 4)
                return;

            foreach (var item in items)
            {
                var text = item["text"]?.ToString() ?? item["automationName"]?.ToString();
                var type = item["type"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    into.Add(new UiCommand
                    {
                        Id = item["id"]?.ToString(),
                        Text = text,
                        Tab = tab,
                        Panel = panel,
                        KeyTip = item["keyTip"]?.ToString(),
                        TooltipTitle = item["tooltip"]?["title"]?.ToString(),
                        TooltipContent = item["tooltip"]?["content"]?.ToString(),
                        IsButton = type.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0,
                    });
                }

                Collect(item["items"] as JArray, tab, panel, into, depth + 1);
            }
        }

        private static string NewestCatalogPath()
        {
            var dir = string.IsNullOrWhiteSpace(OverrideDirectory) ? CatalogDirectory : OverrideDirectory;
            try
            {
                if (!Directory.Exists(dir))
                    return null;
                return Directory.GetFiles(dir, "ribbon-*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static int Score(UiCommand command, IReadOnlyList<string> terms)
        {
            var text = Normalize(command.Text);
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            var title = Normalize(command.TooltipTitle);
            var content = Normalize(command.TooltipContent);
            var panel = Normalize(command.Panel);
            var score = 0;

            foreach (var term in terms)
            {
                if (text == term)
                    score += 40;
                else if (text.StartsWith(term, StringComparison.Ordinal))
                    score += 24;
                else if (text.Contains(term))
                    score += 14;

                if (title.Contains(term))
                    score += 6;
                if (panel.Contains(term))
                    score += 3;
                if (content.Contains(term))
                    score += 2;
            }

            if (score > 0 && command.IsButton)
                score += 5;

            return score;
        }

        /// <summary>
        /// Синонимы новичка. Он говорит «комната», а кнопка называется «Помещение»;
        /// говорит «стенка» или «перегородка», а кнопка — «Стена».
        /// </summary>
        private static readonly Dictionary<string, string[]> Synonyms =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["комнат"] = new[] { "помещен" },
                ["перегородк"] = new[] { "стен" },
                ["стенк"] = new[] { "стен" },
                ["wall"] = new[] { "стен" },
                ["door"] = new[] { "двер" },
                ["window"] = new[] { "окн" },
                ["room"] = new[] { "помещен" },
                ["level"] = new[] { "уровен" },
                ["grid"] = new[] { "ос" },
                ["tag"] = new[] { "марк" },
                ["dimension"] = new[] { "размер" },
                ["sheet"] = new[] { "лист" },
                ["штамп"] = new[] { "основн", "надпис" },
                ["спецификац"] = new[] { "ведомост", "специф" },
                ["подрезк"] = new[] { "обрез", "подрез" },
            };

        private static readonly string[] StopWords =
        {
            "где", "как", "что", "это", "найти", "нажать", "кнопка", "кнопку", "кнопки",
            "сделать", "поставить", "создать", "мне", "надо", "нужно", "revit", "ревит",
            "вкладка", "вкладке", "панель", "панели", "и", "в", "на", "для", "с",
        };

        private static List<string> Tokenize(string query)
        {
            var normalized = Normalize(query);
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(normalized))
                return terms;

            foreach (var raw in normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw.Trim();
                if (word.Length < 3 || StopWords.Contains(word))
                    continue;

                // Окончания: человек пишет «стену», «стены», «стеной», а кнопка называется «Стена».
                // Держим и само слово, и обрубки — падежи русского иначе не сойдутся.
                AddTerm(terms, word);
                if (word.Length >= 4)
                    AddTerm(terms, word.Substring(0, word.Length - 1));
                if (word.Length >= 7)
                    AddTerm(terms, word.Substring(0, word.Length - 2));

                foreach (var kv in Synonyms)
                {
                    if (!word.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var syn in kv.Value)
                        AddTerm(terms, syn);
                }
            }

            return terms;
        }

        private static void AddTerm(List<string> terms, string term)
        {
            if (!string.IsNullOrWhiteSpace(term) && term.Length >= 3 && !terms.Contains(term))
                terms.Add(term);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            // «ё» и «е» новичок пишет как придётся.
            return sb.ToString().Replace('ё', 'е').Trim();
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= max)
                return text;
            return text.Substring(0, max).TrimEnd() + "…";
        }

        private static string Wrap(JObject payload) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "local-revit-ui",
            ["result"] = payload,
        }.ToString(Formatting.None);

        /// <summary>Сбрасывает кэш — для тестов.</summary>
        public static void ResetCache()
        {
            _commands = null;
            _loadedPath = null;
            _loadedStamp = default(DateTime);
        }
    }
}
