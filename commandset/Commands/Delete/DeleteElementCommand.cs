using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Delete
{
    public class DeleteElementCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private DeleteElementEventHandler _handler => (DeleteElementEventHandler)Handler;

        public override string CommandName => "delete_element";

        public DeleteElementCommand(UIApplication uiApp)
            : base(new DeleteElementEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    // 解析数组参数
                    var elementIds = parameters?["elementIds"]?.ToObject<string[]>();
                    if (elementIds == null || elementIds.Length == 0)
                    {
                        throw new ArgumentException("元素ID列表不能为空");
                    }

                    // 设置要删除的元素ID数组
                    _handler.ElementIds = elementIds;
                    _handler.Prepare();

                    // 触发外部事件并等待完成
                    if (RaiseAndWaitForCompletion(15000))
                    {
                        // Report what was targeted, not just how many elements Revit removed:
                        // doc.Delete also drops dependents, so one sheet can come back as 7.
                        if (_handler.IsSuccess)
                        {
                            return new
                            {
                                deleted = true,
                                count = _handler.DeletedCount,
                                requested = _handler.DeletedDescriptions,
                                dependentsRemoved =
                                    Math.Max(0, _handler.DeletedCount - _handler.DeletedDescriptions.Count),
                                missingIds = _handler.MissingIds,
                                invalidIds = _handler.InvalidIds
                            };
                        }

                        return new
                        {
                            deleted = false,
                            count = 0,
                            message = string.IsNullOrEmpty(_handler.ErrorMessage)
                                ? "Delete failed."
                                : _handler.ErrorMessage,
                            missingIds = _handler.MissingIds,
                            invalidIds = _handler.InvalidIds
                        };
                    }
                    else
                    {
                        throw new TimeoutException("删除元素操作超时");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"删除元素失败: {ex.Message}");
                }
            }
        }
    }
}
