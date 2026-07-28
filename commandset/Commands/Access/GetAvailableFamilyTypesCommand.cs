using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Access
{
    public class GetAvailableFamilyTypesCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private GetAvailableFamilyTypesEventHandler _handler => (GetAvailableFamilyTypesEventHandler)Handler;

        public override string CommandName => "get_available_family_types";

        public GetAvailableFamilyTypesCommand(UIApplication uiApp)
            : base(new GetAvailableFamilyTypesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    // Support both categoryList (array) and categoryName (string) — agent often sends categoryName.
                    List<string> categoryList = parameters?["categoryList"]?.ToObject<List<string>>()
                        ?? new List<string>();
                    var categoryName = parameters?["categoryName"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(categoryName) && categoryList.Count == 0)
                        categoryList.Add(categoryName.Trim());

                    string familyNameFilter = parameters?["familyNameFilter"]?.Value<string>();
                    int? limit = parameters?["limit"]?.Value<int>();

                    // Default: walls only when agent asks for types before creating walls without filter —
                    // keep unfiltered if neither arg set (legacy). Limit large dumps.
                    if (!limit.HasValue || limit.Value <= 0)
                        limit = categoryList.Count > 0 ? 80 : 60;

                    // 设置查询参数
                    _handler.CategoryList = categoryList;
                    _handler.FamilyNameFilter = familyNameFilter;
                    _handler.Limit = limit;
                    _handler.Prepare();

                    // 触发外部事件并等待完成，最多等待15秒
                    if (RaiseAndWaitForCompletion(15000))
                    {
                        return _handler.ResultFamilyTypes;
                    }
                    else
                    {
                        throw new TimeoutException("获取可用族类型超时");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"获取可用族类型失败: {ex.Message}");
                }
            }
        }
    }
}
