using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI.Assistant
{
    /// <summary>
    /// Bubble for a single chat message.
    /// For bot messages exposes a feedback bar (👍 👎 📋 ↻ ✎) and a dislike-reason form.
    /// Subscribe to <see cref="FeedbackSubmitted"/> to receive ratings.
    /// </summary>
    public sealed class ChatBubble : Grid
    {
        /// <summary>Raised when the user clicks 👍 or 👎 (and optionally selects a reason tag).</summary>
        public event EventHandler<FeedbackEventArgs> FeedbackSubmitted;

        /// <summary>REV-127: re-send the original user request.</summary>
        public event EventHandler<RetryEventArgs> RetryRequested;

        /// <summary>REV-127: load the original user request into the input box.</summary>
        public event EventHandler<RetryEventArgs> EditRequested;

        /// <summary>
        /// The two accuracy tags come from the architects' own review: "стены выстраиваются
        /// с отклонениями от исходной геометрии" and "неверное определение привязок" were
        /// their top complaints and had nowhere to go — they landed in "(без тега)" or, worse,
        /// under "не тот инструмент", where a wrong tool is not what went wrong at all.
        /// </summary>
        private static readonly string[] DislikeReasons =
        {
            "не понял запрос",
            "не тот инструмент",
            "построил неточно",
            "не те привязки",
            "сломал модель",
            "выдумал нормы",
            "не довёл до конца",
            "слишком долго",
            "ошибка/упал",
        };

        private readonly string _turnId;
        private readonly string _userRequestText;
        private string _messageText;
        private readonly bool _fromUser;

        /// <summary>
        /// True only for bubbles that carry a real assistant turn. System notices
        /// ("Отчёт сохранён…", "Новый диалог", errors) have no turn behind them, so
        /// rating one produced a rating patch whose turnId matched no log entry —
        /// and the complaint arrived empty, which is the very thing the turn log fixed.
        /// </summary>
        private readonly bool _ratable;

        private Button _likeBtn;
        private Button _dislikeBtn;
        private int _rating;

        private Border _dislikeForm;
        private string _selectedReason;
        private readonly List<Button> _reasonChips = new List<Button>();
        private TextBox _commentBox;
        /// <summary>Past a handful the extra shots stop being evidence and only make the package heavy.</summary>
        private const int MaxShots = 5;

        private Button _shotBtn;
        private Button _pasteBtn;
        private TextBlock _shotHint;
        private Border _shotPreview;
        private WrapPanel _shotList;
        private readonly List<string> _shotPaths = new List<string>();
        private Button _sendBtn;
        private TextBlock _feedbackStatusText;
        private readonly string _metaFooter;
        private StackPanel _messageStack;
        private FrameworkElement _textHost;

        public ChatBubble(
            string text,
            bool fromUser,
            IList<ChatAttachment> attachments = null,
            string turnId = null,
            string metaFooter = null,
            string userRequestText = null)
        {
            // A generated id used to stand in here, which is exactly how a notice
            // ended up ratable against a turn that never existed.
            _ratable = !string.IsNullOrWhiteSpace(turnId);
            _turnId = turnId;
            _metaFooter = metaFooter;
            _userRequestText = userRequestText;
            _messageText = text ?? "";
            _fromUser = fromUser;

            Margin = new Thickness(0, 0, 0, 10);
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = CreateAvatar(fromUser);

            FrameworkElement bubbleContent = fromUser
                ? CreateBubble(text, fromUser, attachments)
                : CreateBotBubbleWithActions(text, attachments);

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

        /// <summary>REV-127: update streamed text in place (throttled by caller).</summary>
        public void SetStreamingText(string text)
        {
            _messageText = text ?? "";
            if (_messageStack == null) return;

            if (_textHost != null)
                _messageStack.Children.Remove(_textHost);

            if (!string.IsNullOrWhiteSpace(_messageText))
            {
                _textHost = _fromUser
                    ? CreateSelectableMessageText(_messageText, fromUser: true)
                    : AssistantMarkdown.CreateViewer(_messageText, fromUser: false);
                _messageStack.Children.Add(_textHost);
            }
            else
            {
                _textHost = null;
            }
        }

        private FrameworkElement CreateBotBubbleWithActions(string text, IList<ChatAttachment> attachments)
        {
            var outer = new StackPanel();
            outer.Children.Add(CreateBubble(text, fromUser: false, attachments));

            var actionRow = new WrapPanel { Margin = new Thickness(2, 4, 0, 0) };

            var copyBtn = MakeActionButton("📋", "Копировать");
            var retryBtn = MakeActionButton("↻", "Повторить");
            var editBtn = MakeActionButton("✎", "Изменить запрос");

            if (_ratable)
            {
                _likeBtn = MakeActionButton("👍", "Хороший ответ");
                _dislikeBtn = MakeActionButton("👎", "Плохой ответ");
                _likeBtn.Click += (s, e) => OnLikeClick();
                _dislikeBtn.Click += (s, e) => OnDislikeClick();
            }

            copyBtn.Click += (s, e) => OnCopyClick(_messageText);
            retryBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(_userRequestText))
                    RetryRequested?.Invoke(this, new RetryEventArgs(_turnId, _userRequestText));
            };
            editBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(_userRequestText))
                    EditRequested?.Invoke(this, new RetryEventArgs(_turnId, _userRequestText));
            };

            if (_ratable)
            {
                actionRow.Children.Add(_likeBtn);
                actionRow.Children.Add(_dislikeBtn);
            }
            actionRow.Children.Add(copyBtn);
            if (!string.IsNullOrWhiteSpace(_userRequestText))
            {
                actionRow.Children.Add(retryBtn);
                actionRow.Children.Add(editBtn);
            }
            outer.Children.Add(actionRow);

            if (_ratable)
            {
                // Lives outside _dislikeForm on purpose: SubmitDislike hides the form right
                // after firing FeedbackSubmitted, and this ack must survive that (Д19).
                _feedbackStatusText = new TextBlock
                {
                    FontSize = 10.5,
                    Margin = new Thickness(2, 3, 0, 0),
                    Visibility = Visibility.Collapsed,
                    TextWrapping = TextWrapping.Wrap,
                };
                outer.Children.Add(_feedbackStatusText);
            }

            if (!string.IsNullOrWhiteSpace(_metaFooter))
            {
                outer.Children.Add(new TextBlock
                {
                    Text = _metaFooter,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x96, 0xA8)),
                    Margin = new Thickness(2, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            if (_ratable)
            {
                _dislikeForm = BuildDislikeForm();
                outer.Children.Add(_dislikeForm);
            }

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
            return new ControlTemplate(typeof(Button)) { VisualTree = factory };
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
            stack.Children.Add(new TextBlock
            {
                Text = "Что пошло не так? Тег и/или пара слов",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44)),
                Margin = new Thickness(0, 0, 0, 6),
            });

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
                ToolTip = "Можно отправить один комментарий, без тега",
            };
            AutomationProperties.SetName(_commentBox, "Комментарий к дизлайку");
            _commentBox.TextChanged += (s, e) => UpdateSendEnabled();
            _commentBox.PreviewKeyDown += CommentBoxPreviewKeyDown;
            stack.Children.Add(_commentBox);

            // Two ways in on purpose: the full-screen grab is one click and always available,
            // but it cannot point at anything. A crop the architect took themselves can.
            var shotRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

            _shotBtn = MakeShotButton(
                "📷 Скрин экрана",
                "Снимок окна Revit в текущем виде — попадёт в отчёт вместе с жалобой",
                "Приложить снимок экрана к дизлайку");
            _shotBtn.Click += (s, e) => CaptureShot();
            shotRow.Children.Add(_shotBtn);

            _pasteBtn = MakeShotButton(
                "📋 Вставить свой",
                "Свой скриншот из буфера обмена: вырежьте нужное через Win+Shift+S, потом сюда (или Ctrl+V в поле выше)",
                "Вставить скриншот из буфера обмена");
            _pasteBtn.Click += (s, e) => PasteShot();
            shotRow.Children.Add(_pasteBtn);

            stack.Children.Add(shotRow);

            _shotHint = new TextBlock
            {
                Visibility = Visibility.Collapsed,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A)),
            };
            stack.Children.Add(_shotHint);

            _shotPreview = BuildShotPreview();
            stack.Children.Add(_shotPreview);

            _sendBtn = new Button
            {
                Content = "Отправить",
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0),
                IsEnabled = false,
            };
            AutomationProperties.SetName(_sendBtn, "Отправить дизлайк");
            _sendBtn.Template = BuildSimpleRoundedButtonTemplate(10);
            ApplySendButtonEnabledStyle(enabled: false);
            _sendBtn.Click += (s, e) => SubmitDislike();

            foreach (var chip in _reasonChips)
                chip.Click += (s, e) => UpdateSendEnabled();

            stack.Children.Add(_sendBtn);
            form.Child = stack;
            return form;
        }


        private Button MakeShotButton(string caption, string tooltip, string automationName)
        {
            var btn = new Button
            {
                Content = caption,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
            };
            AutomationProperties.SetName(btn, automationName);
            btn.Template = BuildSimpleRoundedButtonTemplate(12);
            return btn;
        }

        private Border BuildShotPreview()
        {
            _shotList = new WrapPanel();
            return new Border
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 6, 0, 0),
                Child = _shotList,
            };
        }

        /// <summary>
        /// Rebuilds the thumbnail strip from <see cref="_shotPaths"/>. Every shot carries its
        /// own ✕: with a single attachment "убрать" and "убрать всё" were the same thing,
        /// with four of them they are not.
        /// </summary>
        private void RenderShots()
        {
            if (_shotList == null) return;

            _shotList.Children.Clear();
            foreach (var path in _shotPaths)
                _shotList.Children.Add(BuildShotTile(path));

            if (_shotPreview != null)
                _shotPreview.Visibility = _shotPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement BuildShotTile(string path)
        {
            var thumb = new Image
            {
                MaxWidth = 74,
                MaxHeight = 56,
                Stretch = Stretch.Uniform,
            };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // do not hold the file open
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 150;
                bmp.EndInit();
                bmp.Freeze();
                thumb.Source = bmp;
            }
            catch { /* the file is attached either way; only its preview is lost */ }

            var remove = new Button
            {
                Content = "✕",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0),
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Убрать этот скрин",
            };
            AutomationProperties.SetName(remove, "Убрать скриншот");
            remove.Template = BuildSimpleRoundedButtonTemplate(9);
            remove.Click += (s, e) => RemoveShot(path);

            var tile = new Grid { Margin = new Thickness(0, 0, 8, 6) };
            tile.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xE8)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                ClipToBounds = true,
                MinWidth = 40,
                MinHeight = 30,
                Child = thumb,
            });
            tile.Children.Add(remove);
            return tile;
        }

        /// <summary>
        /// Grabs the Revit window as it looks right now. The panel and the open form are
        /// part of the frame on purpose — it shows which answer the complaint is about.
        /// </summary>
        private void CaptureShot()
        {
            if (!HasRoomForAnotherShot())
                return;

            // Let WPF finish painting the pressed button first, or the shot catches the
            // form half-rendered.
            try { Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render); }
            catch { /* not fatal */ }

            var path = FeedbackScreenshot.Capture(_turnId);
            if (string.IsNullOrEmpty(path))
            {
                ShowShotHint("Не удалось снять экран.", error: true);
                return;
            }

            AttachShot(path);
        }

        /// <summary>
        /// Attaches the picture the architect cropped themselves. The full-screen grab shows
        /// the whole window, where the wall that came out wrong is one detail among hundreds;
        /// a Win+Shift+S crop points straight at it.
        /// </summary>
        private void PasteShot()
        {
            if (!HasRoomForAnotherShot())
                return;

            var path = FeedbackScreenshot.SaveFromClipboard(_turnId);
            if (string.IsNullOrEmpty(path))
            {
                ShowShotHint("В буфере нет картинки. Вырежьте нужный кусок через Win+Shift+S и нажмите ещё раз.", error: true);
                return;
            }

            AttachShot(path);
        }

        /// <summary>
        /// One complaint often needs two pictures — the wrong result and the drawing it was
        /// supposed to follow — so shots add up instead of replacing each other.
        /// </summary>
        private void AttachShot(string path)
        {
            _shotPaths.Add(path);
            HideShotHint();
            RenderShots();
            UpdateSendEnabled();
        }

        private bool HasRoomForAnotherShot()
        {
            if (_shotPaths.Count < MaxShots)
                return true;

            ShowShotHint($"Больше {MaxShots} скринов к одной жалобе не приложить — уберите лишний.", error: true);
            return false;
        }

        /// <summary>Drops one shot and deletes its file; the rest of the complaint stays.</summary>
        private void RemoveShot(string path)
        {
            if (!_shotPaths.Remove(path))
                return;

            try { File.Delete(path); } catch { /* leave the orphan, purge sweeps it */ }
            RenderShots();
            UpdateSendEnabled();
        }

        /// <summary>
        /// Ctrl+V in the comment box attaches the clipboard picture instead of doing nothing.
        /// A TextBox silently swallows an image paste, and the crop the architect just took
        /// is exactly the thing they meant to send. Text on the clipboard still pastes as text.
        /// </summary>
        private void CommentBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            bool hasText;
            try { hasText = System.Windows.Clipboard.ContainsText(); }
            catch { hasText = false; }
            if (hasText || !FeedbackScreenshot.ClipboardHasImage())
                return;

            e.Handled = true;
            PasteShot();
        }

        private void ShowShotHint(string text, bool error)
        {
            if (_shotHint == null) return;
            _shotHint.Text = text;
            _shotHint.Foreground = new SolidColorBrush(error
                ? Color.FromRgb(0xB0, 0x2A, 0x2A)
                : Color.FromRgb(0x5A, 0x66, 0x75));
            _shotHint.Visibility = Visibility.Visible;
        }

        private void HideShotHint()
        {
            if (_shotHint == null) return;
            _shotHint.Text = string.Empty;
            _shotHint.Visibility = Visibility.Collapsed;
        }

        /// <summary>Drops every pending shot and deletes the files — see <see cref="ResetShotUi"/>.</summary>
        private void ClearShots()
        {
            foreach (var path in _shotPaths)
            {
                try { File.Delete(path); } catch { /* leave the orphan, purge sweeps it */ }
            }
            ResetShotUi();
        }

        /// <summary>
        /// Forgets the shots without touching the files. Used after submit, where ownership
        /// has passed to the rating patch that now names them.
        /// </summary>
        private void ResetShotUi()
        {
            _shotPaths.Clear();
            RenderShots();
            HideShotHint();
        }

        /// <summary>
        /// Enabled when there is a tag, a comment OR a screenshot — a tag alone used to be
        /// required, which made a comment-only report impossible to send (Д19).
        /// </summary>
        private void UpdateSendEnabled()
        {
            var enabled = _selectedReason != null
                || !string.IsNullOrWhiteSpace(_commentBox?.Text)
                || _shotPaths.Count > 0;
            if (_sendBtn != null)
                _sendBtn.IsEnabled = enabled;
            ApplySendButtonEnabledStyle(enabled);
        }

        /// <summary>
        /// <see cref="BuildSimpleRoundedButtonTemplate"/> has no IsEnabled trigger, so a
        /// disabled Send button used to look identical to an enabled one (Д19) — style it
        /// in code instead, same pattern as <see cref="ApplyChipStyle"/>.
        /// </summary>
        private void ApplySendButtonEnabledStyle(bool enabled)
        {
            if (_sendBtn == null) return;
            _sendBtn.Background = enabled
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x44))
                : new SolidColorBrush(Color.FromRgb(0xE3, 0xE7, 0xEC));
            _sendBtn.Foreground = enabled
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB0));
            _sendBtn.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
        }

        /// <summary>
        /// Called by the host once <c>WriteRatingPatch</c> returns, so a silent log
        /// failure is visible instead of leaving the architect believing they complained
        /// successfully (Д19 — "снять глушитель").
        /// </summary>
        public void ShowFeedbackResult(bool ok)
        {
            if (_feedbackStatusText == null) return;
            _feedbackStatusText.Text = ok ? "Записано" : "Не сохранено — попробуйте ещё раз";
            _feedbackStatusText.Foreground = ok
                ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
                : new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A));
            _feedbackStatusText.Visibility = Visibility.Visible;
        }

        private Button MakeReasonChip(string reason)
        {
            var btn = new Button
            {
                Content = reason,
                Margin = new Thickness(0, 0, 4, 4),
                Padding = new Thickness(8, 4, 8, 4),
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
                _selectedReason = null;
                ApplyChipStyle(clicked, selected: false);
            }
            else
            {
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

        private void OnLikeClick()
        {
            if (_rating == 1)
            {
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
            if (_dislikeForm != null)
                _dislikeForm.Visibility = Visibility.Visible;
        }

        private void HideDislikeForm()
        {
            if (_dislikeForm == null)
                return;

            _dislikeForm.Visibility = Visibility.Collapsed;
            _selectedReason = null;
            foreach (var c in _reasonChips)
                ApplyChipStyle(c, selected: false);
            if (_commentBox != null)
                _commentBox.Text = string.Empty;
            ClearShots();
            UpdateSendEnabled();
        }

        private void SubmitDislike()
        {
            var comment = _commentBox?.Text?.Trim();
            var shots = _shotPaths.Count > 0 ? _shotPaths.ToArray() : null;
            if (_selectedReason == null && string.IsNullOrEmpty(comment) && shots == null) return;

            // Hand the files over before HideDislikeForm runs, otherwise ClearShots deletes
            // the very screenshots the rating patch has just been told to reference.
            ResetShotUi();
            FeedbackSubmitted?.Invoke(this, new FeedbackEventArgs(
                _turnId, -1, _selectedReason, string.IsNullOrEmpty(comment) ? null : comment, shots));
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

        private Border CreateBubble(string text, bool fromUser, IList<ChatAttachment> attachments)
        {
            var radius = fromUser
                ? new CornerRadius(14, 14, 4, 14)
                : new CornerRadius(14, 14, 14, 4);

            _messageStack = new StackPanel();
            if (attachments != null && attachments.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, string.IsNullOrWhiteSpace(text) ? 0 : 8) };
                foreach (var a in attachments)
                {
                    if (a == null) continue;
                    wrap.Children.Add(CreateAttachmentPreview(a, fromUser));
                }
                _messageStack.Children.Add(wrap);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                _textHost = fromUser
                    ? CreateSelectableMessageText(text, fromUser)
                    : AssistantMarkdown.CreateViewer(text, fromUser);
                _messageStack.Children.Add(_textHost);
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
                Child = _messageStack
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
                    // fall through
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

    public sealed class FeedbackEventArgs : EventArgs
    {
        private static readonly string[] NoShots = new string[0];

        public string TurnId { get; }
        public int Rating { get; }
        public string Reason { get; }
        public string Comment { get; }

        /// <summary>Full paths to the attached screenshots under Logs/feedback-shots; never null.</summary>
        public IReadOnlyList<string> ShotPaths { get; }

        /// <summary>First attachment, or null — for callers that only ever expected one.</summary>
        public string ShotPath => ShotPaths.Count > 0 ? ShotPaths[0] : null;

        public FeedbackEventArgs(string turnId, int rating, string reason, string comment, IReadOnlyList<string> shotPaths = null)
        {
            TurnId = turnId;
            Rating = rating;
            Reason = reason;
            Comment = comment;
            ShotPaths = shotPaths ?? NoShots;
        }
    }

    /// <summary>REV-127: retry / edit the original user prompt.</summary>
    public sealed class RetryEventArgs : EventArgs
    {
        public string TurnId { get; }
        public string UserText { get; }

        public RetryEventArgs(string turnId, string userText)
        {
            TurnId = turnId;
            UserText = userText;
        }
    }
}
