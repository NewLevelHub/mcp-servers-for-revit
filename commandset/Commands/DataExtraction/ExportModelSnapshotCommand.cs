using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    /// <summary>
    /// One page of a model snapshot (REV-170). The server calls this repeatedly and
    /// writes each page into SQLite; nothing here holds a whole model in memory.
    /// </summary>
    public class ExportModelSnapshotCommand : ExternalEventCommandBase
    {
        private ExportModelSnapshotEventHandler _handler => (ExportModelSnapshotEventHandler)Handler;

        public override string CommandName => "export_model_snapshot";

        public ExportModelSnapshotCommand(UIApplication uiApp)
            : base(new ExportModelSnapshotEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                int offset = parameters?["offset"]?.Value<int>() ?? 0;
                int limit = parameters?["limit"]?.Value<int>() ?? 5000;
                bool includeAnnotation = parameters?["includeAnnotation"]?.Value<bool>() ?? false;
                bool includeRooms = parameters?["includeRooms"]?.Value<bool>() ?? true;
                bool includeBoundingBox = parameters?["includeBoundingBox"]?.Value<bool>() ?? true;
                bool includeServiceCategories =
                    parameters?["includeServiceCategories"]?.Value<bool>() ?? false;
                string snapshotToken = parameters?["snapshotToken"]?.Value<string>() ?? string.Empty;

                var categories = ReadStrings(parameters, "categories");
                var extraParameters = ReadStrings(parameters, "extraParameters");

                _handler.SetParameters(
                    offset, limit, includeAnnotation, includeRooms, includeBoundingBox,
                    includeServiceCategories, categories, extraParameters, snapshotToken);

                // The first page pays for the full pass over the model that decides which
                // elements belong in the snapshot at all — on 300k elements that is the
                // slow part, and the later pages are quick by comparison. The wait is
                // sized for that first page.
                if (RaiseAndWaitForCompletion(180000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("export_model_snapshot operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to export model snapshot: {ex.Message}");
            }
        }

        private static List<string> ReadStrings(JObject parameters, string name)
        {
            var values = new List<string>();
            if (parameters?[name] is not JArray array) return values;

            foreach (var token in array)
            {
                var value = token?.Value<string>();
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
            }

            return values;
        }
    }
}
