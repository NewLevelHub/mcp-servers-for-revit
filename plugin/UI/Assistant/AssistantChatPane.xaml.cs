using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Core;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI.Assistant
{
    public partial class AssistantChatPane : UserControl
    {
        private UIApplication _uiApp;
        private readonly LocalAgentHost _agent = new LocalAgentHost();
        private readonly List<ChatAttachment> _pendingAttachments = new List<ChatAttachment>();
        private CancellationTokenSource _runCts;
        private TaskCompletionSource<bool> _confirmTcs;
        private bool _busy;

        public AssistantChatPane()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            _agent.StatusChanged += OnAgentStatus;
            _agent.ConfirmationRequested += OnConfirmationRequested;
            _agent.HistoryTrimmed += OnHistoryTrimmed;
            BuildChips();
            ShowWelcomeMessage();
            SetStatus("Готов", StatusTone.Ok);
        }

        private void ShowWelcomeMessage()
        {
            AddBotMessage(
                "Напишите запрос обычным языком или выберите сценарий ниже.\n" +
                "Enter — отправить, Shift+Enter — новая строка.\n" +
                "Файл: 📎, перетащить в чат или Ctrl+V (скрин).\n" +
                "Пример: «Проверь этаж и подпиши нарушения».\n\n" +
                "Если диалог стал длинным — нажмите «+ Новый», чтобы очистить историю.");
        }

        public void AttachUiApplication(UIApplication uiApp)
        {
            _uiApp = uiApp;
            RefreshContextAndBanner();
        }

        public void RefreshContextAndBanner()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshContextAndBanner));
                return;
            }

            ContextText.Text = BuildViewContextLine();
            var running = SocketService.Instance.IsRunning;
            ServerBanner.Visibility = running ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            var settings = PluginSettingsStore.LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.AssistantApiKey))
                SetStatus("Нужен ключ", StatusTone.Warn);
            else if (!running)
                SetStatus("Выключен", StatusTone.Warn);
            else if (!_busy)
                SetStatus("Готов", StatusTone.Ok);
        }

        private enum StatusTone { Ok, Busy, Warn }

        private void SetStatus(string text, StatusTone tone)
        {
            StatusText.Text = text;
            System.Windows.Media.Color bg;
            switch (tone)
            {
                case StatusTone.Busy:
                    bg = System.Windows.Media.Color.FromRgb(0x2F, 0x5D, 0x8A);
                    break;
                case StatusTone.Warn:
                    bg = System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09);
                    break;
                default:
                    bg = System.Windows.Media.Color.FromRgb(0x1F, 0x6B, 0x4A);
                    break;
            }
            StatusPill.Background = new SolidColorBrush(bg);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshContextAndBanner();
            RefreshFeedbackBadge();
        }

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                try { _runCts?.Cancel(); } catch { /* ignore */ }
                try { _confirmTcs?.TrySetResult(false); } catch { /* ignore */ }
            }

            StartNewChat(showNotice: true);
        }

        private void ExportFeedbackButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = Core.Assistant.FeedbackExporter.Export();
                if (path != null)
                {
                    AddBotMessage($"Отчёт сохранён:\n{path}\n\nПуть скопирован в буфер обмена.");
                }
                else
                {
                    AddBotMessage("Нет невыгруженных дизлайков.");
                }
                RefreshFeedbackBadge();
            }
            catch (Exception ex)
            {
                AddBotMessage("Ошибка выгрузки: " + ex.Message);
            }
        }

        private void RefreshFeedbackBadge()
        {
            try
            {
                var n = Core.Assistant.FeedbackExporter.CountPendingDislikes();
                if (n > 0)
                {
                    ExportFeedbackButton.Content = $"📊 {n}";
                    ExportFeedbackButton.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    ExportFeedbackButton.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
            catch { ExportFeedbackButton.Visibility = System.Windows.Visibility.Collapsed; }
        }

        private void StartNewChat(bool showNotice)
        {
            _agent.ClearHistory();
            MessagesPanel.Children.Clear();
            ConfirmBar.Visibility = System.Windows.Visibility.Collapsed;
            InputBox.Clear();
            ClearPendingAttachments();
            ShowWelcomeMessage();
            if (showNotice)
            {
                AddBotMessage("Новый чат. Предыдущая история очищена (на диск не сохранялась).");
            }
            RefreshContextAndBanner();
        }

        private void OnHistoryTrimmed()
        {
            // Don't interrupt an in-flight turn with a mid-stream system bubble.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_busy) return;
                AddBotMessage(
                    "Часть ранних сообщений убрана из памяти (длинный диалог). " +
                    "Последний запрос сохранён. Чтобы начать с нуля — «+ Новый».");
            }));
        }

        private void BuildChips()
        {
            ChipsPanel.Children.Clear();
            foreach (var preset in ScenarioPresets.Pilot)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(preset.Icon) ? "•" : preset.Icon,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3D, 0x7E, 0xA6))
                });
                content.Children.Add(new TextBlock
                {
                    Text = preset.Label,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var btn = new Button
                {
                    Content = content,
                    Style = (Style)FindResource("ChipButton"),
                    Tag = preset,
                    ToolTip = string.IsNullOrWhiteSpace(preset.Hint) ? preset.Label : preset.Hint
                };
                btn.Click += Chip_Click;
                ChipsPanel.Children.Add(btn);
            }
        }

        private void Chip_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var preset = (sender as Button)?.Tag as ScenarioPreset;
            if (preset == null) return;
            InputBox.Text = preset.Prompt;
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
            var attachments = _pendingAttachments.Count > 0 ? _pendingAttachments.ToList() : null;
            ClearPendingAttachments();
            InputBox.Clear();

            if (AssistantNormAuditRouting.ShouldRunDirectNormAudit(preset.Id))
            {
                _ = StartNormAuditAsync(preset.Prompt);
                return;
            }

            _ = StartRunAsync(preset.Prompt, attachments, ScenarioPresets.BuildAgentMessage(preset), preset.Profiles);
        }

        private async Task StartNormAuditAsync(string displayText)
        {
            if (_busy) return;
            _busy = true;
            SetBusyUi(true);
            AddUserMessage(displayText, null);
            RefreshContextAndBanner();
            SetStatus("Проверка норм…", StatusTone.Busy);

            try
            {
                var (reply, done) = await Task.Run(() => NormAuditPresetRunner.RunHighlight(annotate: true))
                    .ConfigureAwait(true);

                var text = reply;
                if (done != null && done.Count > 0)
                {
                    text += Environment.NewLine + Environment.NewLine +
                            "Сделано: " + string.Join(" · ", LocalAgentHost.CollapseDoneSummary(done));
                }

                AddBotMessage(text);
                SetStatus("Готов", StatusTone.Ok);
            }
            catch (Exception ex)
            {
                AddBotMessage("Ошибка проверки норм: " + ex.Message);
                SetStatus("Ошибка", StatusTone.Warn);
            }
            finally
            {
                _busy = false;
                SetBusyUi(false);
                RefreshContextAndBanner();
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var text = (InputBox.Text ?? "").Trim();
            if (_busy) return;
            if (string.IsNullOrEmpty(text) && _pendingAttachments.Count == 0) return;

            var attachments = _pendingAttachments.ToList();
            InputBox.Clear();
            ClearPendingAttachments();
            _ = StartRunAsync(string.IsNullOrEmpty(text) ? "Смотри вложение." : text, attachments);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            try { _runCts?.Cancel(); } catch { /* ignore */ }
            try { _confirmTcs?.TrySetResult(false); } catch { /* ignore */ }
        }

        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var dlg = new OpenFileDialog
            {
                Title = "Прикрепить файл",
                Filter =
                    "Все поддерживаемые|" +
                    "*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.pdf;" +
                    "*.doc;*.docx;*.rtf;*.odt;" +
                    "*.xls;*.xlsx;*.csv;*.tsv;" +
                    "*.ppt;*.pptx;" +
                    "*.txt;*.md;*.json;*.xml;*.html;*.htm;*.log|" +
                    "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|" +
                    "PDF|*.pdf|" +
                    "Word|*.doc;*.docx;*.rtf;*.odt|" +
                    "Excel|*.xls;*.xlsx;*.csv;*.tsv|" +
                    "PowerPoint|*.ppt;*.pptx|" +
                    "Текст|*.txt;*.md;*.json;*.xml;*.html;*.htm;*.log",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true)
                return;

            foreach (var path in dlg.FileNames)
                TryAddAttachmentFromPath(path);
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (TryPasteClipboardImage())
                    e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                CancelButton_Click(sender, e);
                return;
            }

            // Enter = send (как в обычном чате). Shift+Enter = новая строка.
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    return; // allow newline

                e.Handled = true;
                SendButton_Click(sender, e);
            }
        }

        private void Attachment_DragOver(object sender, DragEventArgs e)
        {
            if (_busy)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Attachment_Drop(object sender, DragEventArgs e)
        {
            if (_busy) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            foreach (var path in files)
                TryAddAttachmentFromPath(path);

            e.Handled = true;
            InputBox.Focus();
        }

        private bool TryPasteClipboardImage()
        {
            if (_busy) return false;
            try
            {
                if (!Clipboard.ContainsImage())
                    return false;

                var image = Clipboard.GetImage();
                if (image == null)
                    return false;

                var bytes = EncodeBitmapSourceAsJpeg(DownscaleIfNeeded(image, 1280), 75);
                var attachment = ChatAttachment.FromBytes(
                    "screenshot-" + DateTime.Now.ToString("HHmmss") + ".jpg",
                    "image/jpeg",
                    bytes);
                return TryAddAttachment(attachment);
            }
            catch (Exception ex)
            {
                AddBotMessage("Не удалось вставить скрин: " + ex.Message);
                return true; // consumed paste attempt
            }
        }

        private void TryAddAttachmentFromPath(string path)
        {
            try
            {
                if (!ChatAttachment.IsSupportedPath(path))
                {
                    AddBotMessage("Пропуск: формат не поддерживается — " + Path.GetFileName(path) +
                                  ". Можно: " + ChatAttachment.SupportedTypesHint + ".");
                    return;
                }

                var attachment = ChatAttachment.FromFile(path);
                if (attachment.IsImage)
                    attachment = MaybeDownscaleImageAttachment(attachment);

                TryAddAttachment(attachment);
            }
            catch (Exception ex)
            {
                AddBotMessage("Не удалось прикрепить файл: " + ex.Message);
            }
        }

        private bool TryAddAttachment(ChatAttachment attachment)
        {
            var error = ChatAttachment.ValidateBatch(_pendingAttachments, attachment);
            if (error != null)
            {
                AddBotMessage(error);
                return false;
            }

            _pendingAttachments.Add(attachment);
            RefreshAttachmentStrip();
            return true;
        }

        private void ClearPendingAttachments()
        {
            _pendingAttachments.Clear();
            RefreshAttachmentStrip();
        }

        private void RefreshAttachmentStrip()
        {
            AttachmentStrip.Children.Clear();
            if (_pendingAttachments.Count == 0)
            {
                AttachmentStrip.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            AttachmentStrip.Visibility = System.Windows.Visibility.Visible;
            for (var i = 0; i < _pendingAttachments.Count; i++)
            {
                var a = _pendingAttachments[i];
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xEE, 0xF4)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD5, 0xDE, 0xE8)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 4, 4, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    ToolTip = a.DisplayLabel
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = a.KindLabel + " · " + TruncateName(a.FileName, 22),
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x27, 0x44)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                });

                var remove = new Button
                {
                    Content = "×",
                    Padding = new Thickness(6, 0, 6, 0),
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0x6B, 0x7C)),
                    ToolTip = "Убрать"
                };
                remove.Click += (s, ev) =>
                {
                    if (_busy) return;
                    _pendingAttachments.Remove(a);
                    RefreshAttachmentStrip();
                };
                row.Children.Add(remove);
                chip.Child = row;
                AttachmentStrip.Children.Add(chip);
            }
        }

        private static string TruncateName(string name, int max)
        {
            if (string.IsNullOrEmpty(name) || name.Length <= max)
                return name ?? "файл";
            return name.Substring(0, max - 1) + "…";
        }

        private static ChatAttachment MaybeDownscaleImageAttachment(ChatAttachment attachment)
        {
            try
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(attachment.Data))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                }

                var scaled = DownscaleIfNeeded(bmp, 1280);
                // JPEG is much smaller than PNG for photos — fewer API failures on size limits.
                var jpeg = EncodeBitmapSourceAsJpeg(scaled, 75);
                if (jpeg == null || jpeg.Length == 0)
                    return attachment;

                var name = Path.GetFileNameWithoutExtension(attachment.FileName) + ".jpg";
                return ChatAttachment.FromBytes(name, "image/jpeg", jpeg);
            }
            catch
            {
                return attachment;
            }
        }

        private static BitmapSource DownscaleIfNeeded(BitmapSource source, int maxEdge)
        {
            if (source == null) return null;
            var w = source.PixelWidth;
            var h = source.PixelHeight;
            if (w <= maxEdge && h <= maxEdge)
                return source;

            var scale = Math.Min((double)maxEdge / w, (double)maxEdge / h);
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            return transformed;
        }

        private static byte[] EncodeBitmapSourceAsJpeg(BitmapSource source, int quality)
        {
            if (source == null) return null;
            // Ensure a format JPEG encoder accepts.
            BitmapSource ready = source;
            if (source.Format != PixelFormats.Bgr24 && source.Format != PixelFormats.Bgra32
                && source.Format != PixelFormats.Rgb24)
            {
                ready = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
                ready.Freeze();
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Max(40, Math.Min(95, quality)) };
            encoder.Frames.Add(BitmapFrame.Create(ready));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        private void EnableServerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_uiApp == null)
                {
                    AddBotMessage("Не удалось включить сервер: нет связи с Revit UI.");
                    return;
                }

                if (!SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Initialize(_uiApp);
                    SocketService.Instance.Start();
                }

                RefreshContextAndBanner();
                if (SocketService.Instance.IsRunning)
                    AddBotMessage("Сервер команд включён. Можно отправлять запросы.");
                else
                    AddBotMessage("Не удалось запустить сервер. Попробуйте Revit MCP Switch на ленте.");
            }
            catch (Exception ex)
            {
                AddBotMessage("Ошибка запуска: " + ex.Message);
            }
        }

        private void ConfirmOk_Click(object sender, RoutedEventArgs e)
        {
            ConfirmBar.Visibility = System.Windows.Visibility.Collapsed;
            _confirmTcs?.TrySetResult(true);
        }

        private void ConfirmCancel_Click(object sender, RoutedEventArgs e)
        {
            ConfirmBar.Visibility = System.Windows.Visibility.Collapsed;
            _confirmTcs?.TrySetResult(false);
        }

        private async Task StartRunAsync(
            string displayText,
            IList<ChatAttachment> attachments,
            string agentText = null,
            IReadOnlyList<string> toolProfiles = null)
        {
            if (_busy) return;

            _busy = true;
            SetBusyUi(true);
            AddUserMessage(displayText, attachments);
            RefreshContextAndBanner();

            var toAgent = string.IsNullOrWhiteSpace(agentText) ? displayText : agentText;
            _runCts = new CancellationTokenSource();
            var turnId = Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                var result = await _agent.RunAsync(
                        toAgent, BuildViewContextLine(), attachments, _runCts.Token, turnId, toolProfiles)
                    .ConfigureAwait(true);

                if (result.Cancelled)
                {
                    AddBotMessage("Остановлено.");
                    return;
                }

                var reply = result.Reply ?? "";
                if (result.DoneSummary != null && result.DoneSummary.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(reply);
                    sb.AppendLine();
                    sb.Append("Сделано: ");
                    sb.Append(string.Join(" · ", LocalAgentHost.CollapseDoneSummary(result.DoneSummary)));
                    reply = sb.ToString();
                }

                AddBotMessage(reply, turnId);
            }
            catch (OperationCanceledException)
            {
                AddBotMessage("Остановлено.");
            }
            catch (Exception ex)
            {
                AddBotMessage("Ошибка: " + ex.Message);
            }
            finally
            {
                _busy = false;
                SetBusyUi(false);
                ConfirmBar.Visibility = System.Windows.Visibility.Collapsed;
                RefreshContextAndBanner();
            }
        }

        private Task<bool> OnConfirmationRequested(PendingToolConfirmation pending)
        {
            var tcs = new TaskCompletionSource<bool>();
            _confirmTcs = tcs;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ConfirmText.Text = pending?.Summary
                    ?? "Подтвердите действие в модели.";
                ConfirmBar.Visibility = System.Windows.Visibility.Visible;
                SetStatus("Подтвердите", StatusTone.Warn);
            }));

            return tcs.Task;
        }

        private void OnAgentStatus(string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.IsNullOrWhiteSpace(status)) return;
                var tone = status.IndexOf("подтверж", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("ключ", StringComparison.OrdinalIgnoreCase) >= 0
                    ? StatusTone.Warn
                    : StatusTone.Busy;
                if (string.Equals(status, "Готов", StringComparison.OrdinalIgnoreCase))
                    tone = StatusTone.Ok;
                SetStatus(status, tone);
            }));
        }

        private void SetBusyUi(bool busy)
        {
            SendButton.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
            InputBox.IsEnabled = !busy;
            AttachButton.IsEnabled = !busy;
            NewChatButton.IsEnabled = true; // allow abort+reset even while running
            foreach (var child in ChipsPanel.Children)
            {
                if (child is Button chip)
                    chip.IsEnabled = !busy;
            }
        }

        private void AddUserMessage(string text, IList<ChatAttachment> attachments = null)
        {
            MessagesPanel.Children.Add(new ChatBubble(text, fromUser: true, attachments));
            ScrollToEnd();
        }

        private void AddBotMessage(string text, string turnId = null)
        {
            var bubble = new ChatBubble(text, fromUser: false, turnId: turnId);
            bubble.FeedbackSubmitted += OnBubbleFeedback;
            MessagesPanel.Children.Add(bubble);
            ScrollToEnd();
        }

        private void OnBubbleFeedback(object sender, FeedbackEventArgs e)
        {
            Core.Assistant.AssistantTurnLogger.WriteRatingPatch(e.TurnId, e.Rating, e.Reason, e.Comment);
            RefreshFeedbackBadge();
        }

        private void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ChatScroll.ScrollToEnd();
            }), DispatcherPriority.Background);
        }

        private string BuildViewContextLine()
        {
            try
            {
                var uidoc = _uiApp?.ActiveUIDocument;
                var doc = uidoc?.Document;
                var view = uidoc?.ActiveView;
                if (doc == null || view == null)
                    return "Документ: — · Вид: —";

                var levelName = "—";
                try
                {
                    if (view is ViewPlan plan && plan.GenLevel != null)
                        levelName = plan.GenLevel.Name;
                }
                catch
                {
                    // ignore
                }

                return $"Документ: {doc.Title} · Вид: {view.Name} ({view.ViewType}) · Уровень: {levelName}";
            }
            catch
            {
                return "Документ: — · Вид: —";
            }
        }

    }
}
