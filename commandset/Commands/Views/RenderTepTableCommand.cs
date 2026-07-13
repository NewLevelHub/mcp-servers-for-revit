using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views;

public class RenderTepTableCommand : ExternalEventCommandBase
{
    private RenderTepTableEventHandler _handler => (RenderTepTableEventHandler)Handler;

    public override string CommandName => "render_tep_table";

    public RenderTepTableCommand(UIApplication uiApp)
        : base(new RenderTepTableEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?.ToObject<TepTableRenderInfo>() ?? new TepTableRenderInfo();

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(120000))
                return _handler.ResultInfo;

            throw new TimeoutException("Render TEP table timed out after 120 seconds");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to render TEP table: {ex.Message}", ex);
        }
    }
}
