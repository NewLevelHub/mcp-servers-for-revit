using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Commands
{
    /// <summary>
    /// Canonical operate_element payload fixes (MCP + in-Revit assistant).
    /// LLM often omits the <c>data</c> wrapper or sends categoryNames as a string.
    /// </summary>
    internal static class OperateElementParameterNormalizer
    {
        /// <summary>
        /// If <c>data</c> is missing, promote root fields into <c>data</c>.
        /// </summary>
        public static JObject EnsureDataObject(JObject parameters)
        {
            if (parameters == null)
                return null;

            if (parameters["data"] is JObject existing)
                return existing;

            var data = new JObject();
            foreach (var prop in parameters.Properties().ToList())
            {
                if (prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                    continue;
                data[prop.Name] = prop.Value?.DeepClone();
            }

            parameters["data"] = data;
            return data;
        }

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
