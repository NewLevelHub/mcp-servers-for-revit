using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Normatives
{
    public class CheckFireDoorsCommand : ExternalEventCommandBase
    {
        private CheckFireDoorsEventHandler _handler => (CheckFireDoorsEventHandler)Handler;

        public override string CommandName => "check_fire_doors";

        public CheckFireDoorsCommand(UIApplication uiApp)
            : base(new CheckFireDoorsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;
                _handler.SetParameters(levelName);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Check fire doors operation timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to check fire doors: {ex.Message}");
            }
        }
    }
}
