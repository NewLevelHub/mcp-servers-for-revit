using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.AnnotationComponents;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class TagElementsCommand : ExternalEventCommandBase
    {
        private TagElementsEventHandler _handler => (TagElementsEventHandler)Handler;

        public override string CommandName => "tag_elements";

        public TagElementsCommand(UIApplication uiApp)
            : base(new TagElementsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var category = parameters["category"]?.ToString();

            var elementIds = new List<long>();
            if (parameters["elementIds"] is JArray idArray)
            {
                foreach (var token in idArray)
                {
                    if (long.TryParse(token.ToString(), out var id))
                        elementIds.Add(id);
                }
            }

            var useLeader = parameters["useLeader"]?.ToObject<bool>() ?? false;
            var tagTypeId = parameters["tagTypeId"]?.ToString();
            var viewId = parameters["viewId"]?.ToObject<int>() ?? -1;

            _handler.SetParameters(category, elementIds, useLeader, tagTypeId, viewId);

            if (!RaiseAndWaitForCompletion(20000))
                throw new TimeoutException("Маркировка элементов не завершилась за отведённое время.");

            return _handler.Result;
        }
    }
}
