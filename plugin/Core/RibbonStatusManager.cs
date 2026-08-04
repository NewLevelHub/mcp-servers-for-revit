using System.Linq;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.Core
{
    public enum McpLinkStatus
    {
        Offline,
        Reconnecting,
        Connected
    }

    public static class RibbonStatusManager
    {
        public const string PanelName = "Revit MCP Plugin";
        public const string StatusTextBoxName = "MCPStatusIndicator";

        private static UIApplication _uiApp;
        private static TextBox _statusTextBox;
        private static McpLinkStatus _status = McpLinkStatus.Offline;
        private static string _lastError;

        public static void RegisterStatusTextBox(TextBox statusTextBox)
        {
            _statusTextBox = statusTextBox;
        }

        public static void Initialize(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        public static McpLinkStatus CurrentStatus => _status;

        /// <summary>
        /// Legacy helper: running listener without a live Cursor client is shown as Connected
        /// until the first client disconnects (then Reconnecting).
        /// </summary>
        public static void UpdateStatus(bool isRunning)
        {
            UpdateStatus(isRunning ? McpLinkStatus.Connected : McpLinkStatus.Offline);
        }

        public static void UpdateStatus(McpLinkStatus status, string lastError = null)
        {
            _status = status;
            if (lastError != null)
                _lastError = lastError;
            if (status == McpLinkStatus.Connected)
                _lastError = null;

            var statusBox = _statusTextBox ?? FindStatusTextBox();
            if (statusBox == null)
                return;

            try
            {
                switch (status)
                {
                    case McpLinkStatus.Connected:
                        statusBox.Value = "Connected";
                        statusBox.ToolTip = "MCP: Cursor подключён (или сервер готов к подключению)";
                        break;
                    case McpLinkStatus.Reconnecting:
                        statusBox.Value = "Reconnecting";
                        statusBox.ToolTip = string.IsNullOrEmpty(_lastError)
                            ? "MCP: сервер слушает порт, Cursor переподключается…"
                            : "MCP: " + _lastError;
                        break;
                    default:
                        statusBox.Value = "Offline";
                        statusBox.ToolTip = string.IsNullOrEmpty(_lastError)
                            ? "MCP: сервер выключен — нажмите Open Server"
                            : "MCP Offline: " + _lastError;
                        break;
                }
            }
            catch
            {
                // Ribbon updates from a background client thread may race; ignore.
            }
        }

        private static TextBox FindStatusTextBox()
        {
            if (_uiApp == null)
                return null;

            var panel = _uiApp.GetRibbonPanels(PanelName).FirstOrDefault();
            if (panel == null)
                return null;

            return panel.GetItems()
                .OfType<TextBox>()
                .FirstOrDefault(tb => tb.Name == StatusTextBoxName);
        }
    }
}
