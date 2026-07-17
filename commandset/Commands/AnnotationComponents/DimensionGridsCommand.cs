using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Annotation;
using RevitMCPCommandSet.Services.AnnotationComponents;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.AnnotationComponents;

public class DimensionGridsCommand : ExternalEventCommandBase
{
    private DimensionGridsEventHandler _handler => (DimensionGridsEventHandler)Handler;

    public DimensionGridsCommand(UIApplication uiApp)
        : base(new DimensionGridsEventHandler(), uiApp)
    {
    }

    public override string CommandName => "dimension_grids";

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?.ToObject<GridDimensionInfo>()
                ?? new GridDimensionInfo();

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(60000))
                return _handler.Result;

            throw new TimeoutException("Axial dimension operation timed out after 60 seconds.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Error creating axial grid dimensions: {ex.Message}", ex);
        }
    }
}
