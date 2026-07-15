using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views;

public class CreateFloorExplicationCommand : ExternalEventCommandBase
{
    private CreateFloorExplicationEventHandler _handler => (CreateFloorExplicationEventHandler)Handler;

    public override string CommandName => "create_floor_explication";

    public CreateFloorExplicationCommand(UIApplication uiApp)
        : base(new CreateFloorExplicationEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?["explication"]?.ToObject<FloorExplicationCreationInfo>()
                ?? parameters?.ToObject<FloorExplicationCreationInfo>()
                ?? new FloorExplicationCreationInfo();

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(180000))
                return _handler.ResultInfo;

            throw new TimeoutException("Create floor экспликация timed out after 180 seconds");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to create floor экспликация: {ex.Message}", ex);
        }
    }
}
