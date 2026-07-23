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
            RequireConfirmCheckBox.IsChecked = s.AssistantRequireConfirmations;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var s = PluginSettingsStore.LoadSettings();
            s.AssistantApiKey = ApiKeyBox.Password ?? "";
            s.AssistantApiBaseUrl = (BaseUrlBox.Text ?? "").Trim();
            s.AssistantModel = (ModelBox.Text ?? "").Trim();
            s.AssistantRequireConfirmations = RequireConfirmCheckBox.IsChecked == true;
            PluginSettingsStore.SaveSettings(s);
            SaveStatusText.Text = "Сохранено";
        }
    }
}
