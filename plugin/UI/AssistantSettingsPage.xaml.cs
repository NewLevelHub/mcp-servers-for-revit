using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using revit_mcp_plugin.Configuration;

namespace revit_mcp_plugin.UI
{
    public partial class AssistantSettingsPage : Page
    {
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
            ApiKeyBox.Password = s.AssistantApiKey ?? "";
            BaseUrlBox.Text = string.IsNullOrWhiteSpace(s.AssistantApiBaseUrl)
                ? "https://api.openai.com/v1"
                : s.AssistantApiBaseUrl;
            ModelBox.Text = string.IsNullOrWhiteSpace(s.AssistantModel)
                ? "gpt-4o-mini"
                : s.AssistantModel;
            TemperatureBox.Text = s.AssistantTemperature.ToString("0.##", CultureInfo.InvariantCulture);
            MaxTokensBox.Text = s.AssistantMaxTokens.HasValue && s.AssistantMaxTokens.Value > 0
                ? s.AssistantMaxTokens.Value.ToString(CultureInfo.InvariantCulture)
                : "";
            RequireConfirmCheckBox.IsChecked = s.AssistantRequireConfirmations;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var s = PluginSettingsStore.LoadSettings();
            s.AssistantApiKey = ApiKeyBox.Password ?? "";
            s.AssistantApiBaseUrl = (BaseUrlBox.Text ?? "").Trim();
            s.AssistantModel = (ModelBox.Text ?? "").Trim();
            s.AssistantTemperature = ParseTemperature(TemperatureBox.Text);
            s.AssistantMaxTokens = ParseMaxTokens(MaxTokensBox.Text);
            s.AssistantRequireConfirmations = RequireConfirmCheckBox.IsChecked == true;
            PluginSettingsStore.SaveSettings(s);
            SaveStatusText.Text = "Сохранено";
        }

        private static double ParseTemperature(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                return 0;
            if (t < 0) return 0;
            if (t > 2) return 2;
            return t;
        }

        private static int? ParseMaxTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
                return null;
            return n;
        }
    }
}
