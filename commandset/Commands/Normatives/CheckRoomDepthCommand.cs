using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Normatives
{
    public class CheckRoomDepthCommand : ExternalEventCommandBase
    {
        private CheckRoomDepthEventHandler _handler => (CheckRoomDepthEventHandler)Handler;

        public override string CommandName => "check_room_depth";

        public CheckRoomDepthCommand(UIApplication uiApp)
            : base(new CheckRoomDepthEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                double? minDepthMm = parameters?["minDepthMm"]?.Value<double?>();
                double? maxDepthMm = parameters?["maxDepthMm"]?.Value<double?>();
                string mode = parameters?["mode"]?.Value<string>() ?? CheckRoomDepthEventHandler.ModeReport;
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;
                string roomNameFilter = parameters?["roomNameFilter"]?.Value<string>() ?? string.Empty;
                bool includeCompliant = parameters?["includeCompliant"]?.Value<bool>() ?? false;

                int[] highlightColor = null;
                var colorToken = parameters?["highlightColor"];
                if (colorToken != null && colorToken.Type == JTokenType.Object)
                {
                    highlightColor = new[]
                    {
                        colorToken["r"]?.Value<int>() ?? 255,
                        colorToken["g"]?.Value<int>() ?? 0,
                        colorToken["b"]?.Value<int>() ?? 0
                    };
                }

                _handler.SetParameters(
                    minDepthMm,
                    maxDepthMm,
                    mode,
                    levelName,
                    roomNameFilter,
                    includeCompliant,
                    highlightColor);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Check room depth operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to check room depth: {ex.Message}");
            }
        }
    }
}
