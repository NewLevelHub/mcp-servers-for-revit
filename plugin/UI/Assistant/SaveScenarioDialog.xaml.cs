using System.Windows;

namespace revit_mcp_plugin.UI.Assistant
{
    /// <summary>REV-178: name + optional description for a scenario saved from chat.</summary>
    public partial class SaveScenarioDialog : Window
    {
        public string ScenarioName { get; private set; }
        public string ScenarioDescription { get; private set; }

        public SaveScenarioDialog(string suggestedName = null)
        {
            InitializeComponent();
            NameBox.Text = suggestedName ?? "";
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ErrorText.Text = "Название обязательно.";
                ErrorText.Visibility = Visibility.Visible;
                NameBox.Focus();
                return;
            }

            ScenarioName = name;
            ScenarioDescription = DescriptionBox.Text?.Trim();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
