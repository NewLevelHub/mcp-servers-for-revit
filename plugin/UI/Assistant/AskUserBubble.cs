using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI.Assistant
{
    /// <summary>
    /// Clarification card styled like Claude multiple-choice (REV-125):
    /// calm question + full-width lettered options.
    /// </summary>
    public sealed class AskUserBubble : Border
    {
        // Warm stone palette — closer to Claude than cold navy-gray chips.
        private static readonly Brush PageBg = new SolidColorBrush(Color.FromRgb(0xF7, 0xF6, 0xF3));
        private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0xE2, 0xDB));
        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x1E, 0x1B));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x73, 0x6E, 0x66));
        private static readonly Brush OptionBg = Brushes.White;
        private static readonly Brush OptionBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0xE2, 0xDB));
        private static readonly Brush OptionHover = new SolidColorBrush(Color.FromRgb(0xF0, 0xEE, 0xE9));
        private static readonly Brush LetterBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xEE, 0xE9));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xC4, 0x64, 0x3A)); // warm terracotta accent
        private static readonly Brush DoneBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xF5, 0xEE));
        private static readonly Brush DoneBorder = new SolidColorBrush(Color.FromRgb(0xC8, 0xD9, 0xC4));
        private static readonly Brush DoneFg = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0x45));

        private readonly StackPanel _root;
        private readonly StackPanel _optionsStack;
        private readonly StackPanel _freeTextRow;
        private readonly TextBox _freeTextBox;
        private readonly List<Border> _optionRows = new List<Border>();
        private readonly bool _allowFreeText;
        private bool _answered;

        public event Action<AskUserAnswer> Answered;

        public AskUserBubble(PendingAskUser pending)
        {
            Margin = new Thickness(44, 4, 28, 10);
            HorizontalAlignment = HorizontalAlignment.Left;
            MaxWidth = 360;
            Background = PageBg;
            BorderBrush = CardBorder;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(16);
            Padding = new Thickness(16, 14, 16, 14);

            _allowFreeText = pending == null || pending.AllowFreeText;
            _root = new StackPanel();

            _root.Children.Add(new TextBlock
            {
                Text = pending?.Question ?? "Уточните, пожалуйста",
                FontSize = 14.5,
                FontWeight = FontWeights.Medium,
                Foreground = Ink,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 14),
            });

            _optionsStack = new StackPanel();
            var options = pending?.Options ?? Array.Empty<string>();
            var letter = 'A';
            foreach (var opt in options)
            {
                if (string.IsNullOrWhiteSpace(opt)) continue;
                var label = opt.Trim();
                var key = letter;
                if (letter < 'Z') letter++;

                var row = MakeOptionRow(key, label, () =>
                {
                    if (IsOtherLabel(label) && _allowFreeText)
                    {
                        ShowFreeText(focus: true);
                        return;
                    }
                    Complete(new AskUserAnswer { SelectedOption = label });
                });
                _optionRows.Add(row);
                _optionsStack.Children.Add(row);
            }
            _root.Children.Add(_optionsStack);

            _freeTextRow = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            _freeTextBox = new TextBox
            {
                FontSize = 13,
                MinHeight = 36,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = OptionBorder,
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Foreground = Ink,
            };
            _freeTextBox.KeyDown += FreeText_KeyDown;

            var send = new Button
            {
                Content = "Продолжить",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(14, 7, 14, 7),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Background = Ink,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
            };
            send.Template = RoundedButtonTemplate(10);
            send.Click += (_, __) => SubmitFreeText();

            _freeTextRow.Children.Add(_freeTextBox);
            _freeTextRow.Children.Add(send);
            _root.Children.Add(_freeTextRow);

            if (_allowFreeText && !HasOtherOption(options))
            {
                var link = new TextBlock
                {
                    Text = "Свой вариант",
                    FontSize = 12,
                    Foreground = Muted,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(2, 12, 0, 0),
                };
                link.MouseEnter += (_, __) => link.Foreground = Accent;
                link.MouseLeave += (_, __) => link.Foreground = Muted;
                link.MouseLeftButtonUp += (_, __) => ShowFreeText(focus: true);
                _root.Children.Add(link);
            }

            Child = _root;
        }

        private Border MakeOptionRow(char letter, string label, Action onClick)
        {
            var row = new Border
            {
                Background = OptionBg,
                BorderBrush = OptionBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                Tag = label,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(8),
                Background = LetterBg,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = letter.ToString(),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Muted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(badge, 0);

            var text = new TextBlock
            {
                Text = label,
                FontSize = 13.5,
                Foreground = Ink,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(text, 1);

            grid.Children.Add(badge);
            grid.Children.Add(text);
            row.Child = grid;

            row.MouseEnter += (_, __) =>
            {
                if (_answered) return;
                row.Background = OptionHover;
                row.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xCB, 0xC2));
            };
            row.MouseLeave += (_, __) =>
            {
                if (_answered) return;
                row.Background = OptionBg;
                row.BorderBrush = OptionBorder;
            };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };

            return row;
        }

        private static bool HasOtherOption(IReadOnlyList<string> options)
        {
            if (options == null) return false;
            foreach (var o in options)
            {
                if (IsOtherLabel(o)) return true;
            }
            return false;
        }

        private static bool IsOtherLabel(string label) =>
            !string.IsNullOrWhiteSpace(label)
            && (label.Trim().Equals("Другое", StringComparison.OrdinalIgnoreCase)
                || label.Trim().Equals("Other", StringComparison.OrdinalIgnoreCase));

        private void ShowFreeText(bool focus)
        {
            _freeTextRow.Visibility = Visibility.Visible;
            if (focus)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _freeTextBox.Focus();
                    Keyboard.Focus(_freeTextBox);
                }));
            }
        }

        private static ControlTemplate RoundedButtonTemplate(double radius)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            border.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private void FreeText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                SubmitFreeText();
            }
        }

        private void SubmitFreeText()
        {
            if (_answered) return;
            var text = (_freeTextBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return;
            Complete(new AskUserAnswer { FreeText = text });
        }

        public void Cancel()
        {
            if (_answered) return;
            Complete(new AskUserAnswer { Cancelled = true });
        }

        private void Complete(AskUserAnswer answer)
        {
            if (_answered) return;
            _answered = true;
            ShowAnsweredState(answer);
            try { Answered?.Invoke(answer); }
            catch { /* ignore */ }
        }

        private void ShowAnsweredState(AskUserAnswer answer)
        {
            _root.Children.Clear();
            if (answer == null || answer.Cancelled)
            {
                _root.Children.Add(new TextBlock
                {
                    Text = "Отменено",
                    FontSize = 13,
                    Foreground = Muted,
                });
                return;
            }

            Background = DoneBg;
            BorderBrush = DoneBorder;
            var row = new DockPanel();
            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = DoneFg,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            row.Children.Add(new TextBlock
            {
                Text = answer.DisplayText,
                FontSize = 13.5,
                FontWeight = FontWeights.Medium,
                Foreground = DoneFg,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _root.Children.Add(row);
        }
    }
}
