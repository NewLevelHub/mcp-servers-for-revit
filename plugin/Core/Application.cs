using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB.Events;
using System.Reflection;
using System.Windows.Media.Imaging;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        private UIControlledApplication _application;
        private UIApplication _uiApp;
        private string _lastActiveDocumentTitle;

        public Result OnStartup(UIControlledApplication application)
        {
            _application = application;

            AssistantUiHost.Register(application);

            RibbonPanel mcpPanel = application.CreateRibbonPanel(RibbonStatusManager.PanelName);

            PushButtonData assistantButtonData = new PushButtonData(
                "ID_EXCMD_SHOW_ASSISTANT",
                "AI-\r\nассистент",
                Assembly.GetExecutingAssembly().Location,
                "revit_mcp_plugin.Core.ShowAssistantCommand");
            assistantButtonData.ToolTip = "Открыть панель AI-ассистента внутри Revit";
            assistantButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/ai-16.png", UriKind.RelativeOrAbsolute));
            assistantButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/ai-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(assistantButtonData);

            PushButtonData pushButtonData = new PushButtonData("ID_EXCMD_TOGGLE_REVIT_MCP", "Revit MCP\r\n Switch",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.MCPServiceConnection");
            pushButtonData.ToolTip = "Open / Close mcp server";
            pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/mcp-16.png", UriKind.RelativeOrAbsolute));
            pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/mcp-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(pushButtonData);

            TextBoxData statusIndicatorData = new TextBoxData("ID_MCP_STATUS_INDICATOR");
            statusIndicatorData.Name = RibbonStatusManager.StatusTextBoxName;
            statusIndicatorData.ToolTip = "MCP: Connected / Reconnecting / Offline";
            TextBox statusIndicator = mcpPanel.AddItem(statusIndicatorData) as TextBox;
            if (statusIndicator != null)
            {
                statusIndicator.Value = "Off";
                RibbonStatusManager.RegisterStatusTextBox(statusIndicator);
            }

            PushButtonData mcp_settings_pushButtonData = new PushButtonData("ID_EXCMD_MCP_SETTINGS", "Настройки",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.Settings");
            mcp_settings_pushButtonData.ToolTip = "Настройки MCP";
            mcp_settings_pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-16.png", UriKind.RelativeOrAbsolute));
            mcp_settings_pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(mcp_settings_pushButtonData);

            application.Idling += OnIdlingForAutoStart;

            application.ControlledApplication.DocumentSaved += OnDocumentSaved;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            application.ControlledApplication.DocumentClosed += OnDocumentClosed;

            // Dismisses warnings raised while a tool call is running, so a modal
            // dialog nobody can click cannot stall the rest of the turn. Armed
            // only around agent commands — see AgentFailureGuard.
            application.ControlledApplication.FailuresProcessing +=
                AgentFailureGuard.OnFailuresProcessing;

            return Result.Succeeded;
        }

        private void OnDocumentSaved(object sender, DocumentSavedEventArgs e)
        {
            ModelCacheInvalidator.InvalidateProject(e.Document.Title);
        }

        private void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            ModelCacheInvalidator.InvalidateAll();
        }

        private void OnDocumentClosed(object sender, DocumentClosedEventArgs e)
        {
            ModelCacheInvalidator.InvalidateAll();
        }

        private void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            var document = e.Document;
            if (document == null)
                return;

            var title = document.Title;
            if (
                !string.IsNullOrEmpty(_lastActiveDocumentTitle) &&
                !string.Equals(_lastActiveDocumentTitle, title, StringComparison.Ordinal)
            )
            {
                ModelCacheInvalidator.InvalidateAll();
            }

            _lastActiveDocumentTitle = title;
            AssistantUiHost.Refresh();
        }

        private void OnIdlingForAutoStart(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            _application.Idling -= OnIdlingForAutoStart;

            var uiApp = sender as UIApplication;
            if (uiApp == null)
                return;

            _uiApp = uiApp;
            RibbonStatusManager.Initialize(uiApp);
            RibbonStatusManager.UpdateStatus(SocketService.Instance.IsRunning);
            uiApp.ViewActivated += OnViewActivated;
            AssistantUiHost.Attach(uiApp);
            RefreshRibbonCatalog(uiApp);

            if (!PluginSettingsStore.GetAutoStartOnLaunch() || SocketService.Instance.IsRunning)
            {
                AssistantUiHost.Refresh();
                return;
            }

            try
            {
                SocketService.Instance.Initialize(uiApp);
                SocketService.Instance.Start();
            }
            catch
            {
                // Auto-start failure is non-fatal; user can start manually via Switch.
            }

            AssistantUiHost.Refresh();
        }

        /// <summary>
        /// Молча снимает список кнопок ленты в файл (REV-150 → REV-151).
        ///
        /// Ассистент должен отвечать «где эта кнопка» по факту, а не по памяти: у пользователя
        /// своя версия Revit, свой язык интерфейса и свои аддоны. Ничего в интерфейсе при этом
        /// не появляется — сканер работает один раз при запуске и молчит.
        /// </summary>
        private static void RefreshRibbonCatalog(UIApplication uiApp)
        {
            try
            {
                var version = uiApp.Application.VersionNumber;
                var language = uiApp.Application.Language.ToString();
                var catalog = Tutor.RibbonScanner.Scan(version, language);
                if (catalog != null)
                    Tutor.RibbonScanner.Save(catalog, version, language);
            }
            catch
            {
                // Каталог — удобство, а не условие работы плагина. Не смогли снять — молчим.
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdlingForAutoStart;
            application.ControlledApplication.DocumentSaved -= OnDocumentSaved;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
            application.ControlledApplication.FailuresProcessing -=
                AgentFailureGuard.OnFailuresProcessing;
            if (_uiApp != null)
            {
                _uiApp.ViewActivated -= OnViewActivated;
            }

            try
            {
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch { }

            // Otherwise the Node bridge outlives Revit and leaks a process per session.
            try
            {
                Assistant.AssistantBridgeLauncher.Stop();
            }
            catch { }

            return Result.Succeeded;
        }
    }
}
