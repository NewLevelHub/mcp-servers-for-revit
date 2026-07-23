using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace revit_mcp_plugin.UI.Assistant
{
    public sealed class ChatBubble : Grid
    {
        public ChatBubble(string text, bool fromUser)
        {
            Margin = new Thickness(0, 0, 0, 10);
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = CreateAvatar(fromUser);
            var bubble = CreateBubble(text, fromUser);

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

        private static Border CreateBubble(string text, bool fromUser)
        {
            var radius = fromUser
                ? new CornerRadius(14, 14, 4, 14)
                : new CornerRadius(14, 14, 14, 4);

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
                Child = new TextBlock
                {
                    Text = text ?? "",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = fromUser
                        ? Brushes.White
                        : new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33)),
                    FontSize = 12.5,
                    LineHeight = 18
                }
            };
        }
    }
}
