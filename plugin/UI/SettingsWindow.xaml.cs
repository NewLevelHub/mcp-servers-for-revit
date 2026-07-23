using System.Windows;
using System.Windows.Controls;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// Settings.xaml 的交互逻辑
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private CommandSetSettingsPage commandSetPage;
        private AssistantSettingsPage assistantPage;
        private bool isInitialized = false;

        public SettingsWindow()
        {
            InitializeComponent();

            commandSetPage = new CommandSetSettingsPage();
            assistantPage = new AssistantSettingsPage();

            ContentFrame.Navigate(commandSetPage);

            Loaded += (sender, args) =>
            {
                commandSetPage.ReloadCommandSets();
                assistantPage.Reload();
            };

            isInitialized = true;
        }

        private void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isInitialized) return;

            if (NavListBox.SelectedItem == CommandSetItem)
            {
                ContentFrame.Navigate(commandSetPage);
            }
            else if (NavListBox.SelectedItem == AssistantItem)
            {
                assistantPage.Reload();
                ContentFrame.Navigate(assistantPage);
            }
        }
    }
}
