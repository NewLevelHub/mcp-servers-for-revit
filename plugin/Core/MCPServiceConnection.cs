using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class MCPServiceConnection : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                RibbonStatusManager.Initialize(commandData.Application);

                // 获取socket服务
                // Obtain socket service.
                SocketService service = SocketService.Instance;

                if (service.IsRunning)
                {
                    service.Stop();
                    TaskDialog.Show("revitMCP", "Close Server");
                }
                else
                {
                    service.Initialize(commandData.Application);
                    service.Start();

                    // Report what actually happened. Announcing "Open Server" unconditionally hid
                    // failed binds: the dialog said started, the ribbon said Offline, and the only
                    // trace of the real error was a tooltip nobody hovers.
                    if (service.IsRunning)
                    {
                        TaskDialog.Show("revitMCP", "Open Server");
                    }
                    else
                    {
                        TaskDialog.Show(
                            "revitMCP",
                            $"Не удалось открыть сервер на порту {service.Port}.\n\n" +
                            $"{service.LastStartError}\n\n" +
                            "Если порт занят — его держит прошлый сеанс плагина; перезапустите Revit.");
                    }
                }

                RibbonStatusManager.UpdateStatus(service.IsRunning);
                AssistantUiHost.Refresh();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
