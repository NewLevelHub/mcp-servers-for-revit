using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views;

public class ConfigureScheduleCommand : ExternalEventCommandBase
{
    private ConfigureScheduleEventHandler _handler => (ConfigureScheduleEventHandler)Handler;

    public override string CommandName => "configure_schedule";

    public ConfigureScheduleCommand(UIApplication uiApp)
        : base(new ConfigureScheduleEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?.ToObject<ConfigureScheduleInfo>()
                ?? throw new ArgumentException("configure_schedule: request body is required.");

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(60000))
                return _handler.ResultInfo;

            throw new TimeoutException("configure_schedule timed out after 60 seconds.");
        }
        catch (Exception ex)
        {
            throw new Exception($"configure_schedule failed: {ex.Message}", ex);
        }
    }
}
