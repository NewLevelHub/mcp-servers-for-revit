using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Services
{
    public class GetSelectedElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        /// <summary>
        /// Paged envelope, not a bare list: Take(Limit) used to drop the rest of the
        /// selection without saying so, and "selected 12 elements" then meant a
        /// silently different set than the architect had highlighted.
        /// </summary>
        public AIResult<List<Models.Common.ElementInfo>> Result { get; private set; }

        // 状态同步对象
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        // 限制返回的元素数量
        public int? Limit { get; set; }
        public int? Offset { get; set; }

        // 实现IWaitableExternalEventHandler接口
                /// <summary>
        /// Reset wait state before ExternalEvent.Raise. Must be called from the command before RaiseAndWaitForCompletion.
        /// </summary>
        public void Prepare()
        {
            TaskCompleted = false;
            _resetEvent.Reset();
        }
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                var doc = uiDoc.Document;

                // 获取当前选中的元素
                var selectedIds = uiDoc.Selection.GetElementIds();
                var selectedElements = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(element => element != null)
                    .ToList();

                var total = selectedElements.Count;
                var offset = Offset.HasValue && Offset.Value > 0 ? Offset.Value : 0;
                var limit = Limit.HasValue && Limit.Value > 0 ? Limit.Value : int.MaxValue;

                var page = selectedElements
                    .Skip(offset)
                    .Take(limit)
                    .Select(element => new ElementInfo
                    {
#if REVIT2024_OR_GREATER
                        Id = element.Id.Value,
#else
                        Id = element.Id.IntegerValue,
#endif
                        UniqueId = element.UniqueId,
                        Name = element.Name,
                        Category = element.Category?.Name
                    })
                    .ToList();

                var hasMore = offset + page.Count < total;

                Result = new AIResult<List<ElementInfo>>
                {
                    // An empty selection is an answer, not a failure.
                    Success = true,
                    Message = total == 0
                        ? "В Revit ничего не выделено."
                        : hasMore
                            ? $"Выделено элементов: {total}, показано {page.Count} начиная с {offset}. "
                              + $"Есть ещё: повторите с offset={offset + page.Count} или увеличьте limit."
                            : $"Выделено элементов: {total} (показаны все).",
                    Response = page,
                    TotalCount = total,
                    HasMore = hasMore,
                    Offset = offset,
                    Limit = limit == int.MaxValue ? (int?)null : limit
                };
            }
            catch (Exception ex)
            {
                // No TaskDialog.Show: this runs inside an ExternalEvent with nobody able
                // to click it during an agent-driven turn — it would hang the chat.
                System.Diagnostics.Trace.WriteLine($"get_selected_elements failed: {ex}");
                Result = new AIResult<List<ElementInfo>>
                {
                    Success = false,
                    Message = ex.Message,
                    Response = new List<ElementInfo>()
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "获取选中元素";
        }
    }
}
