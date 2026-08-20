using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Architecture;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Architecture
{
    public class CreateMepOpeningsCommand : ExternalEventCommandBase
    {
        private CreateMepOpeningsEventHandler _handler => (CreateMepOpeningsEventHandler)Handler;

        public override string CommandName => "create_mep_openings";

        public CreateMepOpeningsCommand(UIApplication uiApp)
            : base(new CreateMepOpeningsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                string linkNameFilter = parameters?["linkNameFilter"]?.Value<string>() ?? string.Empty;
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;
                var mepCategories = ReadStrings(parameters, "mepCategories");
                double clearanceMm = parameters?["clearanceMm"]?.Value<double>()
                                     ?? MepOpeningRules.DefaultClearanceMm;
                double mergeGapMm = parameters?["mergeGapMm"]?.Value<double>()
                                    ?? MepOpeningRules.DefaultMergeGapMm;
                double sizeStepMm = parameters?["sizeStepMm"]?.Value<double>()
                                    ?? MepOpeningRules.DefaultSizeStepMm;

                // Preview unless the caller explicitly says otherwise. A missing argument
                // must never be read as permission to cut holes in someone's model.
                bool apply = parameters?["apply"]?.Value<bool>() ?? false;

                long openingTypeId = parameters?["openingTypeId"]?.Value<long>() ?? 0;
                int maxOpenings = parameters?["maxOpenings"]?.Value<int>() ?? 200;
                int timeBudgetSeconds = parameters?["timeBudgetSeconds"]?.Value<int>() ?? 90;

                _handler.SetParameters(
                    linkNameFilter, levelName, mepCategories, clearanceMm, mergeGapMm,
                    sizeStepMm, apply, openingTypeId, maxOpenings, timeBudgetSeconds);

                // The scan gives itself up to 150 s and the writing pass follows it, so the
                // wait has to outlast both rather than time out on a job that succeeded.
                if (RaiseAndWaitForCompletion(180000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("create_mep_openings operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to build the opening задание: {ex.Message}");
            }
        }

        private static List<string> ReadStrings(JObject parameters, string name)
        {
            var token = parameters?[name];
            if (token is not JArray array)
                return new List<string>();

            return array
                .Select(item => item?.Value<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }
    }
}
