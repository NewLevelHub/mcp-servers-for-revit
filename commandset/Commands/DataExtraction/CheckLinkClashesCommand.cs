using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class CheckLinkClashesCommand : ExternalEventCommandBase
    {
        private CheckLinkClashesEventHandler _handler => (CheckLinkClashesEventHandler)Handler;

        public override string CommandName => "check_link_clashes";

        public CheckLinkClashesCommand(UIApplication uiApp)
            : base(new CheckLinkClashesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                string linkNameFilter = parameters?["linkNameFilter"]?.Value<string>() ?? string.Empty;
                var hostCategories = ReadStrings(parameters, "hostCategories");
                var linkCategories = ReadStrings(parameters, "linkCategories");
                double toleranceMm = parameters?["toleranceMm"]?.Value<double>() ?? ClashRules.DefaultToleranceMm;
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;
                int maxClashes = parameters?["maxClashes"]?.Value<int>() ?? 500;
                int maxHostElements = parameters?["maxHostElements"]?.Value<int>() ?? 50000;
                int timeBudgetSeconds = parameters?["timeBudgetSeconds"]?.Value<int>() ?? 90;
                bool includeRooms = parameters?["includeRooms"]?.Value<bool>() ?? true;
                bool mergeLayers = parameters?["mergeLayers"]?.Value<bool>() ?? true;

                _handler.SetParameters(
                    linkNameFilter, hostCategories, linkCategories, toleranceMm, levelName,
                    maxClashes, maxHostElements, timeBudgetSeconds, includeRooms, mergeLayers);

                // The handler caps itself at a 150 s budget; the wait has to outlast that
                // so a run that used its whole budget returns a list rather than a timeout.
                if (RaiseAndWaitForCompletion(180000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("check_link_clashes operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to check link clashes: {ex.Message}");
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
