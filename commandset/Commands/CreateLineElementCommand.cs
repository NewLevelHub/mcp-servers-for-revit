using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands
{
    public class CreateLineElementCommand : ExternalEventCommandBase
    {
        private CreateLineElementEventHandler _handler => (CreateLineElementEventHandler)Handler;

        /// <summary>
        /// 命令名称
        /// </summary>
        public override string CommandName => "create_line_based_element";

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="uiApp">Revit UIApplication</param>
        public CreateLineElementCommand(UIApplication uiApp)
            : base(new CreateLineElementEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                if (parameters == null || parameters["data"] == null || parameters["data"].Type == JTokenType.Null)
                    throw new ArgumentException(
                        "data is required: array of {category, typeId, locationLine:{p0:{x,y,z}, p1:{x,y,z}}, height, baseLevel, baseOffset}. " +
                        "Get typeId from get_available_family_types (OST_Walls).");

                var dataToken = parameters["data"];
                if (dataToken.Type == JTokenType.Object)
                    dataToken = new JArray(dataToken);

                List<LineElement> data = dataToken.ToObject<List<LineElement>>();
                if (data == null || data.Count == 0)
                    throw new ArgumentException("data array is empty — pass at least one wall segment.");

                for (int i = 0; i < data.Count; i++)
                {
                    var item = data[i];
                    if (item == null)
                        throw new ArgumentException($"data[{i}] is null.");
                    if (item.LocationLine == null || item.LocationLine.P0 == null || item.LocationLine.P1 == null)
                        throw new ArgumentException(
                            $"data[{i}].locationLine with p0 and p1 (mm) is required.");
                    if (item.TypeId <= 0)
                        throw new ArgumentException(
                            $"data[{i}].typeId is required — call get_available_family_types first.");
                    if (item.Height <= 0)
                        item.Height = 3000;
                }

                _handler.SetParameters(data);

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.Result;
                }
                else
                {
                    throw new TimeoutException("create_line_based_element timed out after 60 seconds");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create line-based elements: {ex.Message}");
            }
        }
    }
}
