using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class DeleteElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        // 执行结果
        public bool IsSuccess { get; private set; }

        // 成功删除的元素数量
        public int DeletedCount { get; private set; }

        /// <summary>
        /// What the caller asked to delete, as «Категория «Имя» (id)». doc.Delete also removes
        /// dependents, so a request for one sheet can report a count of seven — the journal has
        /// to say what was actually targeted, not just a number.
        /// </summary>
        public List<string> DeletedDescriptions { get; } = new List<string>();

        /// <summary>Ids that were not in the document (already deleted, or from another model).</summary>
        public List<string> MissingIds { get; } = new List<string>();

        /// <summary>Ids that were not numbers at all.</summary>
        public List<string> InvalidIds { get; } = new List<string>();

        /// <summary>Set when the delete transaction itself failed.</summary>
        public string ErrorMessage { get; private set; } = string.Empty;

        // 状态同步对象
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        // 要删除的元素ID数组
        public string[] ElementIds { get; set; }
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
                var doc = app.ActiveUIDocument.Document;
                DeletedCount = 0;
                DeletedDescriptions.Clear();
                MissingIds.Clear();
                InvalidIds.Clear();
                ErrorMessage = string.Empty;

                if (ElementIds == null || ElementIds.Length == 0)
                {
                    ErrorMessage = "elementIds is empty.";
                    IsSuccess = false;
                    return;
                }

                var elementIdsToDelete = new List<ElementId>();
                foreach (var idStr in ElementIds)
                {
                    if (!int.TryParse(idStr, out var elementIdValue))
                    {
                        InvalidIds.Add(idStr);
                        continue;
                    }

                    var elementId = new ElementId(elementIdValue);
                    var element = doc.GetElement(elementId);
                    if (element == null)
                    {
                        MissingIds.Add(idStr);
                        continue;
                    }

                    elementIdsToDelete.Add(elementId);
                    DeletedDescriptions.Add(Describe(element));
                }

                // Nothing left to delete is a normal answer for an id that is already gone —
                // never a modal dialog: this runs on an MCP call with no one at the keyboard,
                // and a TaskDialog would block Revit until someone clicks it.
                if (elementIdsToDelete.Count == 0)
                {
                    ErrorMessage = InvalidIds.Count > 0
                        ? "No element ids could be parsed."
                        : "None of the requested elements exist in the active document (already deleted?).";
                    IsSuccess = false;
                    return;
                }

                using (var transaction = new Transaction(doc, "Delete Elements"))
                {
                    transaction.Start();
                    var deletedIds = doc.Delete(elementIdsToDelete);
                    DeletedCount = deletedIds.Count;
                    transaction.Commit();
                }

                IsSuccess = true;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                IsSuccess = false;
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }
        private static string Describe(Element element)
        {
            var category = element.Category?.Name;
            var name = element.Name;
            var id = element.Id.GetValue();

            if (string.IsNullOrWhiteSpace(category))
                return string.IsNullOrWhiteSpace(name) ? $"id {id}" : $"'{name}' (id {id})";

            return string.IsNullOrWhiteSpace(name)
                ? $"{category} (id {id})"
                : $"{category} '{name}' (id {id})";
        }

        public string GetName()
        {
            return "删除元素";
        }
    }
}
