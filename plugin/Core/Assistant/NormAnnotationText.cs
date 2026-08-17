using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>Pure text for norm callouts (REV-130). No Revit / Socket deps.</summary>
    public static class NormAnnotationText
    {
        public static string Format(JToken f) => Compose(NameOf(f), f);

        /// <summary>Element name as printed at the head of a callout.</summary>
        public static string NameOf(JToken f)
        {
            return (f["name"]?.ToString() ?? ("id " + f["elementId"])).Trim();
        }

        /// <summary>
        /// Callout without the leading element name — a continuation line inside a
        /// note whose first line already names the element.
        /// </summary>
        public static string FormatWithoutName(JToken f) => Compose(null, f);

        private static string Compose(string name, JToken f)
        {
            var doc = f["source"]?["document"]?.ToString()?.Trim() ?? "";
            var clause = f["source"]?["clause"]?.ToString()?.Trim() ?? "";
            var sourceBit = JoinNonEmpty(doc, clause);

            var comparison = FormatComparison(f);
            string text;
            if (string.IsNullOrEmpty(name))
                text = comparison ?? "";
            else
                text = string.IsNullOrEmpty(comparison) ? name : name + ": " + comparison;

            if (!string.IsNullOrEmpty(sourceBit))
                text = string.IsNullOrEmpty(text) ? sourceBit : text + " · " + sourceBit;
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
                return FallbackDetail(f);

            var a = JTokenParsing.GetDouble(actual);
            var r = JTokenParsing.GetDouble(required);
            if (!a.HasValue || !r.HasValue)
                return FallbackDetail(f);

            // Compare with raw values; display mm rounded to whole units (REV-135 float noise).
            var op = a.Value < r.Value ? "<" : a.Value > r.Value ? ">" : "=";

            if ((f["checkType"]?.ToString() ?? "").Contains("room_area"))
            {
                var aM2 = Math.Round(a.Value, 2, MidpointRounding.AwayFromZero);
                var rM2 = Math.Round(r.Value, 2, MidpointRounding.AwayFromZero);
                return aM2 + " " + op + " " + rM2 + " м²";
            }

            var aMm = (long)Math.Round(a.Value, MidpointRounding.AwayFromZero);
            var rMm = (long)Math.Round(r.Value, MidpointRounding.AwayFromZero);
            var values = aMm + " " + op + " " + rMm + " мм";

            // Naming the metric matters once findings are stacked per element:
            // «250 < 300 мм» alone does not say it is про проступь. Matches the
            // server-side template in formatFindingAnnotation.ts.
            var metric = f["metric"]?.ToString()?.Trim() ?? "";
            return string.IsNullOrEmpty(metric) ? values : metric + " " + values;
        }

        private static string FallbackDetail(JToken f)
        {
            var note = f["note"]?.ToString();
            if (!string.IsNullOrWhiteSpace(note))
                return note.Trim();
            return f["metric"]?.ToString()?.Trim() ?? "";
        }

        /// <summary>Callout lines for one element, in the order findings arrived.</summary>
        public sealed class ElementNotes
        {
            public long ElementId;
            public string Name = "";
            public readonly List<string> Lines = new List<string>();
            /// <summary>Named form of each line already added — the dedup key.</summary>
            public readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// One note per element, not per finding. A stair failing both march width
        /// and tread used to produce two notes whose leaders ran to the same point,
        /// drawing two lines on top of each other across the plan.
        /// Mirrors findingsToAnnotationNotes in formatFindingAnnotation.ts.
        /// </summary>
        public static List<ElementNotes> GroupByElement(JArray findings)
        {
            var ordered = new List<ElementNotes>();
            var byElement = new Dictionary<long, ElementNotes>();
            if (findings == null)
                return ordered;

            foreach (JToken f in findings)
            {
                var status = f["status"]?.ToString() ?? "";
                if (!status.Equals("violation", StringComparison.OrdinalIgnoreCase)
                    && !status.Equals("nearLimit", StringComparison.OrdinalIgnoreCase))
                    continue;

                var elementId = JTokenParsing.GetLong(f["elementId"])
                    ?? JTokenParsing.GetLong(f["ElementId"])
                    ?? JTokenParsing.GetLong(f["id"])
                    ?? 0;
                if (elementId <= 0)
                    continue;

                var name = NameOf(f);
                // Dedup on the named form: line 2+ drops the name, so comparing
                // rendered lines would never match line 1.
                var named = Format(f);

                ElementNotes entry;
                if (!byElement.TryGetValue(elementId, out entry))
                {
                    entry = new ElementNotes { ElementId = elementId, Name = name };
                    entry.Lines.Add(named);
                    entry.Seen.Add(named);
                    byElement[elementId] = entry;
                    ordered.Add(entry);
                    continue;
                }

                if (!entry.Seen.Add(named))
                    continue;

                // Repeat the name only when this finding calls the element something else.
                entry.Lines.Add(string.Equals(name, entry.Name, StringComparison.Ordinal)
                    ? FormatWithoutName(f)
                    : named);
            }

            return ordered;
        }

        private static string JoinNonEmpty(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a)) return b ?? "";
            if (string.IsNullOrWhiteSpace(b)) return a;
            return a.Trim() + " " + b.Trim();
        }
    }
}
