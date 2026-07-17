using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class ExportEgressGraphCommand : ExternalEventCommandBase
    {
        private ExportEgressGraphEventHandler _handler => (ExportEgressGraphEventHandler)Handler;

        public override string CommandName => "export_egress_graph";

        public ExportEgressGraphCommand(UIApplication uiApp)
            : base(new ExportEgressGraphEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                string levelName = parameters?["levelName"]?.Value<string>();

                _handler.SetParameters(levelName);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Export egress graph operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to export egress graph: {ex.Message}");
            }
        }
    }
}
