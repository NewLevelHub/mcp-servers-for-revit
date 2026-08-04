using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace revit_mcp_plugin.UI.Assistant
{
    /// <summary>
    /// Lightweight markdown → FlowDocument for assistant bubbles (REV-127).
    /// Supports bold, lists, fenced code, pipe tables. No external NuGet (net48).
    /// </summary>
    public static class AssistantMarkdown
    {
        private static readonly Regex Bold = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex InlineCode = new Regex(@"`([^`]+)`", RegexOptions.Compiled);

        public static FrameworkElement CreateViewer(string markdown, bool fromUser)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return new TextBlock();

            try
            {
                var doc = BuildDocument(markdown, fromUser);
                var viewer = new FlowDocumentScrollViewer
                {
                    Document = doc,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    IsToolBarVisible = false,
                    MaxWidth = 256,
                    Focusable = true,
                };
                return viewer;
            }
            catch
            {
                return CreatePlainFallback(markdown, fromUser);
            }
        }

        public static FlowDocument BuildDocument(string markdown, bool fromUser)
        {
            var fg = fromUser
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33));

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = 12.5,
                Foreground = fg,
                TextAlignment = TextAlignment.Left,
            };

            var lines = NormalizeLines(markdown);
            var i = 0;
            while (i < lines.Count)
            {
                var line = lines[i];

                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    var code = new List<string>();
                    i++;
                    while (i < lines.Count && !lines[i].StartsWith("```", StringComparison.Ordinal))
                    {
                        code.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Count) i++; // closing fence
                    doc.Blocks.Add(MakeCodeBlock(string.Join("\n", code), fromUser));
                    continue;
                }

                if (IsTableSeparatorRow(line) == false && LooksLikeTableRow(line)
                    && i + 1 < lines.Count && IsTableSeparatorRow(lines[i + 1]))
                {
                    var tableLines = new List<string> { line };
                    i++;
                    // skip separator
                    i++;
                    while (i < lines.Count && LooksLikeTableRow(lines[i]))
                    {
                        tableLines.Add(lines[i]);
                        i++;
                    }
                    doc.Blocks.Add(MakeTable(tableLines, fromUser));
                    continue;
                }

                if (IsBullet(line, out var bulletText))
                {
                    var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 4) };
                    while (i < lines.Count && IsBullet(lines[i], out bulletText))
                    {
                        list.ListItems.Add(new ListItem(MakeParagraph(bulletText, fg)));
                        i++;
                    }
                    doc.Blocks.Add(list);
                    continue;
                }

                if (IsOrdered(line, out var orderedText))
                {
                    var list = new List { MarkerStyle = TextMarkerStyle.Decimal, Margin = new Thickness(0, 0, 0, 4) };
                    while (i < lines.Count && IsOrdered(lines[i], out orderedText))
                    {
                        list.ListItems.Add(new ListItem(MakeParagraph(orderedText, fg)));
                        i++;
                    }
                    doc.Blocks.Add(list);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    i++;
                    continue;
                }

                doc.Blocks.Add(MakeParagraph(line, fg));
                i++;
            }

            if (doc.Blocks.Count == 0)
                doc.Blocks.Add(MakeParagraph(markdown, fg));

            return doc;
        }

        private static FrameworkElement CreatePlainFallback(string text, bool fromUser)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = fromUser
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33)),
            };
        }

        private static List<string> NormalizeLines(string text)
        {
            return new List<string>((text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        }

        private static bool IsBullet(string line, out string text)
        {
            text = null;
            var t = line?.TrimStart() ?? "";
            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
            {
                text = t.Substring(2);
                return true;
            }
            return false;
        }

        private static bool IsOrdered(string line, out string text)
        {
            text = null;
            var t = line?.TrimStart() ?? "";
            var m = Regex.Match(t, @"^\d+\.\s+(.*)$");
            if (!m.Success) return false;
            text = m.Groups[1].Value;
            return true;
        }

        private static bool LooksLikeTableRow(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var t = line.Trim();
            return t.Contains("|") && t.Split('|').Length >= 3;
        }

        private static bool IsTableSeparatorRow(string line)
        {
            if (!LooksLikeTableRow(line)) return false;
            var cells = SplitTableCells(line);
            if (cells.Count == 0) return false;
            foreach (var c in cells)
            {
                var s = c.Trim();
                if (s.Length == 0) return false;
                foreach (var ch in s)
                {
                    if (ch != '-' && ch != ':' && ch != ' ')
                        return false;
                }
            }
            return true;
        }

        private static List<string> SplitTableCells(string line)
        {
            var t = (line ?? "").Trim();
            if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
            var parts = t.Split('|');
            var list = new List<string>();
            foreach (var p in parts)
                list.Add(p.Trim());
            return list;
        }

        private static Block MakeTable(List<string> rows, bool fromUser)
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 6) };
            if (rows.Count == 0) return table;

            var cols = SplitTableCells(rows[0]).Count;
            for (var c = 0; c < cols; c++)
                table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            for (var r = 0; r < rows.Count; r++)
            {
                var cells = SplitTableCells(rows[r]);
                var row = new TableRow();
                for (var c = 0; c < cols; c++)
                {
                    var cellText = c < cells.Count ? cells[c] : "";
                    var para = MakeParagraph(cellText, fromUser ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33)));
                    para.FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal;
                    para.FontSize = 11.5;
                    var cell = new TableCell(para)
                    {
                        BorderBrush = fromUser
                            ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
                            : new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(2, 2, 6, 2),
                    };
                    row.Cells.Add(cell);
                }
                group.Rows.Add(row);
            }

            return table;
        }

        private static Block MakeCodeBlock(string code, bool fromUser)
        {
            var para = new Paragraph
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Background = fromUser
                    ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF4)),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 2, 0, 4),
            };
            para.Inlines.Add(new Run(code ?? ""));
            return para;
        }

        private static Paragraph MakeParagraph(string text, Brush fg)
        {
            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 4), Foreground = fg };
            AppendInlines(para.Inlines, text ?? "", fg);
            return para;
        }

        private static void AppendInlines(InlineCollection inlines, string text, Brush fg)
        {
            // Split by inline code first, then bold inside plain segments.
            var pos = 0;
            foreach (Match m in InlineCode.Matches(text))
            {
                if (m.Index > pos)
                    AppendBoldSegments(inlines, text.Substring(pos, m.Index - pos), fg);
                inlines.Add(new Run(m.Groups[1].Value)
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
                });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length)
                AppendBoldSegments(inlines, text.Substring(pos), fg);
        }

        private static void AppendBoldSegments(InlineCollection inlines, string text, Brush fg)
        {
            var pos = 0;
            foreach (Match m in Bold.Matches(text))
            {
                if (m.Index > pos)
                    inlines.Add(new Run(text.Substring(pos, m.Index - pos)) { Foreground = fg });
                inlines.Add(new Run(m.Groups[1].Value)
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = fg,
                });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length)
                inlines.Add(new Run(text.Substring(pos)) { Foreground = fg });
        }
    }
}
