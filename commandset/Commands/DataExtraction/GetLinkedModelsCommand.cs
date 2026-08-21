using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class GetLinkedModelsCommand : ExternalEventCommandBase
    {
        private GetLinkedModelsEventHandler _handler => (GetLinkedModelsEventHandler)Handler;

        public override string CommandName => "get_linked_models";

        public GetLinkedModelsCommand(UIApplication uiApp)
            : base(new GetLinkedModelsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                bool includeElementCounts = parameters?["includeElementCounts"]?.Value<bool>() ?? true;
                bool includeCategories = parameters?["includeCategories"]?.Value<bool>() ?? false;
                int categoryLimit = parameters?["categoryLimit"]?.Value<int>() ?? 8;
                int coordinateSamples = parameters?["coordinateSamples"]?.Value<int>() ?? 1;
                string levelName = parameters?["levelName"]?.Value<string>() ?? string.Empty;
                string nameFilter = parameters?["nameFilter"]?.Value<string>() ?? string.Empty;
                // REV-169: сверка общей площадки. Выключено по умолчанию — обход осей
                // и уровней каждой связи не нужен тем, кто просто спрашивает список.
                bool includeLevels = parameters?["includeLevels"]?.Value<bool>() ?? false;
                bool includeGrids = parameters?["includeGrids"]?.Value<bool>() ?? false;
                bool includeSitePoints = parameters?["includeSitePoints"]?.Value<bool>() ?? false;

                _handler.SetParameters(
                    includeElementCounts, includeCategories, categoryLimit,
                    coordinateSamples, levelName, nameFilter,
                    includeLevels, includeGrids, includeSitePoints);

                // The default pass is a few milliseconds per link, but a project with a
                // dozen ИОС links and includeCategories on walks every element of every
                // one of them. The wait is sized for that worst case, not for the default.
                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("get_linked_models operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to read linked models: {ex.Message}");
            }
        }
    }
}
