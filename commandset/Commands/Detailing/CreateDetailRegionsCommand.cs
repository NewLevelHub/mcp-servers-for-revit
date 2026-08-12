using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Services.Detailing;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Detailing
{
    public class CreateDetailRegionsCommand : ExternalEventCommandBase
    {
        private CreateDetailRegionsEventHandler _handler => (CreateDetailRegionsEventHandler)Handler;

        public override string CommandName => "create_detail_regions";

        public CreateDetailRegionsCommand(UIApplication uiApp)
            : base(new CreateDetailRegionsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var info = parameters?.ToObject<DetailRegionsCreationInfo>() ?? new DetailRegionsCreationInfo();

                if (info.Regions == null || info.Regions.Count == 0)
                {
                    info.Regions = parameters?["regions"]?.ToObject<List<DetailRegionInfo>>()
                        ?? new List<DetailRegionInfo>();
                }

                _handler.SetParameters(info);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Create detail regions operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create detail regions: {ex.Message}");
            }
        }
    }
}
