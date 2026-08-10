using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class GetCadLinkGeometryCommand : ExternalEventCommandBase
    {
        private GetCadLinkGeometryEventHandler _handler => (GetCadLinkGeometryEventHandler)Handler;

        public override string CommandName => "get_cad_link_geometry";

        public GetCadLinkGeometryCommand(UIApplication uiApp)
            : base(new GetCadLinkGeometryEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                string cadLinkName = parameters?["cadLinkName"]?.Value<string>() ?? string.Empty;
                long viewId = parameters?["viewId"]?.Value<long>() ?? 0;
                double minLengthMm = parameters?["minLengthMm"]?.Value<double>() ?? 0;
                int limit = parameters?["limit"]?.Value<int>() ?? 5000;
                bool includeHiddenLayers = parameters?["includeHiddenLayers"]?.Value<bool>() ?? false;
                bool includeModelLines = parameters?["includeModelLines"]?.Value<bool>() ?? false;
                string arcMode = parameters?["arcMode"]?.Value<string>() ?? "tessellate";

                var layerFilters = new List<string>();
                var layerToken = parameters?["layerFilter"];
                if (layerToken is JArray arr)
                {
                    foreach (var t in arr)
                    {
                        var s = t?.Value<string>();
                        if (!string.IsNullOrWhiteSpace(s))
                            layerFilters.Add(s);
                    }
                }
                else if (layerToken != null && layerToken.Type != JTokenType.Null)
                {
                    var s = layerToken.Value<string>();
                    if (!string.IsNullOrWhiteSpace(s))
                        layerFilters.Add(s);
                }

                _handler.SetParameters(
                    cadLinkName, layerFilters, viewId, minLengthMm, limit,
                    includeHiddenLayers, includeModelLines, arcMode);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Get CAD link geometry operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to read CAD link geometry: {ex.Message}");
            }
        }
    }
}
