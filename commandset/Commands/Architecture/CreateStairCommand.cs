using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Services.Architecture;

namespace RevitMCPCommandSet.Commands.Architecture;

/// <summary>
/// Create a straight single-run stair (REV-83).
/// </summary>
public class CreateStairCommand : ExternalEventCommandBase
{
    private CreateStairEventHandler _handler => (CreateStairEventHandler)Handler;

    public override string CommandName => "create_stair";

    public CreateStairCommand(UIApplication uiApp)
        : base(new CreateStairEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            List<StairCreationInfo> data = parameters["data"]?.ToObject<List<StairCreationInfo>>();
            if (data == null || data.Count == 0)
                throw new ArgumentNullException(nameof(data), "No stair data provided");

            _handler.SetParameters(data);

            if (RaiseAndWaitForCompletion(30000))
            {
                return _handler.Result;
            }

            throw new TimeoutException("Create stair operation timed out");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to create stair: {ex.Message}", ex);
        }
    }
}
