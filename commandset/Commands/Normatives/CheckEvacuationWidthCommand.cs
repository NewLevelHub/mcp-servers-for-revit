using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Normatives
{
    public class CheckEvacuationWidthCommand : ExternalEventCommandBase
    {
        private CheckEvacuationWidthEventHandler _handler => (CheckEvacuationWidthEventHandler)Handler;

        public override string CommandName => "check_evacuation_width";

        public CheckEvacuationWidthCommand(UIApplication uiApp)
            : base(new CheckEvacuationWidthEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                double? minWidthMm = parameters?["minWidthMm"]?.Value<double?>();
                string mode = parameters?["mode"]?.Value<string>() ?? CheckEvacuationWidthEventHandler.ModeReport;
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;
                string roomNameFilter = parameters?["roomNameFilter"]?.Value<string>() ?? string.Empty;
                bool includeCompliant = parameters?["includeCompliant"]?.Value<bool>() ?? false;
                bool corridorOnly = parameters?["corridorOnly"]?.Value<bool>() ?? true;

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

                string highlightTarget = parameters?["highlightTarget"]?.Value<string>()
                    ?? CheckEvacuationWidthEventHandler.HighlightTargetViolations;

                int[] compliantHighlightColor = null;
                var compliantColorToken = parameters?["compliantHighlightColor"];
                if (compliantColorToken != null && compliantColorToken.Type == JTokenType.Object)
                {
                    compliantHighlightColor = new[]
                    {
                        compliantColorToken["r"]?.Value<int>() ?? 0,
                        compliantColorToken["g"]?.Value<int>() ?? 180,
                        compliantColorToken["b"]?.Value<int>() ?? 0
                    };
                }

                _handler.SetParameters(
                    minWidthMm,
                    mode,
                    levelName,
                    roomNameFilter,
                    includeCompliant,
                    corridorOnly,
                    highlightColor,
                    highlightTarget,
                    compliantHighlightColor);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Check evacuation width operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to check evacuation width: {ex.Message}");
            }
        }
    }
}
