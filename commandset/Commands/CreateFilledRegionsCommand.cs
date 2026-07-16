using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands
{
    public class CreateFilledRegionsCommand : ExternalEventCommandBase
    {
        private CreateFilledRegionsEventHandler _handler => (CreateFilledRegionsEventHandler)Handler;

        public override string CommandName => "create_filled_regions";

        public CreateFilledRegionsCommand(UIApplication uiApp)
            : base(new CreateFilledRegionsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var roomIds = new List<long>();
                if (parameters?["roomIds"] is JArray idsArray)
                {
                    foreach (var token in idsArray)
                    {
                        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                            roomIds.Add(token.Value<long>());
                        else if (long.TryParse(token.ToString(), out var parsed))
                            roomIds.Add(parsed);
                    }
                }

                var roomNames = new List<string>();
                if (parameters?["roomNames"] is JArray namesArray)
                {
                    foreach (var token in namesArray)
                    {
                        var name = token?.Value<string>();
                        if (!string.IsNullOrWhiteSpace(name))
                            roomNames.Add(name);
                    }
                }

                string filledRegionTypeName = parameters?["filledRegionTypeName"]?.Value<string>() ?? string.Empty;
                string colorPreset = parameters?["colorPreset"]?.Value<string>() ?? "red";
                bool clearPrevious = parameters?["clearPrevious"]?.Value<bool>() ?? true;
                string commentTag = parameters?["commentTag"]?.Value<string>()
                    ?? CreateFilledRegionsEventHandler.DefaultCommentTag;

                _handler.SetParameters(
                    roomIds,
                    roomNames,
                    filledRegionTypeName,
                    colorPreset,
                    clearPrevious,
                    commentTag);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("create_filled_regions timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"create_filled_regions failed: {ex.Message}", ex);
            }
        }
    }
}
