using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands;

public class GetElementParametersCommand : ExternalEventCommandBase
{
    private GetElementParametersEventHandler _handler => (GetElementParametersEventHandler)Handler;

    public override string CommandName => "get_element_parameters";

    public GetElementParametersCommand(UIApplication uiApp)
        : base(new GetElementParametersEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        var elementId = parameters?["elementId"]?.Value<long>()
            ?? throw new ArgumentException("elementId is required.");

        _handler.TargetElementId = elementId;
        _handler.ParameterNames = parameters?["parameterNames"]?.ToObject<List<string>>();
        _handler.Slim = parameters?["slim"]?.Value<bool>() ?? false;
        _handler.Prepare();

        if (RaiseAndWaitForCompletion(30000))
            return _handler.Result;

        throw new TimeoutException("Get element parameters operation timed out after 30 seconds.");
    }
}
