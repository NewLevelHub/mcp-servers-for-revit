using System;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>Pure text for norm callouts (REV-130). No Revit / Socket deps.</summary>
    public static class NormAnnotationText
    {
        public static string Format(JToken f)
        {
            var name = (f["name"]?.ToString() ?? ("id " + f["elementId"])).Trim();
            var doc = f["source"]?["document"]?.ToString()?.Trim() ?? "";
            var clause = f["source"]?["clause"]?.ToString()?.Trim() ?? "";
            var sourceBit = JoinNonEmpty(doc, clause);

            var comparison = FormatComparison(f);
            var text = string.IsNullOrEmpty(comparison) ? name : name + ": " + comparison;
            if (!string.IsNullOrEmpty(sourceBit))
                text += " · " + sourceBit;
            return text;
        }

        public static bool HasUsefulContent(JToken f)
        {
            var text = Format(f);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return text.IndexOf("мм", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("м²", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf(" · ", StringComparison.Ordinal) >= 0
                || text.IndexOf(':') >= 0;
        }

        private static string FormatComparison(JToken f)
        {
            var actual = f["actualMm"];
            var required = f["requiredMm"];
            if (actual == null || required == null)
                return f["note"]?.ToString() ?? "";

            var a = JTokenParsing.GetDouble(actual);
            var r = JTokenParsing.GetDouble(required);
            if (!a.HasValue || !r.HasValue)
                return f["note"]?.ToString() ?? "";

            var op = a.Value < r.Value ? "<" : a.Value > r.Value ? ">" : "=";

            if ((f["checkType"]?.ToString() ?? "").Contains("room_area"))
                return a.Value + " " + op + " " + r.Value + " м²";

            return a.Value + " " + op + " " + r.Value + " мм";
        }

        private static string JoinNonEmpty(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a)) return b ?? "";
            if (string.IsNullOrWhiteSpace(b)) return a;
            return a.Trim() + " " + b.Trim();
        }
    }
}
