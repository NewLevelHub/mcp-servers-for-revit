using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands;

public class SetElementsParametersCommand : ExternalEventCommandBase
{
    /// <summary>Matches get_elements_parameters — one page of work per ExternalEvent.</summary>
    private const int MaxElements = 100;

    private SetElementsParametersEventHandler _handler =>
        (SetElementsParametersEventHandler)Handler;

    public override string CommandName => "set_elements_parameters";

    public SetElementsParametersCommand(UIApplication uiApp)
        : base(new SetElementsParametersEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        if (parameters?["edits"] is not JArray editArray || editArray.Count == 0)
            throw new ArgumentException("edits array is required and must not be empty.");

        if (editArray.Count > MaxElements)
            throw new ArgumentException($"edits exceeds maximum of {MaxElements} elements.");

        var edits = new List<ElementParameterEdit>(editArray.Count);
        var writeCount = 0;

        foreach (var token in editArray)
        {
            if (token is not JObject editObject)
                throw new ArgumentException("each edit must be an object with elementId and parameters.");

            var elementId = editObject["elementId"]?.Value<long>()
                ?? throw new ArgumentException("each edit needs an elementId.");

            if (editObject["parameters"] is not JObject parameterObject
                || !parameterObject.HasValues)
            {
                throw new ArgumentException(
                    $"edit for element {elementId} has no parameters to write.");
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in parameterObject.Properties())
                values[property.Name] = ConvertToken(property.Value);

            writeCount += values.Count;
            edits.Add(new ElementParameterEdit { ElementId = elementId, Parameters = values });
        }

        _handler.Edits = edits;
        _handler.Prepare();

        var timeoutMs = Math.Min(120_000, 30_000 + writeCount * 500);
        if (RaiseAndWaitForCompletion(timeoutMs))
            return _handler.Result;

        throw new TimeoutException(
            $"Set elements parameters operation timed out after {timeoutMs / 1000} seconds.");
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
