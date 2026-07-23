using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.UI.Assistant
{
    public class AssistantDockablePaneProvider : IDockablePaneProvider
    {
        private readonly AssistantChatPane _pane;

        public AssistantDockablePaneProvider(AssistantChatPane pane)
        {
            _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        }

        public AssistantChatPane Pane => _pane;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _pane;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }
    }
}
