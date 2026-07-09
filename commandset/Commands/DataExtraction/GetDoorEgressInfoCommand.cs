using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class GetDoorEgressInfoCommand : ExternalEventCommandBase
    {
        private DoorEgressInfoEventHandler _handler => (DoorEgressInfoEventHandler)Handler;

        public override string CommandName => "get_door_egress_info";

        public GetDoorEgressInfoCommand(UIApplication uiApp)
            : base(new DoorEgressInfoEventHandler(), uiApp)
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

                throw new TimeoutException("Get door egress info operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to collect door egress info: {ex.Message}");
            }
        }
    }
}
