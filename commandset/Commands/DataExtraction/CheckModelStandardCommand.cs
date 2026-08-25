using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    /// <summary>REV-179: raw facts for the org-standard audit — see CheckModelStandardEventHandler.</summary>
    public class CheckModelStandardCommand : ExternalEventCommandBase
    {
        private CheckModelStandardEventHandler _handler => (CheckModelStandardEventHandler)Handler;

        public override string CommandName => "check_model_standard";

        public CheckModelStandardCommand(UIApplication uiApp)
            : base(new CheckModelStandardEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                _handler.SetParameters();

                // One pass over every element of the model, cheap per element (category/level/
                // workset/type id only, no geometry) — REV-175's live measurement of a similar
                // pass was ~1s on a 50k-element model, so 120s covers a large model comfortably.
                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("check_model_standard operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to check model standard: {ex.Message}");
            }
        }
    }
}
