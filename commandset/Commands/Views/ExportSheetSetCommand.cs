using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ExportSheetSetCommand : ExternalEventCommandBase
    {
        private ExportSheetSetEventHandler _handler => (ExportSheetSetEventHandler)Handler;

        public override string CommandName => "export_sheet_set";

        public ExportSheetSetCommand(UIApplication uiApp)
            : base(new ExportSheetSetEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var info = parameters?.ToObject<ExportSheetSetInfo>() ?? new ExportSheetSetInfo();

                _handler.SetParameters(info);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Export sheet set operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to export sheet set: {ex.Message}");
            }
        }
    }
}
