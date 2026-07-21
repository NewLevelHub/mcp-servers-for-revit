using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Services.Architecture;

namespace RevitMCPCommandSet.Commands.Architecture;

/// <summary>
/// Create a floor opening or vertical shaft (REV-85).
/// </summary>
public class CreateFloorOpeningCommand : ExternalEventCommandBase
{
    private CreateFloorOpeningEventHandler _handler => (CreateFloorOpeningEventHandler)Handler;

    public override string CommandName => "create_floor_opening";

    public CreateFloorOpeningCommand(UIApplication uiApp)
        : base(new CreateFloorOpeningEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            List<FloorOpeningCreationInfo> data =
                parameters["data"]?.ToObject<List<FloorOpeningCreationInfo>>();
            if (data == null || data.Count == 0)
                throw new ArgumentNullException(nameof(data), "No floor opening data provided");

            _handler.SetParameters(data);

            if (RaiseAndWaitForCompletion(30000))
            {
                return _handler.Result;
            }

            throw new TimeoutException("Create floor opening operation timed out");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to create floor opening: {ex.Message}", ex);
        }
    }
}
