using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Views;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views;

public class GetScheduleDefinitionCommand : ExternalEventCommandBase
{
    private GetScheduleDefinitionEventHandler _handler => (GetScheduleDefinitionEventHandler)Handler;

    public override string CommandName => "get_schedule_definition";

    public GetScheduleDefinitionCommand(UIApplication uiApp)
        : base(new GetScheduleDefinitionEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            long? scheduleId = parameters?["scheduleId"]?.Value<long?>();
            string scheduleUniqueId = parameters?["scheduleUniqueId"]?.Value<string>();
            string scheduleName = parameters?["scheduleName"]?.Value<string>();

            _handler.SetParameters(scheduleId, scheduleUniqueId, scheduleName);

            if (RaiseAndWaitForCompletion(120000))
                return _handler.ResultInfo;

            throw new TimeoutException("Get schedule definition timed out after 120 seconds");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to get schedule definition: {ex.Message}", ex);
        }
    }
}
