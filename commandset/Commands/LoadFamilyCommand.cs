using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands
{
    public class LoadFamilyCommand : ExternalEventCommandBase
    {
        private LoadFamilyEventHandler _handler => (LoadFamilyEventHandler)Handler;

        public override string CommandName => "load_family";

        public LoadFamilyCommand(UIApplication uiApp)
            : base(new LoadFamilyEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var info = parameters?.ToObject<LoadFamilyRequestInfo>() ?? new LoadFamilyRequestInfo();
                _handler.SetParameters(info);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Load family operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load families: {ex.Message}");
            }
        }
    }
}
