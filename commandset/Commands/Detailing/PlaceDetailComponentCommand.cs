using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Services.Detailing;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Detailing;

public class PlaceDetailComponentCommand : ExternalEventCommandBase
{
    private PlaceDetailComponentEventHandler _handler => (PlaceDetailComponentEventHandler)Handler;

    public override string CommandName => "place_detail_component";

    public PlaceDetailComponentCommand(UIApplication uiApp)
        : base(new PlaceDetailComponentEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?.ToObject<DetailComponentPlacementInfo>()
                ?? throw new ArgumentException("Detail component placement info is required.");

            if (info.Items == null || info.Items.Count == 0)
                throw new ArgumentException("At least one detail component item is required.");

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(60000))
                return _handler.ResultInfo;

            throw new TimeoutException("Place detail component timed out after 60 seconds");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to place detail components: {ex.Message}", ex);
        }
    }
}
