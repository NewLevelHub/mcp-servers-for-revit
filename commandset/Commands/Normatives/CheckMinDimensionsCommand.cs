using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Normatives
{
    public class CheckMinDimensionsCommand : ExternalEventCommandBase
    {
        private CheckMinDimensionsEventHandler _handler => (CheckMinDimensionsEventHandler)Handler;

        public override string CommandName => "check_min_dimensions";

        public CheckMinDimensionsCommand(UIApplication uiApp)
            : base(new CheckMinDimensionsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                double? minBalconyWidthMm = parameters?["minBalconyWidthMm"]?.Value<double?>();
                double? minLoggiaWidthMm = parameters?["minLoggiaWidthMm"]?.Value<double?>();
                double? minLoggiaDepthMm = parameters?["minLoggiaDepthMm"]?.Value<double?>();
                double? minFirePathOutdoorWidthMm =
                    parameters?["minFirePathOutdoorWidthMm"]?.Value<double?>();
                double? minFirePierToOpeningMm = parameters?["minFirePierToOpeningMm"]?.Value<double?>();
                double? minFirePierBetweenOpeningsMm =
                    parameters?["minFirePierBetweenOpeningsMm"]?.Value<double?>();

                string mode = parameters?["mode"]?.Value<string>() ?? CheckMinDimensionsEventHandler.ModeReport;
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;
                string roomNameFilter = parameters?["roomNameFilter"]?.Value<string>() ?? string.Empty;
                bool includeCompliant = parameters?["includeCompliant"]?.Value<bool>() ?? false;
                bool checkFirePiers = parameters?["checkFirePiers"]?.Value<bool>() ?? true;

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
                    minBalconyWidthMm,
                    minLoggiaWidthMm,
                    minLoggiaDepthMm,
                    minFirePierToOpeningMm,
                    minFirePierBetweenOpeningsMm,
                    mode,
                    levelName,
                    roomNameFilter,
                    includeCompliant,
                    checkFirePiers,
                    highlightColor,
                    minFirePathOutdoorWidthMm);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Check minimum dimensions operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to check minimum dimensions: {ex.Message}");
            }
        }
    }
}
