using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Services.Detailing;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Detailing
{
    public class CreateNodeDetailCommand : ExternalEventCommandBase
    {
        private CreateNodeDetailEventHandler _handler => (CreateNodeDetailEventHandler)Handler;

        public override string CommandName => "create_node_detail";

        public CreateNodeDetailCommand(UIApplication uiApp)
            : base(new CreateNodeDetailEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var info = parameters?.ToObject<NodeDetailCreationInfo>() ?? new NodeDetailCreationInfo();

                if (info.ExtraLayers == null || info.ExtraLayers.Count == 0)
                {
                    info.ExtraLayers = parameters?["extraLayers"]?.ToObject<List<NodeExtraLayerInfo>>()
                        ?? new List<NodeExtraLayerInfo>();
                }

                _handler.SetParameters(info);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Create node detail operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create node detail: {ex.Message}");
            }
        }
    }
}
