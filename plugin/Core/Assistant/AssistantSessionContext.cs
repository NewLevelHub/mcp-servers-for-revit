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

        /// <summary>
        /// Revit's ViewType enum is English API vocabulary ("FloorPlan"); the architect
        /// reading the header should see the same words as the Revit browser.
        /// </summary>
        private static readonly Dictionary<string, string> ViewTypeNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["FloorPlan"] = "План этажа",
                ["CeilingPlan"] = "План потолка",
                ["EngineeringPlan"] = "Инженерный план",
                ["AreaPlan"] = "План зонирования",
                ["Section"] = "Разрез",
                ["Elevation"] = "Фасад",
                ["Detail"] = "Узел",
                ["DraftingView"] = "Чертёжный вид",
                ["ThreeD"] = "3D-вид",
                ["Schedule"] = "Спецификация",
                ["ColumnSchedule"] = "Спецификация колонн",
                ["PanelSchedule"] = "Спецификация щитов",
                ["DrawingSheet"] = "Лист",
                ["Legend"] = "Легенда",
                ["Rendering"] = "Визуализация",
                ["Walkthrough"] = "Обход",
                ["Report"] = "Отчёт",
                ["CostReport"] = "Отчёт по стоимости",
                ["LoadsReport"] = "Отчёт по нагрузкам",
                ["Undefined"] = "",
                ["Internal"] = "",
            };

        /// <summary>Russian caption for <see cref="ViewType"/>, or the raw value when unmapped.</summary>
        private string ViewTypeCaption
        {
            get
            {
                if (string.IsNullOrEmpty(ViewType))
                    return "";
                return ViewTypeNames.TryGetValue(ViewType, out var ru) ? ru : ViewType;
            }
        }

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
            var type = ViewTypeCaption;
            var viewBit = string.IsNullOrEmpty(type)
                ? ViewName
                : $"{ViewName} ({type}{scale})";
            var sel = SelectionCount > 0
                ? $" · Выделено: {SelectionCount}{FormatSelectionCategories()}"
                : "";
            return $"Документ: {DocumentTitle} · Вид: {viewBit} · Уровень: {LevelName}{sel}";
        }

        /// <summary>" (Стены, Двери)" for the header, or "" when categories are unknown.</summary>
        private string FormatSelectionCategories()
        {
            if (SelectionCategories == null || SelectionCategories.Count == 0)
                return "";
            return " (" + string.Join(", ", SelectionCategories.Take(2)) + ")";
        }

        /// <summary>Legacy single-line format (ParseViewContext / logs).</summary>
        public string FormatLegacyLine()
        {
            var scale = Scale.HasValue && Scale.Value > 0 ? $", 1:{Scale.Value}" : "";
            var type = ViewTypeCaption;
            var viewBit = string.IsNullOrEmpty(type)
                ? ViewName
                : $"{ViewName} ({type}{scale})";
            return $"Документ: {DocumentTitle} · Вид: {viewBit} · Уровень: {LevelName}";
        }

        private string FormatPrimaryLine()
        {
            var scale = Scale.HasValue && Scale.Value > 0 ? $", 1:{Scale.Value}" : "";
            var type = ViewTypeCaption;
            var viewBit = string.IsNullOrEmpty(type)
                ? $"«{ViewName}»"
                : $"«{ViewName}» ({type}{scale})";
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
