using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// Assistant settings. Cursor is the only engine (REV-155) — there is no
    /// backend switch and no OpenAI fields any more.
    /// </summary>
    public partial class AssistantSettingsPage : Page
    {
        // Ids verified against Cursor.models.list(); see CursorModelCatalog.
        private static readonly string[] CursorModelLabels =
            CursorModelCatalog.Entries.Select(e => e.Label).ToArray();

        private static readonly string[] CursorModelIds =
            CursorModelCatalog.Entries.Select(e => e.Id).ToArray();

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6B, 0x4A));
        private static readonly Brush ErrBrush = new SolidColorBrush(Color.FromRgb(0xB3, 0x2D, 0x2D));

        public AssistantSettingsPage()
        {
            InitializeComponent();
            LoadFromStore();
        }

        public void Reload()
        {
            LoadFromStore();
            SaveStatusText.Text = "";
        }

        private void LoadFromStore()
        {
            var s = PluginSettingsStore.LoadSettings();

            CursorApiKeyBox.Password = s.AssistantCursorApiKey ?? "";
            NodePathBox.Text = s.AssistantNodePath ?? "";
            BridgePortBox.Text = (s.AssistantBridgePort > 0 ? s.AssistantBridgePort : 8790).ToString();
            FeedbackAuthorBox.Text = s.AssistantFeedbackAuthor ?? "";
            FeedbackDropDirBox.Text = s.AssistantFeedbackDropDir ?? "";

            // Normalize first: stored "auto-smart:*" ids are rejected by the Cursor API.
            var cursorModel = CursorModelCatalog.Normalize(s.AssistantCursorModel);
            var cursorIdx = Array.IndexOf(CursorModelIds, cursorModel);
            if (cursorIdx < 0)
            {
                // Unknown id from a newer catalog — keep it selectable rather than silently swapping it.
                var labels = new List<string>(CursorModelLabels) { cursorModel };
                CursorModelCombo.ItemsSource = labels;
                CursorModelCombo.SelectedIndex = labels.Count - 1;
            }
            else
            {
                CursorModelCombo.ItemsSource = CursorModelLabels;
                CursorModelCombo.SelectedIndex = cursorIdx;
            }
        }

        private string SelectedCursorModelId()
        {
            var i = CursorModelCombo.SelectedIndex;
            if (i >= 0 && i < CursorModelIds.Length)
                return CursorModelIds[i];
            return CursorModelCatalog.Normalize(CursorModelCombo.SelectedItem as string);
        }

        private ServiceSettings CollectSettings()
        {
            var s = PluginSettingsStore.LoadSettings();
            s.AssistantBackend = "cursor";
            s.AssistantCursorApiKey = CursorApiKeyBox.Password ?? "";
            s.AssistantCursorModel = SelectedCursorModelId();
            s.AssistantNodePath = (NodePathBox.Text ?? "").Trim();
            s.AssistantFeedbackAuthor = (FeedbackAuthorBox.Text ?? "").Trim();
            s.AssistantFeedbackDropDir = (FeedbackDropDirBox.Text ?? "").Trim();

            int port;
            if (int.TryParse((BridgePortBox.Text ?? "").Trim(), out port) && port > 0 && port < 65536)
                s.AssistantBridgePort = port;

            return s;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var s = CollectSettings();
            PluginSettingsStore.SaveSettings(s);
            BridgePortBox.Text = s.AssistantBridgePort.ToString();

            SetStatus("Сохранено", ok: true);
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            var s = CollectSettings();
            PluginSettingsStore.SaveSettings(s);

            TestButton.IsEnabled = false;
            SetStatus("Проверяю…", ok: true);

            try
            {
                // Starting the bridge is the honest test: it validates Node, paths and the key.
                // Stop first — EnsureRunning keeps a live process whose settings did not change,
                // so a bridge that has gone sour (Cursor answering "Authentication error" on
                // every turn) would otherwise survive until Revit itself is closed.
                await Task.Run(() =>
                {
                    AssistantBridgeLauncher.Stop();
                    AssistantBridgeLauncher.EnsureRunning(s);
                }).ConfigureAwait(true);
                SetStatus("Движок перезапущен, ассистент готов", ok: true);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, ok: false);
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        private void SetStatus(string text, bool ok)
        {
            SaveStatusText.Foreground = ok ? OkBrush : ErrBrush;
            SaveStatusText.Text = text;
        }
    }
}
