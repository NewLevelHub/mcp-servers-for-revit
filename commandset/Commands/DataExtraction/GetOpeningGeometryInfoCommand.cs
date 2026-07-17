using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class GetOpeningGeometryInfoCommand : ExternalEventCommandBase
    {
        private OpeningGeometryInfoEventHandler _handler => (OpeningGeometryInfoEventHandler)Handler;

        public override string CommandName => "get_opening_geometry_info";

        public GetOpeningGeometryInfoCommand(UIApplication uiApp)
            : base(new OpeningGeometryInfoEventHandler(), uiApp)
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

                throw new TimeoutException("Get opening geometry info operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to collect opening geometry info: {ex.Message}");
            }
        }
    }
}
