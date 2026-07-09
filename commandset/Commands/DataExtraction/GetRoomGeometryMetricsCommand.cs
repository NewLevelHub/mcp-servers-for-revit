using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class GetRoomGeometryMetricsCommand : ExternalEventCommandBase
    {
        private RoomGeometryMetricsEventHandler _handler => (RoomGeometryMetricsEventHandler)Handler;

        public override string CommandName => "get_room_geometry_metrics";

        public GetRoomGeometryMetricsCommand(UIApplication uiApp)
            : base(new RoomGeometryMetricsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                bool includeUnplacedRooms = parameters?["includeUnplacedRooms"]?.Value<bool>() ?? false;
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;

                _handler.SetParameters(includeUnplacedRooms, levelName);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Get room geometry metrics operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to collect room geometry metrics: {ex.Message}");
            }
        }
    }
}
