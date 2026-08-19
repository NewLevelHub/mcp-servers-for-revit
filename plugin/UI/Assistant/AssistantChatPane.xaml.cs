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
        /// <summary>Turn count after which the meter suggests starting a new thread.</summary>
        private const int LongThreadTurns = 20;

        private UIApplication _uiApp;
        private IAssistantHost _agent;
        private readonly List<ChatAttachment> _pendingAttachments = new List<ChatAttachment>();
        private ScenarioPreset _pendingPreset;
        private CancellationTokenSource _runCts;
        private TaskCompletionSource<bool> _confirmTcs;
        private TaskCompletionSource<AskUserAnswer> _askUserTcs;
        private AskUserBubble _activeAskBubble;
        private bool _busy;
        private bool _tutorMode;
        private bool _suppressTutorNotice;
        private PlanChecklistBubble _activePlanBubble;
        private ToolJournalBubble _activeJournalBubble;
        private ChatBubble _streamingBubble;
        private string _streamBuffer = "";
        private DateTime _lastStreamUiUtc = DateTime.MinValue;
        private string _currentTurnId;
        private string _currentUserText;
        private readonly Dictionary<string, string> _userTextByTurnId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public AssistantChatPane()
        {
            InitializeComponent();
            var settings = PluginSettingsStore.LoadSettings();
            _agent = AssistantHostFactory.Create(settings);
            Loaded += OnLoaded;
            _agent.StatusChanged += OnAgentStatus;
            _agent.ConfirmationRequested += OnConfirmationRequested;
            _agent.AskUserRequested += OnAskUserRequested;
            _agent.HistoryTrimmed += OnHistoryTrimmed;
            _agent.HistoryBudgetChanged += OnHistoryBudgetChanged;
            _agent.PlanChanged += OnPlanChanged;
            _agent.ModelEscalated += OnModelEscalated;
            _agent.ToolStepChanged += OnToolStepChanged;
            _agent.ReplyDelta += OnReplyDelta;
            BuildChips();
            RestoreTutorMode(settings);
            ShowWelcomeMessage();
            SetStatus("Готов", StatusTone.Ok);
        }

        /// <summary>
        /// REV-154: режим наставника переживает перезапуск Revit — новичок включает его один раз,
        /// а не каждое утро заново.
        /// </summary>
        private void RestoreTutorMode(ServiceSettings settings)
        {
            _tutorMode = settings != null && settings.AssistantTutorMode;
            _suppressTutorNotice = true;
            TutorModeToggle.IsChecked = _tutorMode;
            _suppressTutorNotice = false;
        }

        private void TutorModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            _tutorMode = TutorModeToggle.IsChecked == true;
            if (_suppressTutorNotice)
                return;

            try
            {
                var settings = PluginSettingsStore.LoadSettings();
                settings.AssistantTutorMode = _tutorMode;
                PluginSettingsStore.SaveSettings(settings);
            }
            catch
            {
                // Настройку не записали — на текущую сессию режим всё равно действует.
            }

            // Человек должен видеть в переписке, где он находится: молчаливая смена режима
            // выглядит как «ассистент вдруг перестал работать».
            AddBotMessage(TutorMode.NoticeFor(_tutorMode));
        }

        private void ShowWelcomeMessage()
        {
            AddBotMessage(
                "Напишите запрос обычным языком или выберите сценарий ниже.\n" +
                "Чип: клик — вставить и править, Ctrl+клик — сразу.\n" +
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

            ContextText.Text = SnapshotSessionContext().FormatForHeader();
            UpdateContextMeter(_agent.GetHistoryBudget());
            var running = SocketService.Instance.IsRunning;
            ServerBanner.Visibility = running ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            var settings = PluginSettingsStore.LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.AssistantCursorApiKey))
                SetStatus("Нужен Cursor key", StatusTone.Warn);
            else if (!running)
                SetStatus("Выключен", StatusTone.Warn);
            else if (!_busy)
                SetStatus("Готов", StatusTone.Ok);
        }

        private enum StatusTone { Ok, Busy, Warn }

        /// <summary>
        /// Caption behind the running clock, so the timer can re-render it every second
        /// without swallowing whatever phase the turn is in ("Думает…", "Проверка норм…").
        /// </summary>
        private string _busyStatusBase;

        private DateTime _busyStartedAt;
        private DispatcherTimer _busyTimer;

        private void SetStatus(string text, StatusTone tone)
        {
            // A turn can sit 30+ seconds inside the model with no tool call to show for
            // it, and a frozen caption reads as a hang. Keep a clock on it.
            if (tone == StatusTone.Busy)
            {
                _busyStatusBase = text;
                StartBusyClock();
                text = ComposeBusyStatus();
            }
            else
            {
                StopBusyClock();
            }

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

        private string ComposeBusyStatus()
        {
            var elapsed = DateTime.UtcNow - _busyStartedAt;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            return $"{_busyStatusBase} {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        }

        /// <summary>Runs from the first busy phase of a turn to the end, not per phase.</summary>
        private void StartBusyClock()
        {
            if (_busyTimer == null)
            {
                _busyTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1),
                };
                _busyTimer.Tick += (s, e) => StatusText.Text = ComposeBusyStatus();
            }

            if (!_busyTimer.IsEnabled)
            {
                _busyStartedAt = DateTime.UtcNow;
                _busyTimer.Start();
            }
        }

        private void StopBusyClock()
        {
            if (_busyTimer != null && _busyTimer.IsEnabled)
                _busyTimer.Stop();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshContextAndBanner();
            RefreshFeedbackBadge();
            FlushFeedbackInBackground();
            WarmUpEngine();
        }

        /// <summary>
        /// Starts the Node bridge while the architect is still typing, so the first
        /// message does not pay the ~15 s cold start. Errors surface on that message.
        /// </summary>
        private void WarmUpEngine()
        {
            var settings = PluginSettingsStore.LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.AssistantCursorApiKey))
                return;

            Task.Run(() =>
            {
                try { AssistantBridgeLauncher.EnsureRunning(settings); }
                catch { /* reported when the first turn runs */ }
            });
        }

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                try { _runCts?.Cancel(); } catch { /* ignore */ }
                try { _confirmTcs?.TrySetResult(false); } catch { /* ignore */ }
                try { _askUserTcs?.TrySetResult(new AskUserAnswer { Cancelled = true }); } catch { /* ignore */ }
                try { _activeAskBubble?.Cancel(); } catch { /* ignore */ }
            }

            StartNewChat(showNotice: true);
        }

        private void ExportFeedbackButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = Core.Assistant.FeedbackExporter.Export();
                if (result == null)
                {
                    AddBotMessage(
                        "Нет невыгруженных жалоб.\n\nСобранные ранее пакеты лежат в папке:\n"
                        + Core.Assistant.FeedbackExporter.GetPackagesDirectory());
                }
                else
                {
                    // Clipboard is STA-only, so it belongs here and not inside Export,
                    // which also runs on the background flush.
                    try { System.Windows.Clipboard.SetText(result.PackagePath); } catch { /* may be locked */ }
                    AddBotMessage(DescribeExport(result));
                }
                RefreshFeedbackBadge();
            }
            catch (Exception ex)
            {
                AddBotMessage("Ошибка выгрузки: " + ex.Message);
            }
        }

        private static string DescribeExport(Core.Assistant.FeedbackExportResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Собрал {result.Count} жалоб(ы) в один архив:");
            sb.AppendLine(result.PackagePath);

            if (result.Delivered)
            {
                sb.AppendLine();
                sb.AppendLine("Отправлено в общую папку — от вас больше ничего не нужно.");
            }
            else if (!string.IsNullOrEmpty(result.DeliveryError))
            {
                sb.AppendLine();
                sb.AppendLine("Общая папка сейчас недоступна (" + result.DeliveryError
                    + "). Архив остался на компьютере и уйдёт сам, когда папка появится.");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("Путь скопирован в буфер обмена — пришлите архив разработчику.");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Ships whatever is pending in the background. The architect is here to draw, not
        /// to remember to press an export button, so the panel does it on open and after
        /// every complaint — but only when a collection folder is configured.
        /// </summary>
        private void FlushFeedbackInBackground()
        {
            Task.Run(() =>
            {
                try { Core.Assistant.FeedbackExporter.TryAutoFlush(); }
                catch { /* never surfaces as a chat error */ }
            })
            .ContinueWith(
                _ => RefreshFeedbackBadge(),
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
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
            try { _askUserTcs?.TrySetResult(new AskUserAnswer { Cancelled = true }); } catch { /* ignore */ }
            _activeAskBubble = null;
            _activePlanBubble = null;
            _agent.ClearHistory();
            MessagesPanel.Children.Clear();
            ConfirmBar.Visibility = System.Windows.Visibility.Collapsed;
            _pendingPreset = null;
            InputBox.Clear();
            ClearPendingAttachments();
            // A fresh chat is exactly when the launchpad is useful again.
            _scenariosChosenByUser = false;
            SetScenariosExpanded(true);
            ShowWelcomeMessage();
            if (showNotice)
            {
                AddBotMessage("Новый диалог. История и журнал созданных элементов очищены.");
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
                    "Часть ранних сообщений сжата в сводку (длинный диалог). " +
                    "Последний запрос сохранён. Чтобы начать с нуля — «+ Новый».");
            }));
        }

        private void OnHistoryBudgetChanged(HistoryBudget budget)
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateContextMeter(budget)));
        }

        private void UpdateContextMeter(HistoryBudget budget)
        {
            if (budget == null)
                budget = _agent.GetHistoryBudget();

            // Cursor manages its own context window, so a "12 of 13" cap would be a lie.
            // Show the thread length and nudge towards "+ Новый" once it gets long.
            var turns = budget.UserTurns;
            ContextMeterText.Text = turns <= 0 ? "Новый диалог" : $"Вопросов: {turns}";

            var longThread = turns >= LongThreadTurns;
            ContextMeterText.Foreground = new SolidColorBrush(longThread
                ? System.Windows.Media.Color.FromRgb(0xE8, 0xA0, 0x5A)
                : System.Windows.Media.Color.FromRgb(0x7A, 0x9B, 0xB8));
            ContextMeterText.ToolTip = longThread
                ? "Диалог длинный. Если ассистент начал путаться — нажмите «+ Новый»."
                : "Вопросов в этом диалоге. «+ Новый» начинает разговор с чистого листа.";
        }

        /// <summary>
        /// Set once the architect clicks the header, so the auto-collapse below never
        /// overrides a choice they made themselves.
        /// </summary>
        private bool _scenariosChosenByUser;

        private void ScenariosToggle_Click(object sender, RoutedEventArgs e)
        {
            _scenariosChosenByUser = true;
            SetScenariosExpanded(ScenariosBody.Visibility != System.Windows.Visibility.Visible);
        }

        private void SetScenariosExpanded(bool expanded)
        {
            ScenariosBody.Visibility = expanded
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            ScenariosChevron.Text = expanded ? "⌄" : "›";
        }

        /// <summary>
        /// The chips are a launchpad for the first question; after that they only steal
        /// height from the answer. Fold them away once, leaving the header to bring them back.
        /// </summary>
        private void CollapseScenariosAfterFirstTurn()
        {
            if (_scenariosChosenByUser) return;
            SetScenariosExpanded(false);
        }

        private void BuildChips()
        {
            ChipsPanel.Children.Clear();
            foreach (var preset in ScenarioPresets.Pilot)
                ChipsPanel.Children.Add(MakeChipButton(preset));
        }

        private Button MakeChipButton(ScenarioPreset preset)
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

            var tip = (string.IsNullOrWhiteSpace(preset.Hint) ? preset.Label : preset.Hint)
                + "\nКлик — править · Ctrl+клик — сразу";
            var btn = new Button
            {
                Content = content,
                Style = (Style)FindResource("ChipButton"),
                Tag = preset,
                ToolTip = tip
            };
            btn.Click += Chip_Click;
            return btn;
        }

        private void Chip_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var preset = (sender as Button)?.Tag as ScenarioPreset;
            if (preset == null) return;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                RunPresetImmediate(preset);
                return;
            }

            _pendingPreset = preset;
            InputBox.Text = preset.Prompt ?? "";
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }

        private void RunPresetImmediate(ScenarioPreset preset)
        {
            if (preset == null || _busy) return;

            _pendingPreset = null;

            if (!string.IsNullOrWhiteSpace(preset.Hint))
                AddBotMessage(preset.Hint);

            var text = preset.Prompt;
            var attachments = _pendingAttachments.Count > 0 ? _pendingAttachments.ToList() : null;
            ClearPendingAttachments();

            if (AssistantNormAuditRouting.ShouldRunDirectNormAudit(preset.Id))
            {
                _ = StartNormAuditAsync(text);
                return;
            }

            _ = StartRunAsync(text, attachments, ScenarioPresets.BuildAgentMessage(preset, text), preset.Profiles);
        }

        private async Task StartNormAuditAsync(string displayText)
        {
            if (_busy) return;
            _busy = true;
            SetBusyUi(true);
            AddUserMessage(displayText, null);
            CollapseScenariosAfterFirstTurn();
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
            var pending = _pendingPreset;
            InputBox.Clear();
            ClearPendingAttachments();
            _pendingPreset = null;

            var display = string.IsNullOrEmpty(text) ? "Смотри вложение." : text;
            if (pending != null)
            {
                _ = StartRunAsync(
                    display,
                    attachments,
                    ScenarioPresets.BuildAgentMessage(pending, display),
                    pending.Profiles);
                return;
            }

            _ = StartRunAsync(display, attachments);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            try { _runCts?.Cancel(); } catch { /* ignore */ }
            try { _confirmTcs?.TrySetResult(false); } catch { /* ignore */ }
            try { _askUserTcs?.TrySetResult(new AskUserAnswer { Cancelled = true }); } catch { /* ignore */ }
            try { _activeAskBubble?.Cancel(); } catch { /* ignore */ }
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
            _activePlanBubble = null;
            _activeJournalBubble = null;
            _streamingBubble = null;
            _streamBuffer = "";
            _activeAskBubble = null;
            AddUserMessage(displayText, attachments);
            CollapseScenariosAfterFirstTurn();
            RefreshContextAndBanner();

            var toAgent = string.IsNullOrWhiteSpace(agentText) ? displayText : agentText;
            _runCts = new CancellationTokenSource();
            var turnId = Guid.NewGuid().ToString("N").Substring(0, 12);
            _currentTurnId = turnId;
            _currentUserText = displayText;
            _userTextByTurnId[turnId] = displayText;
            try
            {
                var result = await _agent.RunAsync(
                        toAgent, SnapshotSessionContext().FormatForPrompt(), attachments, _runCts.Token, turnId,
                        TutorMode.ResolveProfiles(_tutorMode, toolProfiles))
                    .ConfigureAwait(true);

                ClearStreamingBubble();

                if (result.Cancelled)
                {
                    AddBotMessage(
                        string.IsNullOrWhiteSpace(result.Reply) ? "Остановлено." : result.Reply,
                        turnId,
                        FormatModelMeta(result),
                        displayText);
                    return;
                }

                var reply = result.Reply ?? "";
                if (result.DoneSummary != null && result.DoneSummary.Count > 0
                    && reply.IndexOf("Успели:", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(reply);
                    sb.AppendLine();
                    sb.Append("Сделано: ");
                    sb.Append(string.Join(" · ", LocalAgentHost.CollapseDoneSummary(result.DoneSummary)));
                    reply = sb.ToString();
                }

                AddBotMessage(reply, turnId, FormatModelMeta(result), displayText);
            }
            catch (OperationCanceledException)
            {
                ClearStreamingBubble();
                AddBotMessage("Остановлено.", turnId, null, displayText);
            }
            catch (Exception ex)
            {
                ClearStreamingBubble();
                AddBotMessage(DescribeTurnFailure(ex), turnId, null, displayText);
            }
            finally
            {
                _busy = false;
                _currentTurnId = null;
                _currentUserText = null;
                _streamingBubble = null;
                SetBusyUi(false);
                ConfirmBar.Visibility = System.Windows.Visibility.Collapsed;
                _activeAskBubble = null;
                RefreshContextAndBanner();
            }
        }

        private void OnModelEscalated(string notice)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnModelEscalated(notice)));
                return;
            }

            if (string.IsNullOrWhiteSpace(notice))
                return;
            AddBotMessage(notice.Trim());
            SetStatus("Сильная модель…", StatusTone.Busy);
        }

        /// <summary>
        /// A dropped connection surfaced as the raw .NET text ("Сбой операции чтения,
        /// см. внутреннее исключение"), which tells an architect nothing about the one
        /// thing that matters: the turn may have already changed the model before it died.
        /// </summary>
        private static string DescribeTurnFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is IOException
                    || e is System.Net.Http.HttpRequestException
                    || e is System.Net.Sockets.SocketException
                    || e is System.Net.WebException)
                {
                    return "Связь с ассистентом оборвалась — проверьте интернет.\n" +
                           "Часть работы могла успеть выполниться. Напишите «продолжи»: " +
                           "он сверится с моделью, прежде чем что-то добавлять.";
                }
            }

            return "Ошибка: " + ex.Message;
        }

        private static string FormatModelMeta(AgentTurnResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.Model))
                return null;

            var total = result.PromptTokens + result.CompletionTokens;
            var tokens = total > 0
                ? $"{result.PromptTokens}+{result.CompletionTokens} tok"
                : null;
            var escalate = result.EscalatedToSmart ? " · escalate" : "";
            return tokens != null
                ? $"{result.Model} · {tokens}{escalate}"
                : $"{result.Model}{escalate}";
        }

        private Task<bool> OnConfirmationRequested(PendingToolConfirmation pending)
        {
            var tcs = new TaskCompletionSource<bool>();
            _confirmTcs = tcs;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var summary = EnrichConfirmSummary(pending);
                ConfirmText.Text = summary;
                var isDelete = IsDeleteConfirmation(pending);
                ConfirmOkButton.Content = isDelete ? "Удалить" : "Выполнить";
                ConfirmBar.Visibility = System.Windows.Visibility.Visible;
                SetStatus("Подтвердите", StatusTone.Warn);
            }));

            return tcs.Task;
        }

        private static bool IsDeleteConfirmation(PendingToolConfirmation pending)
        {
            if (pending == null) return false;
            var name = pending.ToolName ?? "";
            return name.Equals("delete_element", StringComparison.OrdinalIgnoreCase)
                   || ToolCatalog.RequiresConfirmation(name, pending.ArgumentsJson)
                      && !name.Equals("send_code_to_revit", StringComparison.OrdinalIgnoreCase);
        }

        private string EnrichConfirmSummary(PendingToolConfirmation pending)
        {
            if (pending == null)
                return "Подтвердите действие в модели.";

            var name = pending.ToolName ?? "";
            if (name.Equals("delete_element", StringComparison.OrdinalIgnoreCase)
                || (name.Equals("operate_element", StringComparison.OrdinalIgnoreCase)
                    && ToolCatalog.RequiresConfirmation(name, pending.ArgumentsJson)))
            {
                return DeleteConfirmSummary.Format(name, pending.ArgumentsJson, ResolveElementCategory);
            }

            return string.IsNullOrWhiteSpace(pending.Summary)
                ? "Подтвердите действие в модели."
                : pending.Summary;
        }

        private string ResolveElementCategory(string idText)
        {
            try
            {
                var doc = _uiApp?.ActiveUIDocument?.Document;
                if (doc == null || string.IsNullOrWhiteSpace(idText))
                    return null;
                if (!int.TryParse(idText.Trim(), out var idInt))
                    return null;
                var el = doc.GetElement(new ElementId(idInt));
                return el?.Category?.Name;
            }
            catch
            {
                return null;
            }
        }

        private Task<AskUserAnswer> OnAskUserRequested(PendingAskUser pending)
        {
            var tcs = new TaskCompletionSource<AskUserAnswer>();
            _askUserTcs = tcs;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var bubble = new AskUserBubble(pending);
                _activeAskBubble = bubble;
                bubble.Answered += answer =>
                {
                    _askUserTcs?.TrySetResult(answer ?? new AskUserAnswer { Cancelled = true });
                };
                MessagesPanel.Children.Add(bubble);
                ScrollToEnd();
                SetStatus("Ждёт ответ", StatusTone.Warn);
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
                    || status.IndexOf("Ждёт", StringComparison.OrdinalIgnoreCase) >= 0
                    ? StatusTone.Warn
                    : StatusTone.Busy;
                if (string.Equals(status, "Готов", StringComparison.OrdinalIgnoreCase))
                    tone = StatusTone.Ok;
                SetStatus(status, tone);
            }));
        }

        private void OnPlanChanged(AgentPlanSnapshot plan)
        {
            if (plan == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_activePlanBubble == null)
                {
                    _activePlanBubble = new PlanChecklistBubble(plan);
                    MessagesPanel.Children.Add(_activePlanBubble);
                    ScrollToEnd();
                }
                else
                {
                    _activePlanBubble.Apply(plan);
                }
            }));
        }

        private void OnToolStepChanged(ToolStepEvent ev)
        {
            if (ev == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Tool steps mean the text stream (if any) was thinking — discard it.
                ClearStreamingBubble();

                if (_activeJournalBubble == null)
                {
                    _activeJournalBubble = new ToolJournalBubble();
                    _activeJournalBubble.ElementIdClicked += SelectElementsInRevit;
                    MessagesPanel.Children.Add(_activeJournalBubble);
                }
                _activeJournalBubble.Apply(ev);
                ScrollToEnd();
            }));
        }

        private void OnReplyDelta(string cumulative)
        {
            _streamBuffer = cumulative ?? "";
            var now = DateTime.UtcNow;
            if ((now - _lastStreamUiUtc).TotalMilliseconds < 100 && _streamingBubble != null)
                return;
            _lastStreamUiUtc = now;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.IsNullOrEmpty(_streamBuffer))
                    return;

                if (_streamingBubble == null)
                {
                    _streamingBubble = new ChatBubble(
                        _streamBuffer,
                        fromUser: false,
                        turnId: _currentTurnId,
                        userRequestText: _currentUserText);
                    MessagesPanel.Children.Add(_streamingBubble);
                }
                else
                {
                    _streamingBubble.SetStreamingText(_streamBuffer);
                }
                ScrollToEnd();
            }));
        }

        private void ClearStreamingBubble()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ClearStreamingBubble));
                return;
            }

            if (_streamingBubble != null)
            {
                MessagesPanel.Children.Remove(_streamingBubble);
                _streamingBubble = null;
            }
            _streamBuffer = "";
        }

        private void SelectElementsInRevit(int elementId)
        {
            try
            {
                var uidoc = _uiApp?.ActiveUIDocument;
                if (uidoc == null) return;
                var id = new ElementId(elementId);
                if (uidoc.Document.GetElement(id) == null) return;
                uidoc.Selection.SetElementIds(new List<ElementId> { id });
                uidoc.ShowElements(id);
            }
            catch
            {
                // selection may fail outside a view
            }
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

        private void AddBotMessage(string text, string turnId = null, string metaFooter = null, string userRequestText = null)
        {
            var userText = userRequestText;
            if (userText == null && turnId != null)
                _userTextByTurnId.TryGetValue(turnId, out userText);

            var bubble = new ChatBubble(
                text,
                fromUser: false,
                turnId: turnId,
                metaFooter: metaFooter,
                userRequestText: userText);
            bubble.FeedbackSubmitted += OnBubbleFeedback;
            bubble.RetryRequested += OnBubbleRetry;
            bubble.EditRequested += OnBubbleEdit;
            MessagesPanel.Children.Add(bubble);
            ScrollToEnd();
        }

        private void OnBubbleFeedback(object sender, FeedbackEventArgs e)
        {
            // A rating without a turn behind it can only ever export as an empty
            // complaint, so refuse it here as well as hiding the buttons.
            if (e == null || string.IsNullOrWhiteSpace(e.TurnId))
                return;

            var ok = Core.Assistant.AssistantTurnLogger.WriteRatingPatch(
                e.TurnId, e.Rating, e.Reason, e.Comment, e.ShotPath);
            (sender as ChatBubble)?.ShowFeedbackResult(ok);
            RefreshFeedbackBadge();

            if (ok && e.Rating < 0)
                FlushFeedbackInBackground();
        }

        private void OnBubbleRetry(object sender, RetryEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.UserText) || _busy)
                return;
            _ = StartRunAsync(e.UserText, null);
        }

        private void OnBubbleEdit(object sender, RetryEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.UserText))
                return;
            InputBox.Text = e.UserText;
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }

        private void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ChatScroll.ScrollToEnd();
            }), DispatcherPriority.Background);
        }

        private AssistantSessionContext SnapshotSessionContext()
        {
            return AssistantSessionContext.Snapshot(_uiApp);
        }

    }
}
