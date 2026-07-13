using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Normatives;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Normatives
{
    public class ApplyNormResultCommand : ExternalEventCommandBase
    {
        private ApplyNormResultEventHandler _handler => (ApplyNormResultEventHandler)Handler;

        public override string CommandName => "apply_norm_result";

        public ApplyNormResultCommand(UIApplication uiApp)
            : base(new ApplyNormResultEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var elements = new List<NormResultElement>();
                if (parameters?["elements"] is JArray elementsToken)
                {
                    foreach (var token in elementsToken)
                    {
                        elements.Add(new NormResultElement
                        {
                            ElementId = token["elementId"]?.Value<long>() ?? 0,
                            Note = token["note"]?.Value<string>() ?? string.Empty
                        });
                    }
                }

                var actions = parameters?["actions"] is JArray actionsToken
                    ? actionsToken.Select(token => token.Value<string>() ?? string.Empty).ToList()
                    : new List<string>();

                var norm = parameters?["norm"];
                string normDocument = norm?["document"]?.Value<string>() ?? string.Empty;
                string normClause = norm?["clause"]?.Value<string>() ?? string.Empty;

                string parameterName = parameters?["parameterName"]?.Value<string>() ?? "Comments";
                string valueTemplate = parameters?["valueTemplate"]?.Value<string>() ?? string.Empty;
                string markPrefix = parameters?["markPrefix"]?.Value<string>() ?? "НК-";
                string scheduleName = parameters?["scheduleName"]?.Value<string>() ?? string.Empty;
                bool preview = parameters?["preview"]?.Value<bool>() ?? true;
                bool overwrite = parameters?["overwrite"]?.Value<bool>() ?? false;

                int[] highlightColor = null;
                var colorToken = parameters?["highlightColor"];
                if (colorToken != null && colorToken.Type == JTokenType.Object)
                {
                    highlightColor = new[]
                    {
                        colorToken["r"]?.Value<int>() ?? 255,
                        colorToken["g"]?.Value<int>() ?? 0,
                        colorToken["b"]?.Value<int>() ?? 0
                    };
                }

                _handler.SetParameters(
                    elements,
                    actions,
                    normDocument,
                    normClause,
                    parameterName,
                    valueTemplate,
                    markPrefix,
                    scheduleName,
                    preview,
                    overwrite,
                    highlightColor);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Apply norm result operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to apply norm result: {ex.Message}");
            }
        }
    }
}
