using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands;

public class SetElementParameterCommand : ExternalEventCommandBase
{
    private SetElementParameterEventHandler _handler => (SetElementParameterEventHandler)Handler;

    public override string CommandName => "set_element_parameter";

    public SetElementParameterCommand(UIApplication uiApp)
        : base(new SetElementParameterEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        var elementId = parameters?["elementId"]?.Value<long>()
            ?? throw new ArgumentException("elementId is required.");

        var parameterName = parameters?["parameterName"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(parameterName))
            throw new ArgumentException("parameterName is required.");

        if (!parameters!.TryGetValue("value", out var valueToken))
            throw new ArgumentException("value is required.");

        _handler.TargetElementId = elementId;
        _handler.ParameterName = parameterName;
        _handler.Value = ConvertToken(valueToken);
        _handler.Prepare();

        if (RaiseAndWaitForCompletion(30000))
            return _handler.Result;

        throw new TimeoutException("Set element parameter operation timed out after 30 seconds.");
    }

    private static object? ConvertToken(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Integer => token.Value<long>(),
            JTokenType.Float => token.Value<double>(),
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.String => token.Value<string>(),
            JTokenType.Null => null,
            _ => token.ToString()
        };
    }
}
