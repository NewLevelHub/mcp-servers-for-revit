using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Compact Revit session snapshot for the assistant preamble — no MCP call (REV-126).
    /// </summary>
    public sealed class AssistantSessionContext
    {
        public string DocumentTitle { get; set; } = "—";
        public string ViewName { get; set; } = "—";
        public string ViewType { get; set; } = "";
        public string LevelName { get; set; } = "—";
        public int? Scale { get; set; }
        public int SelectionCount { get; set; }
        public IList<string> SelectionCategories { get; set; } = new List<string>();
        public string Units { get; set; } = "мм";

        /// <summary>Full preamble block for the model.</summary>
        public string FormatForPrompt()
        {
            var sb = new StringBuilder();
            sb.Append("[КОНТЕКСТ] ");
            sb.Append(FormatPrimaryLine());
            sb.AppendLine();
            sb.Append(FormatSelectionLine());
            sb.Append(" · Единицы: ").Append(Units);
            return sb.ToString().TrimEnd();
        }

        /// <summary>Short line for the chat header.</summary>
        public string FormatForHeader()
        {
            var scale = Scale.HasValue && Scale.Value > 0 ? $", 1:{Scale.Value}" : "";
            var viewBit = string.IsNullOrEmpty(ViewType)
                ? ViewName
                : $"{ViewName} ({ViewType}{scale})";
            var sel = SelectionCount > 0
                ? $" · Выд.: {SelectionCount}"
                : "";
            return $"Документ: {DocumentTitle} · Вид: {viewBit} · Ур.: {LevelName}{sel}";
        }

        /// <summary>Legacy single-line format (ParseViewContext / logs).</summary>
        public string FormatLegacyLine()
        {
            var scale = Scale.HasValue && Scale.Value > 0 ? $", 1:{Scale.Value}" : "";
            var viewBit = string.IsNullOrEmpty(ViewType)
                ? ViewName
                : $"{ViewName} ({ViewType}{scale})";
            return $"Документ: {DocumentTitle} · Вид: {viewBit} · Уровень: {LevelName}";
        }

        private string FormatPrimaryLine()
        {
            var scale = Scale.HasValue && Scale.Value > 0 ? $", 1:{Scale.Value}" : "";
            var viewBit = string.IsNullOrEmpty(ViewType)
                ? $"«{ViewName}»"
                : $"«{ViewName}» ({ViewType}{scale})";
            return $"Документ: {DocumentTitle} · Вид: {viewBit} · Уровень: {LevelName}";
        }

        private string FormatSelectionLine()
        {
            if (SelectionCount <= 0)
                return "Выделено: нет";

            var cats = SelectionCategories != null && SelectionCategories.Count > 0
                ? " (" + string.Join(", ", SelectionCategories.Take(3)) + ")"
                : "";
            return $"Выделено: {SelectionCount} элемент(ов){cats}";
        }

        public static AssistantSessionContext Snapshot(UIApplication uiApp)
        {
            var ctx = new AssistantSessionContext();
            try
            {
                var uidoc = uiApp?.ActiveUIDocument;
                var doc = uidoc?.Document;
                var view = uidoc?.ActiveView;
                if (doc == null || view == null)
                    return ctx;

                ctx.DocumentTitle = string.IsNullOrWhiteSpace(doc.Title) ? "—" : doc.Title;
                ctx.ViewName = view.Name ?? "—";
                ctx.ViewType = view.ViewType.ToString();
                try
                {
                    if (view.Scale > 0)
                        ctx.Scale = view.Scale;
                }
                catch
                {
                    // some views throw on Scale
                }

                try
                {
                    if (view is ViewPlan plan && plan.GenLevel != null)
                        ctx.LevelName = plan.GenLevel.Name ?? "—";
                }
                catch
                {
                    // ignore
                }

                try
                {
                    var ids = uidoc.Selection.GetElementIds();
                    ctx.SelectionCount = ids?.Count ?? 0;
                    if (ids != null && ids.Count > 0)
                    {
                        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var id in ids)
                        {
                            try
                            {
                                var el = doc.GetElement(id);
                                var cat = el?.Category?.Name;
                                if (string.IsNullOrWhiteSpace(cat))
                                    cat = "Прочее";
                                if (!counts.ContainsKey(cat))
                                    counts[cat] = 0;
                                counts[cat]++;
                            }
                            catch
                            {
                                // skip
                            }
                        }

                        ctx.SelectionCategories = counts
                            .OrderByDescending(kv => kv.Value)
                            .Select(kv => kv.Key)
                            .Take(3)
                            .ToList();
                    }
                }
                catch
                {
                    ctx.SelectionCount = 0;
                }
            }
            catch
            {
                // best-effort
            }

            return ctx;
        }
    }
}
