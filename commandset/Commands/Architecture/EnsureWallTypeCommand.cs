using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Services.Architecture;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Architecture
{
    public class EnsureWallTypeCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();

        private EnsureWallTypeEventHandler _handler => (EnsureWallTypeEventHandler)Handler;

        public override string CommandName => "ensure_wall_type";

        public EnsureWallTypeCommand(UIApplication uiApp)
            : base(new EnsureWallTypeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    var request = parameters?.ToObject<WallTypeRequestInfo>()
                        ?? new WallTypeRequestInfo();

                    _handler.SetParameters(request);

                    if (RaiseAndWaitForCompletion(15000))
                        return _handler.ResultInfo;

                    throw new TimeoutException("ensure_wall_type timed out.");
                }
                catch (Exception ex)
                {
                    throw new Exception($"ensure_wall_type failed: {ex.Message}");
                }
            }
        }
    }
}
