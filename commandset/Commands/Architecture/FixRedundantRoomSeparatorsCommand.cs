using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Architecture;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Architecture
{
    /// <summary>REV-180: preview/apply the one safe warning auto-fix — see the event handler.</summary>
    public class FixRedundantRoomSeparatorsCommand : ExternalEventCommandBase
    {
        private FixRedundantRoomSeparatorsEventHandler _handler => (FixRedundantRoomSeparatorsEventHandler)Handler;

        public override string CommandName => "fix_redundant_room_separators";

        public FixRedundantRoomSeparatorsCommand(UIApplication uiApp)
            : base(new FixRedundantRoomSeparatorsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                bool confirm = parameters?["confirm"]?.Value<bool>() ?? false;
                _handler.SetParameters(confirm);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("fix_redundant_room_separators operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to fix redundant room separators: {ex.Message}");
            }
        }
    }
}
