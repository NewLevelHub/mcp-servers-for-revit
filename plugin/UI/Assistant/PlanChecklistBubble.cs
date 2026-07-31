using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI.Assistant
{
    /// <summary>
    /// Live checklist bubble for <c>declare_plan</c> (REV-120).
    /// Shown before model changes; steps flip to ✓ / ✗ as tools complete.
    /// </summary>
    public sealed class PlanChecklistBubble : Border
    {
        private readonly TextBlock _goalText;
        private readonly StackPanel _stepsPanel;
        private readonly Dictionary<int, TextBlock> _stepMarks = new Dictionary<int, TextBlock>();
        private readonly Dictionary<int, TextBlock> _stepLabels = new Dictionary<int, TextBlock>();

        private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0xD0, 0xDA, 0xE6));
        private static readonly Brush TitleFg = new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44));
        private static readonly Brush MutedFg = new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x7D));
        private static readonly Brush DoneFg = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x4C));
        private static readonly Brush FailFg = new SolidColorBrush(Color.FromRgb(0xB4, 0x2A, 0x2A));
        private static readonly Brush PendingFg = new SolidColorBrush(Color.FromRgb(0x8A, 0x96, 0xA3));

        public PlanChecklistBubble(AgentPlanSnapshot plan)
        {
            Margin = new Thickness(36, 0, 36, 10);
            HorizontalAlignment = HorizontalAlignment.Left;
            MaxWidth = 320;
            Background = CardBg;
            BorderBrush = CardBorder;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(12);
            Padding = new Thickness(12, 10, 12, 10);

            var root = new StackPanel();

            var header = new TextBlock
            {
                Text = "План",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedFg,
                Margin = new Thickness(0, 0, 0, 2),
            };
            root.Children.Add(header);

            _goalText = new TextBlock
            {
                Text = plan?.Goal ?? "",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TitleFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            root.Children.Add(_goalText);

            _stepsPanel = new StackPanel();
            root.Children.Add(_stepsPanel);

            Child = root;
            Apply(plan);
        }

        public void Apply(AgentPlanSnapshot plan)
        {
            if (plan == null) return;
            _goalText.Text = plan.Goal ?? "";

            if (_stepMarks.Count == 0)
            {
                BuildSteps(plan.Steps);
                return;
            }

            foreach (var step in plan.Steps ?? Array.Empty<AgentPlanStep>())
            {
                if (!_stepMarks.TryGetValue(step.N, out var mark))
                    continue;
                ApplyStatus(mark, _stepLabels[step.N], step.Status);
            }
        }

        private void BuildSteps(IReadOnlyList<AgentPlanStep> steps)
        {
            _stepsPanel.Children.Clear();
            _stepMarks.Clear();
            _stepLabels.Clear();
            if (steps == null) return;

            foreach (var step in steps)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var mark = new TextBlock
                {
                    FontSize = 13,
                    Width = 18,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                DockPanel.SetDock(mark, Dock.Left);

                var label = new TextBlock
                {
                    Text = FormatLabel(step),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top,
                };

                ApplyStatus(mark, label, step.Status);
                row.Children.Add(mark);
                row.Children.Add(label);
                _stepsPanel.Children.Add(row);
                _stepMarks[step.N] = mark;
                _stepLabels[step.N] = label;
            }
        }

        private static string FormatLabel(AgentPlanStep step)
        {
            var what = step?.What ?? "";
            return step == null ? what : $"{step.N}. {what}";
        }

        private static void ApplyStatus(TextBlock mark, TextBlock label, string status)
        {
            var s = (status ?? "pending").Trim().ToLowerInvariant();
            switch (s)
            {
                case "done":
                    mark.Text = "✓";
                    mark.Foreground = DoneFg;
                    label.Foreground = TitleFg;
                    label.FontWeight = FontWeights.Normal;
                    break;
                case "failed":
                    mark.Text = "✗";
                    mark.Foreground = FailFg;
                    label.Foreground = FailFg;
                    break;
                case "skipped":
                    mark.Text = "–";
                    mark.Foreground = PendingFg;
                    label.Foreground = MutedFg;
                    break;
                default:
                    mark.Text = "○";
                    mark.Foreground = PendingFg;
                    label.Foreground = TitleFg;
                    break;
            }
        }
    }
}
