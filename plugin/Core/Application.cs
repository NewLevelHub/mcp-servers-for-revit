using System;
using Autodesk.Revit.UI;
using System.Reflection;
using System.Windows.Media.Imaging;
using revit_mcp_plugin.Configuration;

namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        private UIControlledApplication _application;

        public Result OnStartup(UIControlledApplication application)
        {
            _application = application;
            RibbonPanel mcpPanel = application.CreateRibbonPanel(RibbonStatusManager.PanelName);

            PushButtonData pushButtonData = new PushButtonData("ID_EXCMD_TOGGLE_REVIT_MCP", "Revit MCP\r\n Switch",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.MCPServiceConnection");
            pushButtonData.ToolTip = "Open / Close mcp server";
            pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-16.png", UriKind.RelativeOrAbsolute));
            pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(pushButtonData);

            TextBoxData statusIndicatorData = new TextBoxData("ID_MCP_STATUS_INDICATOR");
            statusIndicatorData.Name = RibbonStatusManager.StatusTextBoxName;
            statusIndicatorData.ToolTip = "MCP server connection status";
            TextBox statusIndicator = mcpPanel.AddItem(statusIndicatorData) as TextBox;
            if (statusIndicator != null)
            {
                statusIndicator.Value = "Off";
                RibbonStatusManager.RegisterStatusTextBox(statusIndicator);
            }

            PushButtonData mcp_settings_pushButtonData = new PushButtonData("ID_EXCMD_MCP_SETTINGS", "Settings",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.Settings");
            mcp_settings_pushButtonData.ToolTip = "MCP Settings";
            mcp_settings_pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-16.png", UriKind.RelativeOrAbsolute));
            mcp_settings_pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(mcp_settings_pushButtonData);

            application.Idling += OnIdlingForAutoStart;

            return Result.Succeeded;
        }

        private void OnIdlingForAutoStart(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            _application.Idling -= OnIdlingForAutoStart;

            var uiApp = sender as UIApplication;
            if (uiApp == null)
                return;

            RibbonStatusManager.Initialize(uiApp);
            RibbonStatusManager.UpdateStatus(SocketService.Instance.IsRunning);

            if (!PluginSettingsStore.GetAutoStartOnLaunch() || SocketService.Instance.IsRunning)
                return;

            try
            {
                SocketService.Instance.Initialize(uiApp);
                SocketService.Instance.Start();
            }
            catch
            {
                // Auto-start failure is non-fatal; user can start manually via Switch.
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdlingForAutoStart;

            try
            {
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch { }

            return Result.Succeeded;
        }
    }
}
