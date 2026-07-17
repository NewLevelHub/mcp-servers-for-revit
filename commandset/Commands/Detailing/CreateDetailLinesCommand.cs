using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Detailing;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Detailing
{
    public class CreateDetailLinesCommand : ExternalEventCommandBase
    {
        private CreateDetailLinesEventHandler _handler => (CreateDetailLinesEventHandler)Handler;

        public override string CommandName => "create_detail_lines";

        public CreateDetailLinesCommand(UIApplication uiApp)
            : base(new CreateDetailLinesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var polylines = parameters?["polylines"]?.ToObject<List<DetailPolylineInfo>>()
                    ?? new List<DetailPolylineInfo>();

                _handler.SetParameters(polylines);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Create detail lines operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create detail lines: {ex.Message}");
            }
        }
    }
}
