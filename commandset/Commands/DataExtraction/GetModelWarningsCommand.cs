using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class GetModelWarningsCommand : ExternalEventCommandBase
    {
        private GetModelWarningsEventHandler _handler => (GetModelWarningsEventHandler)Handler;

        public override string CommandName => "get_model_warnings";

        public GetModelWarningsCommand(UIApplication uiApp)
            : base(new GetModelWarningsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                int maxElementIdsPerGroup = parameters?["maxElementIdsPerGroup"]?.Value<int>() ?? 20;
                var severity = parameters?["severity"]?.Value<string>();

                _handler.SetParameters(maxElementIdsPerGroup, severity);

                // Reading the warning list touches no geometry; the wait only has to
                // outlast Revit being busy with a dialog or a regeneration.
                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("get_model_warnings operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to read model warnings: {ex.Message}");
            }
        }
    }
}
