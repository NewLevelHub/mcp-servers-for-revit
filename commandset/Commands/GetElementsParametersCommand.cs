using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands;

public class GetElementsParametersCommand : ExternalEventCommandBase
{
    private const int MaxBatchSize = 100;

    private GetElementsParametersEventHandler _handler =>
        (GetElementsParametersEventHandler)Handler;

    public override string CommandName => "get_elements_parameters";

    public GetElementsParametersCommand(UIApplication uiApp)
        : base(new GetElementsParametersEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        var elementIds = parameters?["elementIds"]?.ToObject<List<long>>();
        if (elementIds == null || elementIds.Count == 0)
            throw new ArgumentException("elementIds array is required.");

        if (elementIds.Count > MaxBatchSize)
            throw new ArgumentException($"elementIds exceeds maximum of {MaxBatchSize}.");

        _handler.TargetElementIds = elementIds;
        _handler.ParameterNames = parameters?["parameterNames"]?.ToObject<List<string>>();
        _handler.Slim = parameters?["slim"]?.Value<bool>() ?? false;
        _handler.Prepare();

        var timeoutMs = Math.Min(120_000, 30_000 + elementIds.Count * 1_000);
        if (RaiseAndWaitForCompletion(timeoutMs))
            return _handler.Result;

        throw new TimeoutException(
            $"Get elements parameters operation timed out after {timeoutMs / 1000} seconds.");
    }
}
