using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views;

public class FitScheduleToSheetCommand : ExternalEventCommandBase
{
    private FitScheduleToSheetEventHandler _handler => (FitScheduleToSheetEventHandler)Handler;

    public override string CommandName => "fit_schedule_to_sheet";

    public FitScheduleToSheetCommand(UIApplication uiApp)
        : base(new FitScheduleToSheetEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?.ToObject<FitScheduleToSheetInfo>()
                ?? throw new ArgumentException("fit_schedule_to_sheet: request body is required.");

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(60000))
                return _handler.ResultInfo;

            throw new TimeoutException("fit_schedule_to_sheet timed out after 60 seconds.");
        }
        catch (Exception ex)
        {
            throw new Exception($"fit_schedule_to_sheet failed: {ex.Message}", ex);
        }
    }
}
