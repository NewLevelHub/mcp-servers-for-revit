using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Core;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI.Assistant
{
    public partial class AssistantChatPane : UserControl
    {
        private UIApplication _uiApp;
        private readonly LocalAgentHost _agent = new LocalAgentHost();
        private CancellationTokenSource _runCts;
        private TaskCompletionSource<bool> _confirmTcs;
        private bool _busy;

        public AssistantChatPane()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            _agent.StatusChanged += OnAgentStatus;
            _agent.ConfirmationRequested += OnConfirmationRequested;
            BuildChips();
            AddBotMessage(
                "Напишите запрос обычным языком или выберите сценарий ниже.\n" +
                "Пример: «Проверь этаж и подпиши нарушения».");
            SetStatus("Готов", StatusTone.Ok);
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
                    ToolTip = preset.Label
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
            // Auto-send for one-click pilot UX
            _ = StartRunAsync(preset.Prompt);
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var text = (InputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text) || _busy) return;
            InputBox.Clear();
            _ = StartRunAsync(text);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            try { _runCts?.Cancel(); } catch { /* ignore */ }
            try { _confirmTcs?.TrySetResult(false); } catch { /* ignore */ }
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SendButton_Click(sender, e);
            }
            if (e.Key == Key.Escape)
            {
                CancelButton_Click(sender, e);
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

        private async Task StartRunAsync(string userText)
        {
            if (_busy) return;
            _busy = true;
            SetBusyUi(true);
            AddUserMessage(userText);
            RefreshContextAndBanner();

            _runCts = new CancellationTokenSource();
            try
            {
                var result = await _agent.RunAsync(userText, BuildViewContextLine(), _runCts.Token)
                    .ConfigureAwait(true);

                var reply = result.Reply ?? "";
                if (result.DoneSummary != null && result.DoneSummary.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(reply);
                    sb.AppendLine();
                    sb.Append("Сделано: ");
                    sb.Append(string.Join(" · ", result.DoneSummary));
                    reply = sb.ToString();
                }

                AddBotMessage(reply);
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
            foreach (Button chip in ChipsPanel.Children)
                chip.IsEnabled = !busy;
        }

        private void AddUserMessage(string text)
        {
            MessagesPanel.Children.Add(new ChatBubble(text, fromUser: true));
            ScrollToEnd();
        }

        private void AddBotMessage(string text)
        {
            MessagesPanel.Children.Add(new ChatBubble(text, fromUser: false));
            ScrollToEnd();
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
