using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views;

public class AutoLayoutSheetCommand : ExternalEventCommandBase
{
    private AutoLayoutSheetEventHandler _handler => (AutoLayoutSheetEventHandler)Handler;

    public override string CommandName => "auto_layout_sheet";

    public AutoLayoutSheetCommand(UIApplication uiApp)
        : base(new AutoLayoutSheetEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?.ToObject<AutoLayoutSheetInfo>()
                ?? throw new ArgumentException("Auto layout info is required.");

            if (info.Items == null || info.Items.Count == 0)
                throw new ArgumentException("At least one item (view or schedule) is required.");

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(120000))
                return _handler.ResultInfo;

            throw new TimeoutException("Auto layout sheet timed out after 120 seconds");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to auto layout sheet: {ex.Message}", ex);
        }
    }
}
