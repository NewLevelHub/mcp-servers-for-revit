using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using revit_mcp_plugin.Configuration;

namespace revit_mcp_plugin.UI
{
    public partial class AssistantSettingsPage : Page
    {
        private static readonly string[] DefaultModelOptions =
        {
            "gpt-4o-mini",
            "gpt-4o",
            "gpt-4-turbo",
            "gpt-3.5-turbo",
            "o3-mini",
            "o1-mini",
        };

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

            var model = string.IsNullOrWhiteSpace(s.AssistantModel)
                ? "gpt-4o-mini"
                : s.AssistantModel.Trim();
            if (!string.IsNullOrWhiteSpace(s.AssistantModelSmart))
                model = s.AssistantModelSmart.Trim();

            var items = new List<string>(DefaultModelOptions);
            if (!items.Contains(model))
                items.Insert(0, model);

            ModelCombo.ItemsSource = items;
            ModelCombo.SelectedItem = model;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var s = PluginSettingsStore.LoadSettings();
            s.AssistantApiKey = ApiKeyBox.Password ?? "";
            s.AssistantApiBaseUrl = (BaseUrlBox.Text ?? "").Trim();
            s.AssistantModel = (ModelCombo.SelectedItem as string ?? "gpt-4o-mini").Trim();
            s.AssistantModelSmart = "";
            PluginSettingsStore.SaveSettings(s);
            SaveStatusText.Text = "Сохранено";
        }
    }
}
