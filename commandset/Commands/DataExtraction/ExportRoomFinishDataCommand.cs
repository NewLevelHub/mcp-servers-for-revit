using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class ExportRoomFinishDataCommand : ExternalEventCommandBase
    {
        private ExportRoomFinishDataEventHandler _handler => (ExportRoomFinishDataEventHandler)Handler;

        public override string CommandName => "export_room_finish_data";

        public ExportRoomFinishDataCommand(UIApplication uiApp)
            : base(new ExportRoomFinishDataEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                bool includeUnplacedRooms = parameters?["includeUnplacedRooms"]?.Value<bool>() ?? false;
                bool includeNotEnclosedRooms = parameters?["includeNotEnclosedRooms"]?.Value<bool>() ?? false;
                // Face-material extraction runs full spatial geometry per room and is the
                // expensive path on large models, so it is opt-in for the MCP tool.
                bool includeMaterials = parameters?["includeMaterials"]?.Value<bool>() ?? false;
                string levelName = parameters?["levelName"]?.Value<string>();
                int offset = parameters?["offset"]?.Value<int>() ?? 0;
                int limit = parameters?["limit"]?.Value<int>() ?? 0;

                _handler.SetParameters(
                    includeUnplacedRooms,
                    includeNotEnclosedRooms,
                    includeMaterials,
                    levelName,
                    offset,
                    limit);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException(
                    "Export room finish data operation timed out after 120 seconds. " +
                    "Use levelName or offset/limit pagination, or set includeMaterials=false.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to export room finish data: {ex.Message}");
            }
        }
    }
}
