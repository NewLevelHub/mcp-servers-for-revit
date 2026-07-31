using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Build delete confirmation text with category breakdown (REV-125).
    /// </summary>
    public static class DeleteConfirmSummary
    {
        public const int DefaultThreshold = 20;

        /// <summary>Extract element id strings from delete_element / operate_element Delete args.</summary>
        public static IReadOnlyList<string> ExtractIds(string toolName, string argsJson)
        {
            var ids = new List<string>();
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                var n = (toolName ?? "").Trim();
                JArray arr = null;
                if (n.Equals("delete_element", StringComparison.OrdinalIgnoreCase))
                    arr = args["elementIds"] as JArray;
                else if (n.Equals("operate_element", StringComparison.OrdinalIgnoreCase))
                    arr = args["data"]?["elementIds"] as JArray ?? args["elementIds"] as JArray;

                if (arr == null)
                    return ids;

                foreach (var token in arr)
                {
                    if (token == null || token.Type == JTokenType.Null)
                        continue;
                    var s = token.Type == JTokenType.String
                        ? token.Value<string>()
                        : token.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        ids.Add(s.Trim());
                }
            }
            catch
            {
                // ignore parse errors
            }

            return ids;
        }

        public static int CountTargets(string toolName, string argsJson) =>
            ExtractIds(toolName, argsJson).Count;

        /// <summary>
        /// Format confirmation text. <paramref name="resolveCategory"/> maps id → category display name;
        /// null/empty → «прочее».
        /// </summary>
        public static string Format(
            string toolName,
            string argsJson,
            Func<string, string> resolveCategory = null)
        {
            var ids = ExtractIds(toolName, argsJson);
            if (ids.Count == 0)
            {
                if ((toolName ?? "").Equals("send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                    return "Выполнить произвольный C# код в модели?";
                return "Удалить элементы?";
            }

            var byCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var cat = resolveCategory?.Invoke(id);
                if (string.IsNullOrWhiteSpace(cat))
                    cat = "прочее";
                if (!byCategory.ContainsKey(cat))
                    byCategory[cat] = 0;
                byCategory[cat]++;
            }

            return Format(ids.Count, byCategory);
        }

        public static string Format(int total, IDictionary<string, int> byCategory)
        {
            var sb = new StringBuilder();
            sb.Append("Удалить ").Append(total).Append(" элемент");
            sb.Append(PluralRu(total)).Append('?');

            if (byCategory == null || byCategory.Count == 0)
                return sb.ToString();

            var parts = byCategory
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key + ": " + kv.Value)
                .ToList();

            if (parts.Count > 0)
            {
                sb.AppendLine();
                sb.Append(string.Join(" · ", parts));
            }

            return sb.ToString();
        }

        private static string PluralRu(int n)
        {
            var mod100 = Math.Abs(n) % 100;
            var mod10 = mod100 % 10;
            if (mod100 >= 11 && mod100 <= 14)
                return "ов";
            if (mod10 == 1)
                return "";
            if (mod10 >= 2 && mod10 <= 4)
                return "а";
            return "ов";
        }
    }
}
