using System;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Core.Assistant;
using revit_mcp_plugin.UI.Assistant;

namespace revit_mcp_plugin.Core
{
    /// <summary>Holds the dockable chat pane instance registered at startup.</summary>
    public static class AssistantUiHost
    {
        private static AssistantChatPane _pane;
        private static AssistantDockablePaneProvider _provider;

        public static AssistantChatPane Pane => _pane;

        public static void Register(UIControlledApplication application)
        {
            if (_pane != null)
                return;

            _pane = new AssistantChatPane();
            _provider = new AssistantDockablePaneProvider(_pane);
            var paneId = new DockablePaneId(AssistantPaneIds.PaneGuid);
            application.RegisterDockablePane(paneId, AssistantPaneIds.PaneTitle, _provider);
        }

        public static void Attach(UIApplication uiApp)
        {
            _pane?.AttachUiApplication(uiApp);
        }

        public static void Refresh()
        {
            _pane?.RefreshContextAndBanner();
        }
    }
}
