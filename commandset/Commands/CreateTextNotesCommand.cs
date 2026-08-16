using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Annotation;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands
{
    public class CreateTextNotesCommand : ExternalEventCommandBase
    {
        private CreateTextNotesEventHandler _handler => (CreateTextNotesEventHandler)Handler;

        public override string CommandName => "create_text_notes";

        public CreateTextNotesCommand(UIApplication uiApp)
            : base(new CreateTextNotesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var notes = parameters?["notes"]?.ToObject<List<TextNotePlacementInfo>>()
                            ?? new List<TextNotePlacementInfo>();

                var lines = parameters?["lines"]?.ToObject<List<DetailLinePlacementInfo>>()
                            ?? new List<DetailLinePlacementInfo>();

                bool clearPrevious = parameters?["clearPrevious"]?.Value<bool>() ?? true;
                bool clearOnly = parameters?["clearOnly"]?.Value<bool>() ?? false;
                string commentTag = parameters?["commentTag"]?.Value<string>()
                                    ?? CreateTextNotesEventHandler.DefaultCommentTag;
                // Empty on purpose: the handler then resolves the project's normal
                // (black) text type. Defaulting to ADSK_Замечания here painted every
                // ordinary caption in the norm-control red.
                string textTypeName = parameters?["textTypeName"]?.Value<string>()
                                      ?? string.Empty;
                string placement = parameters?["placement"]?.Value<string>()
                                   ?? CreateTextNotesEventHandler.DefaultPlacement;
                double marginMm = parameters?["marginMm"]?.Value<double>() ?? 0;
                double paperWidthMm = parameters?["paperWidthMm"]?.Value<double>() ?? 0;
                double textSizeMm = parameters?["textSizeMm"]?.Value<double>() ?? 0;

                long viewId = -1;
                var viewToken = parameters?["viewId"];
                if (viewToken != null && viewToken.Type != JTokenType.Null)
                {
                    if (viewToken.Type == JTokenType.Integer || viewToken.Type == JTokenType.Float)
                        viewId = viewToken.Value<long>();
                    else if (long.TryParse(viewToken.ToString(), out var parsed))
                        viewId = parsed;
                }

                _handler.SetParameters(
                    notes,
                    lines,
                    clearPrevious,
                    commentTag,
                    textTypeName,
                    viewId,
                    placement,
                    marginMm,
                    paperWidthMm,
                    textSizeMm,
                    clearOnly);

                if (RaiseAndWaitForCompletion(60000))
                    return _handler.ResultInfo;

                throw new TimeoutException("create_text_notes timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"create_text_notes failed: {ex.Message}", ex);
            }
        }
    }
}
