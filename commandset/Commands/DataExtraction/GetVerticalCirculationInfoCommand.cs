using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class GetVerticalCirculationInfoCommand : ExternalEventCommandBase
    {
        private VerticalCirculationInfoEventHandler _handler =>
            (VerticalCirculationInfoEventHandler)Handler;

        public override string CommandName => "get_vertical_circulation_info";

        public GetVerticalCirculationInfoCommand(UIApplication uiApp)
            : base(new VerticalCirculationInfoEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;
                _handler.SetParameters(levelName);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Get vertical circulation info operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to collect vertical circulation info: {ex.Message}");
            }
        }
    }
}
