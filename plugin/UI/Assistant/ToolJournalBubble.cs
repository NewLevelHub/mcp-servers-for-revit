using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI.Assistant
{
    /// <summary>
    /// Live collapsible tool-step journal (REV-127). Updated via <see cref="Apply"/>.
    /// </summary>
    public sealed class ToolJournalBubble : Border
    {
        private readonly StackPanel _rowsPanel;
        private readonly TextBlock _headerCount;
        private readonly Expander _rootExpander;
        private readonly Dictionary<int, FrameworkElement> _rows = new Dictionary<int, FrameworkElement>();

        private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0xF7, 0xF6, 0xF3));
        private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0xE2, 0xDB));
        private static readonly Brush TitleFg = new SolidColorBrush(Color.FromRgb(0x1F, 0x1E, 0x1B));
        private static readonly Brush MutedFg = new SolidColorBrush(Color.FromRgb(0x73, 0x6E, 0x66));
        private static readonly Brush DoneFg = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0x45));
        private static readonly Brush FailFg = new SolidColorBrush(Color.FromRgb(0xB4, 0x2A, 0x2A));
        private static readonly Brush RunningFg = new SolidColorBrush(Color.FromRgb(0x2F, 0x5D, 0x8A));

        public event Action<int> ElementIdClicked;

        public ToolJournalBubble()
        {
            Margin = new Thickness(44, 4, 28, 10);
            HorizontalAlignment = HorizontalAlignment.Left;
            MaxWidth = 360;
            Background = CardBg;
            BorderBrush = CardBorder;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(16);
            Padding = new Thickness(12, 10, 12, 10);

            _headerCount = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedFg,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _rowsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

            _rootExpander = new Expander
            {
                IsExpanded = true,
                Header = _headerCount,
                Foreground = TitleFg,
                Content = _rowsPanel,
            };

            Child = _rootExpander;
            UpdateHeader(0);
        }

        public void Apply(ToolStepEvent ev)
        {
            if (ev?.AllSteps == null) return;
            var steps = ev.AllSteps;
            UpdateHeader(steps.Count);

            foreach (var step in steps)
            {
                if (step == null) continue;
                if (_rows.TryGetValue(step.Index, out var existing))
                {
                    UpdateRow(existing, step);
                }
                else
                {
                    var row = BuildRow(step);
                    _rows[step.Index] = row;
                    _rowsPanel.Children.Add(row);
                }
            }
        }

        private void UpdateHeader(int count)
        {
            _headerCount.Text = count <= 0 ? "Шаги" : $"Шаги · {count}";
        }

        private FrameworkElement BuildRow(ToolStepEvent step)
        {
            var expander = new Expander
            {
                Margin = new Thickness(0, 0, 0, 4),
                IsExpanded = false,
                Tag = step,
            };

            var header = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            expander.Header = header;

            var detail = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MutedFg,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 140,
            };
            expander.Content = detail;

            // Double-click row → select first element id found in result/summary/args.
            expander.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount < 2) return;
                var current = (s as Expander)?.Tag as ToolStepEvent;
                if (current == null) return;
                foreach (var id in ExtractElementIds(
                             (current.ResultJson ?? "") + " " + (current.Summary ?? "") + " " + (current.ArgsJson ?? "")))
                {
                    ElementIdClicked?.Invoke(id);
                    e.Handled = true;
                    break;
                }
            };

            UpdateRow(expander, step);
            return expander;
        }

        private void UpdateRow(FrameworkElement row, ToolStepEvent step)
        {
            if (!(row is Expander expander)) return;
            expander.Tag = step;
            if (!(expander.Header is TextBlock header)) return;

            header.Inlines.Clear();
            header.Inlines.Add(new Run(StatusMark(step.Status) + " ")
            {
                Foreground = StatusBrush(step.Status),
                FontWeight = FontWeights.SemiBold,
            });
            header.Inlines.Add(new Run(step.Name ?? "?")
            {
                Foreground = TitleFg,
                FontWeight = FontWeights.SemiBold,
            });

            var summary = string.IsNullOrWhiteSpace(step.Summary) ? "" : step.Summary.Trim();
            if (summary.Length > 80)
                summary = summary.Substring(0, 80) + "…";
            if (!string.IsNullOrEmpty(summary) && step.Status != ToolStepEvent.StatusRunning)
            {
                header.Inlines.Add(new Run(" → " + summary) { Foreground = MutedFg });
            }

            if (step.DurationMs > 0 && step.Status != ToolStepEvent.StatusRunning)
            {
                var sec = step.DurationMs / 1000.0;
                header.Inlines.Add(new Run("  " + sec.ToString("0.0", CultureInfo.InvariantCulture) + " с")
                {
                    Foreground = MutedFg,
                    FontSize = 11,
                });
            }

            // Clickable element ids in summary
            WireElementIdClicks(header, step.Summary);

            if (expander.Content is TextBox detail)
            {
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(step.ArgsJson))
                {
                    sb.AppendLine("args:");
                    sb.AppendLine(step.ArgsJson);
                }
                if (!string.IsNullOrWhiteSpace(step.ResultJson))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("result:");
                    sb.Append(step.ResultJson);
                }
                detail.Text = sb.Length > 0 ? sb.ToString() : "(нет деталей)";
            }
        }

        private void WireElementIdClicks(TextBlock header, string summary)
        {
            if (string.IsNullOrEmpty(summary) || ElementIdClicked == null)
                return;

            // Prefer dedicated hyperlinks only when summary is mostly ids — keep header simple.
            // Selection via expand detail is enough; optional: parse "id":123 patterns in ToolTip.
            header.ToolTip = summary;
        }

        private static string StatusMark(string status)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case ToolStepEvent.StatusOk: return "✓";
                case ToolStepEvent.StatusError: return "✗";
                case ToolStepEvent.StatusCancelled: return "⏹";
                default: return "↻";
            }
        }

        private static Brush StatusBrush(string status)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case ToolStepEvent.StatusOk: return DoneFg;
                case ToolStepEvent.StatusError: return FailFg;
                case ToolStepEvent.StatusCancelled: return MutedFg;
                default: return RunningFg;
            }
        }

        /// <summary>Parse integer ids from journal text for selection in Revit.</summary>
        public static IEnumerable<int> ExtractElementIds(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            foreach (Match m in Regex.Matches(text, @"\b(\d{4,9})\b"))
            {
                if (int.TryParse(m.Groups[1].Value, out var id) && id > 0)
                    yield return id;
            }
        }
    }
}
