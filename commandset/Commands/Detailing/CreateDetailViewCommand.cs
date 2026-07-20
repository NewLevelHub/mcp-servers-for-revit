using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Services.Detailing;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Detailing;

public class CreateDetailViewCommand : ExternalEventCommandBase
{
    private CreateDetailViewEventHandler _handler => (CreateDetailViewEventHandler)Handler;

    public override string CommandName => "create_detail_view";

    public CreateDetailViewCommand(UIApplication uiApp)
        : base(new CreateDetailViewEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?.ToObject<DetailViewCreationInfo>()
                ?? throw new ArgumentException("Detail view creation info is required.");

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(60000))
                return _handler.ResultInfo;

            throw new TimeoutException("Create detail view timed out after 60 seconds");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to create detail view: {ex.Message}", ex);
        }
    }
}
