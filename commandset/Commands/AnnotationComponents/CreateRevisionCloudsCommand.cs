using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Annotation;
using RevitMCPCommandSet.Services.AnnotationComponents;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class CreateRevisionCloudsCommand : ExternalEventCommandBase
    {
        private CreateRevisionCloudsEventHandler _handler => (CreateRevisionCloudsEventHandler)Handler;

        public override string CommandName => "create_revision_clouds";

        public CreateRevisionCloudsCommand(UIApplication uiApp)
            : base(new CreateRevisionCloudsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var info = parameters?.ToObject<RevisionCloudsCreationInfo>() ?? new RevisionCloudsCreationInfo();

                _handler.SetParameters(info);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Create revision clouds operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create revision clouds: {ex.Message}");
            }
        }
    }
}
