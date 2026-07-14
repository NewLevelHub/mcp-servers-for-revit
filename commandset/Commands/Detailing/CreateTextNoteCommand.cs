using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Services.Detailing;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Detailing;

public class CreateTextNoteCommand : ExternalEventCommandBase
{
    private CreateTextNoteEventHandler _handler => (CreateTextNoteEventHandler)Handler;

    public override string CommandName => "create_text_note";

    public CreateTextNoteCommand(UIApplication uiApp)
        : base(new CreateTextNoteEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var info = parameters?.ToObject<TextNoteCreationInfo>()
                ?? throw new ArgumentException("Text note creation info is required.");

            _handler.SetParameters(info);

            if (RaiseAndWaitForCompletion(60000))
                return _handler.ResultInfo;

            throw new TimeoutException("Create text note timed out after 60 seconds");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to create text note: {ex.Message}", ex);
        }
    }
}
