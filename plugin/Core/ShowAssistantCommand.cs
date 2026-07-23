using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class ShowAssistantCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var paneId = new DockablePaneId(AssistantPaneIds.PaneGuid);
                var pane = commandData.Application.GetDockablePane(paneId);
                if (pane == null)
                {
                    message = "Панель AI-ассистента не зарегистрирована.";
                    return Result.Failed;
                }

                if (pane.IsShown())
                    pane.Hide();
                else
                    pane.Show();

                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
