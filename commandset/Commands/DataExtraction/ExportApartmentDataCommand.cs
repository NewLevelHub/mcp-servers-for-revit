using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class ExportApartmentDataCommand : ExternalEventCommandBase
    {
        private ExportApartmentDataEventHandler _handler => (ExportApartmentDataEventHandler)Handler;

        public override string CommandName => "export_apartment_data";

        public ExportApartmentDataCommand(UIApplication uiApp)
            : base(new ExportApartmentDataEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                string apartmentParameter = parameters?["apartmentNumberParameter"]?.Value<string>();
                bool includeRooms = parameters?["includeRooms"]?.Value<bool>() ?? false;

                _handler.SetParameters(apartmentParameter, includeRooms);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Export apartment data operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to export apartment data: {ex.Message}");
            }
        }
    }
}
