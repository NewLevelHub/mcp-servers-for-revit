using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI.Assistant
{
    public sealed class ChatBubble : Grid
    {
        public ChatBubble(string text, bool fromUser, IList<ChatAttachment> attachments = null)
        {
            Margin = new Thickness(0, 0, 0, 10);
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = CreateAvatar(fromUser);
            var bubble = CreateBubble(text, fromUser, attachments);

            if (fromUser)
            {
                SetColumn(bubble, 1);
                SetColumn(avatar, 2);
                bubble.Margin = new Thickness(36, 0, 8, 0);
                bubble.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                SetColumn(avatar, 0);
                SetColumn(bubble, 1);
                bubble.Margin = new Thickness(8, 0, 36, 0);
                bubble.HorizontalAlignment = HorizontalAlignment.Left;
            }

            Children.Add(avatar);
            Children.Add(bubble);
        }

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
}
