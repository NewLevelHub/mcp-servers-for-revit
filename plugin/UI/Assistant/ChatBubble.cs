using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI.Assistant
{
    /// <summary>
    /// Bubble for a single chat message.
    /// For bot messages exposes a feedback bar (👍 👎 📋) and a dislike-reason form.
    /// Subscribe to <see cref="FeedbackSubmitted"/> to receive ratings.
    /// </summary>
    public sealed class ChatBubble : Grid
    {
        /// <summary>Raised when the user clicks 👍 or 👎 (and optionally selects a reason tag).</summary>
        public event EventHandler<FeedbackEventArgs> FeedbackSubmitted;

        // Dislike reason chips shown when 👎 is pressed
        private static readonly string[] DislikeReasons =
        {
            "не понял запрос",
            "не тот инструмент",
            "сломал модель",
            "выдумал нормы",
            "не довёл до конца",
            "слишком долго",
            "ошибка/упал",
        };

        private readonly string _turnId;

        // Action-row buttons (kept as fields to toggle visual state)
        private Button _likeBtn;
        private Button _dislikeBtn;
        private int _rating; // 0=none, 1=like, -1=dislike

        // Dislike form
        private Border _dislikeForm;
        private string _selectedReason;
        private readonly List<Button> _reasonChips = new List<Button>();
        private TextBox _commentBox;

        public ChatBubble(string text, bool fromUser, IList<ChatAttachment> attachments = null, string turnId = null)
        {
            _turnId = turnId ?? Guid.NewGuid().ToString("N").Substring(0, 8);

            Margin = new Thickness(0, 0, 0, 10);
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = CreateAvatar(fromUser);

            FrameworkElement bubbleContent;
            if (fromUser)
            {
                bubbleContent = CreateBubble(text, fromUser, attachments);
            }
            else
            {
                bubbleContent = CreateBotBubbleWithActions(text, attachments);
            }

            if (fromUser)
            {
                SetColumn(bubbleContent, 1);
                SetColumn(avatar, 2);
                bubbleContent.Margin = new Thickness(36, 0, 8, 0);
                bubbleContent.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                SetColumn(avatar, 0);
                SetColumn(bubbleContent, 1);
                bubbleContent.Margin = new Thickness(8, 0, 36, 0);
                bubbleContent.HorizontalAlignment = HorizontalAlignment.Left;
            }

            Children.Add(avatar);
            Children.Add(bubbleContent);
        }

        // ── Bot bubble: message + action row + dislike form ─────────────────────
        private FrameworkElement CreateBotBubbleWithActions(string text, IList<ChatAttachment> attachments)
        {
            var outer = new StackPanel();

            // The message bubble itself
            var messageBorder = CreateBubble(text, fromUser: false, attachments);
            outer.Children.Add(messageBorder);

            // Action row: 👍 👎 📋
            var actionRow = new WrapPanel { Margin = new Thickness(2, 4, 0, 0) };

            _likeBtn = MakeActionButton("👍", "Хороший ответ");
            _dislikeBtn = MakeActionButton("👎", "Плохой ответ");
            var copyBtn = MakeActionButton("📋", "Копировать");

            _likeBtn.Click += (s, e) => OnLikeClick();
            _dislikeBtn.Click += (s, e) => OnDislikeClick();
            copyBtn.Click += (s, e) => OnCopyClick(text);

            actionRow.Children.Add(_likeBtn);
            actionRow.Children.Add(_dislikeBtn);
            actionRow.Children.Add(copyBtn);
            outer.Children.Add(actionRow);

            // Dislike form (hidden until 👎 pressed)
            _dislikeForm = BuildDislikeForm();
            outer.Children.Add(_dislikeForm);

            return outer;
        }

        private static Button MakeActionButton(string emoji, string automationName)
        {
            var btn = new Button
            {
                Content = new TextBlock { Text = emoji, FontSize = 13 },
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = automationName,
            };
            AutomationProperties.SetName(btn, automationName);
            btn.Template = MakeRoundButtonTemplate(28);
            return btn;
        }

        private static ControlTemplate MakeRoundButtonTemplate(double size)
        {
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(content);

            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = factory };

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xF7)), "bd"));
            // triggers need named elements — skip hover styling here for simplicity, handled via style below
            return tpl;
        }

        private Border BuildDislikeForm()
        {
            var form = new Border
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE5)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x00)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 0),
                MaxWidth = 280,
            };

            var stack = new StackPanel();

            var label = new TextBlock
            {
                Text = "Что пошло не так? (выберите тег)",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44)),
                Margin = new Thickness(0, 0, 0, 6),
            };
            stack.Children.Add(label);

            var wrap = new WrapPanel();
            foreach (var reason in DislikeReasons)
            {
                var chip = MakeReasonChip(reason);
                _reasonChips.Add(chip);
                wrap.Children.Add(chip);
            }
            stack.Children.Add(wrap);

            _commentBox = new TextBox
            {
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(8, 5, 8, 5),
                MinHeight = 36,
                MaxHeight = 72,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 11.5,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33)),
                ToolTip = "Необязательный комментарий",
            };
            AutomationProperties.SetName(_commentBox, "Комментарий к дизлайку");
            stack.Children.Add(_commentBox);

            var sendBtn = new Button
            {
                Content = "Отправить",
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(12, 5),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                IsEnabled = false,
            };
            AutomationProperties.SetName(sendBtn, "Отправить дизлайк");
            sendBtn.Template = BuildSimpleRoundedButtonTemplate(10);
            sendBtn.Click += (s, e) => SubmitDislike(sendBtn);

            // Enable send only when a reason is selected
            foreach (var chip in _reasonChips)
            {
                chip.Click += (s, e) =>
                {
                    sendBtn.IsEnabled = _selectedReason != null;
                };
            }

            stack.Children.Add(sendBtn);
            form.Child = stack;
            return form;
        }

        private Button MakeReasonChip(string reason)
        {
            var btn = new Button
            {
                Content = reason,
                Margin = new Thickness(0, 0, 4, 4),
                Padding = new Thickness(8, 4),
                FontSize = 11,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            AutomationProperties.SetName(btn, "Причина: " + reason);
            btn.Template = BuildSimpleRoundedButtonTemplate(12);
            btn.Click += (s, e) => SelectReason(reason, btn);
            return btn;
        }

        private void SelectReason(string reason, Button clicked)
        {
            if (_selectedReason == reason)
            {
                // toggle off
                _selectedReason = null;
                ApplyChipStyle(clicked, selected: false);
            }
            else
            {
                // deselect previous
                foreach (var c in _reasonChips)
                    ApplyChipStyle(c, selected: false);

                _selectedReason = reason;
                ApplyChipStyle(clicked, selected: true);
            }
        }

        private static void ApplyChipStyle(Button chip, bool selected)
        {
            chip.Background = selected
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44))
                : Brushes.White;
            chip.Foreground = selected
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44));
            chip.BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44))
                : new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8));
        }

        private static ControlTemplate BuildSimpleRoundedButtonTemplate(double radius)
        {
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            factory.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = factory };
        }

        // ── Rating actions ───────────────────────────────────────────────────────
        private void OnLikeClick()
        {
            if (_rating == 1)
            {
                // toggle off
                _rating = 0;
                ApplyRatingButtonStyle(_likeBtn, active: false);
            }
            else
            {
                _rating = 1;
                ApplyRatingButtonStyle(_likeBtn, active: true);
                ApplyRatingButtonStyle(_dislikeBtn, active: false);
                HideDislikeForm();
                FeedbackSubmitted?.Invoke(this, new FeedbackEventArgs(_turnId, 1, null, null));
            }
        }

        private void OnDislikeClick()
        {
            if (_rating == -1)
            {
                // toggle off — hide form and reset
                _rating = 0;
                ApplyRatingButtonStyle(_dislikeBtn, active: false);
                HideDislikeForm();
            }
            else
            {
                _rating = -1;
                ApplyRatingButtonStyle(_dislikeBtn, active: true);
                ApplyRatingButtonStyle(_likeBtn, active: false);
                ShowDislikeForm();
            }
        }

        private void OnCopyClick(string text)
        {
            try
            {
                if (!string.IsNullOrEmpty(text))
                    Clipboard.SetText(text);
            }
            catch { /* clipboard may be locked */ }
        }

        private void ShowDislikeForm()
        {
            _dislikeForm.Visibility = Visibility.Visible;
        }

        private void HideDislikeForm()
        {
            _dislikeForm.Visibility = Visibility.Collapsed;
            _selectedReason = null;
            foreach (var c in _reasonChips)
                ApplyChipStyle(c, selected: false);
            if (_commentBox != null)
                _commentBox.Text = string.Empty;
        }

        private void SubmitDislike(Button sendBtn)
        {
            if (_selectedReason == null) return;

            var comment = _commentBox?.Text?.Trim();
            FeedbackSubmitted?.Invoke(this, new FeedbackEventArgs(_turnId, -1, _selectedReason, string.IsNullOrEmpty(comment) ? null : comment));

            // Collapse form, keep 👎 active
            HideDislikeForm();
            ApplyRatingButtonStyle(_dislikeBtn, active: true);
        }

        private static void ApplyRatingButtonStyle(Button btn, bool active)
        {
            if (btn == null) return;
            btn.Background = active
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44))
                : Brushes.Transparent;
            btn.BorderBrush = active
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44))
                : new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8));
            if (btn.Content is TextBlock tb)
                tb.Foreground = active ? Brushes.White : Brushes.Black;
        }

        // ── Avatar / bubble (unchanged helpers) ─────────────────────────────────
        private static FrameworkElement CreateAvatar(bool fromUser)
        {
            var circle = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(fromUser
                    ? Color.FromRgb(0x2F, 0x5D, 0x8A)
                    : Color.FromRgb(0x1A, 0x27, 0x44))
            };

            circle.Child = new TextBlock
            {
                Text = fromUser ? "Вы" : "AI",
                FontSize = fromUser ? 10 : 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            return circle;
        }

        private static Border CreateBubble(string text, bool fromUser, IList<ChatAttachment> attachments)
        {
            var radius = fromUser
                ? new CornerRadius(14, 14, 4, 14)
                : new CornerRadius(14, 14, 14, 4);

            var stack = new StackPanel();
            if (attachments != null && attachments.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, string.IsNullOrWhiteSpace(text) ? 0 : 8) };
                foreach (var a in attachments)
                {
                    if (a == null) continue;
                    wrap.Children.Add(CreateAttachmentPreview(a, fromUser));
                }
                stack.Children.Add(wrap);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                stack.Children.Add(CreateSelectableMessageText(text, fromUser));
            }

            return new Border
            {
                CornerRadius = radius,
                Padding = new Thickness(12, 9, 12, 9),
                MaxWidth = 280,
                Background = new SolidColorBrush(fromUser
                    ? Color.FromRgb(0x2F, 0x5D, 0x8A)
                    : Color.FromRgb(0xF0, 0xF4, 0xF8)),
                BorderBrush = fromUser
                    ? null
                    : new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                BorderThickness = fromUser ? new Thickness(0) : new Thickness(1),
                Child = stack
            };
        }

        private static TextBox CreateSelectableMessageText(string text, bool fromUser)
        {
            var foreground = fromUser
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33));

            var box = new TextBox
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = foreground,
                FontSize = 12.5,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Cursor = Cursors.IBeam,
                FocusVisualStyle = null,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalAlignment = VerticalAlignment.Top
            };

            if (fromUser)
            {
                box.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
                box.SelectionTextBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33));
            }

            box.Loaded += (_, __) => ResizeMessageTextBox(box);
            box.SizeChanged += (_, __) => ResizeMessageTextBox(box);
            return box;
        }

        private static void ResizeMessageTextBox(TextBox box)
        {
            var width = box.ActualWidth;
            if (width <= 0 || double.IsNaN(width))
                width = 256;

            box.Measure(new Size(width, double.PositiveInfinity));
            var height = Math.Ceiling(box.DesiredSize.Height);
            box.MinHeight = height;
            box.MaxHeight = height;
        }

        private static FrameworkElement CreateAttachmentPreview(ChatAttachment attachment, bool fromUser)
        {
            if (attachment.IsImage && attachment.Data != null && attachment.Data.Length > 0)
            {
                try
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(attachment.Data))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.DecodePixelWidth = 160;
                        bmp.EndInit();
                        bmp.Freeze();
                    }

                    return new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Margin = new Thickness(0, 0, 6, 6),
                        BorderBrush = fromUser
                            ? new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))
                            : new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                        BorderThickness = new Thickness(1),
                        ClipToBounds = true,
                        Child = new Image
                        {
                            Source = bmp,
                            MaxWidth = 160,
                            MaxHeight = 100,
                            Stretch = Stretch.Uniform
                        },
                        ToolTip = attachment.DisplayLabel
                    };
                }
                catch
                {
                    // fall through to chip
                }
            }

            var fg = fromUser ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44));
            var bg = fromUser
                ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF4));

            return new Border
            {
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 5, 8, 5),
                Background = bg,
                Child = new TextBlock
                {
                    Text = attachment.KindLabel + " · " + (attachment.FileName ?? "файл"),
                    FontSize = 11,
                    Foreground = fg,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 180
                },
                ToolTip = attachment.DisplayLabel
            };
        }
    }

    /// <summary>Event data for a like/dislike rating submitted by the user.</summary>
    public sealed class FeedbackEventArgs : EventArgs
    {
        /// <summary>Identifier of the assistant turn being rated.</summary>
        public string TurnId { get; }

        /// <summary>+1 = like, -1 = dislike.</summary>
        public int Rating { get; }

        /// <summary>One of the fixed dislike-reason tags; null for likes.</summary>
        public string Reason { get; }

        /// <summary>Free-text comment; null when not provided.</summary>
        public string Comment { get; }

        public FeedbackEventArgs(string turnId, int rating, string reason, string comment)
        {
            TurnId = turnId;
            Rating = rating;
            Reason = reason;
            Comment = comment;
        }
    }
}
