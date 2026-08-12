using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Services.Architecture;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Architecture
{
    public class EnsureOpeningTypeCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();

        private EnsureOpeningTypeEventHandler _handler => (EnsureOpeningTypeEventHandler)Handler;

        public override string CommandName => "ensure_opening_type";

        public EnsureOpeningTypeCommand(UIApplication uiApp)
            : base(new EnsureOpeningTypeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    var request = parameters?.ToObject<OpeningTypeRequestInfo>()
                        ?? new OpeningTypeRequestInfo();

                    _handler.SetParameters(request);

                    if (RaiseAndWaitForCompletion(15000))
                        return _handler.ResultInfo;

                    throw new TimeoutException("ensure_opening_type timed out.");
                }
                catch (Exception ex)
                {
                    throw new Exception($"ensure_opening_type failed: {ex.Message}");
                }
            }
        }
    }
}
