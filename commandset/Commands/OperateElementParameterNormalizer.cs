using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Commands
{
    /// <summary>
    /// LLM often sends categoryNames as a string ("Doors") instead of ["Doors","Windows"].
    /// </summary>
    internal static class OperateElementParameterNormalizer
    {
        public static void NormalizeCategoryNames(JObject data)
        {
            if (data == null)
                return;

            var cat = data["categoryNames"];
            if (cat == null || cat.Type == JTokenType.Null)
                return;

            if (cat.Type == JTokenType.String)
            {
                data["categoryNames"] = new JArray(SplitNames(cat.ToString()));
                return;
            }

            if (cat is not JArray arr)
                return;

            var flat = new List<string>();
            foreach (var item in arr)
            {
                if (item.Type != JTokenType.String)
                    continue;
                var s = item.ToString().Trim();
                if (s.Length == 0)
                    continue;
                if (s.Contains(",") || s.Contains(";"))
                    flat.AddRange(SplitNames(s));
                else
                    flat.Add(s);
            }

            if (flat.Count > 0)
                data["categoryNames"] = new JArray(flat);
        }

        private static IEnumerable<string> SplitNames(string raw)
        {
            return (raw ?? string.Empty)
                .Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0);
        }
    }
}
