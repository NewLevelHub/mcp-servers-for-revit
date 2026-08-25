using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Architecture
{
    /// <summary>
    /// REV-180's one auto-fix: deletes room-separation lines Revit itself has flagged as
    /// redundant because a wall already overlaps them — the "лишние линии разделения помещений"
    /// named in the ticket. Never touches the wall side of the warning.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow, not a generic "auto-resolve warnings" mechanism: there is no such
    /// thing in the Revit API, and building one would mean guessing at fixes for warning types
    /// nobody has vetted. This handler recognizes exactly one FailureDefinitionId and does
    /// nothing at all for any other — see server/src/quality/warningCatalog.ts for why every
    /// other cataloged warning is explain-only.
    /// </remarks>
    public class FixRedundantRoomSeparatorsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        /// <summary>"Стена и линия-разделитель помещений перекрываются." — harvested live, REV-180.</summary>
        public const string TargetFailureGuid = "f7b3a015-c3eb-4a3f-b345-c474ec07d43f";

        private bool _confirm;

        public FixRedundantRoomSeparatorsResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(bool confirm = false)
        {
            _confirm = confirm;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var separatorIds = FindRedundantSeparators(doc);

                if (!_confirm)
                {
                    ResultInfo = new FixRedundantRoomSeparatorsResult
                    {
                        Success = true,
                        Applied = false,
                        Count = separatorIds.Count,
                        ElementIds = separatorIds.Select(id => (long)id.GetIntValue()).ToList(),
                        Message = separatorIds.Count == 0
                            ? "Лишних линий-разделителей не найдено."
                            : $"Проба: удалит {separatorIds.Count} линий-разделителей помещений. Подтвердите confirm=true, чтобы применить.",
                    };
                    return;
                }

                if (separatorIds.Count == 0)
                {
                    ResultInfo = new FixRedundantRoomSeparatorsResult
                    {
                        Success = true,
                        Applied = true,
                        Count = 0,
                        Message = "Лишних линий-разделителей не найдено — применять нечего.",
                    };
                    return;
                }

                var deletedIds = new List<ElementId>();
                using (var transaction = new Transaction(doc, "REV-180: удалить лишние линии-разделители"))
                {
                    transaction.Start();
                    try
                    {
                        foreach (var id in separatorIds)
                        {
                            // A separator can vanish as a side effect of deleting an earlier one
                            // in the same batch (e.g. a shared endpoint) — check, don't assume.
                            if (doc.GetElement(id) != null)
                                deletedIds.AddRange(doc.Delete(id));
                        }
                        transaction.Commit();
                    }
                    catch
                    {
                        if (transaction.HasStarted() && !transaction.HasEnded())
                            transaction.RollBack();
                        throw;
                    }
                }

                var removedSeparators = deletedIds.Where(separatorIds.Contains).Distinct().ToList();
                ResultInfo = new FixRedundantRoomSeparatorsResult
                {
                    Success = true,
                    Applied = true,
                    Count = removedSeparators.Count,
                    ElementIds = removedSeparators.Select(id => (long)id.GetIntValue()).ToList(),
                    Message = $"Удалено линий-разделителей: {removedSeparators.Count}.",
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new FixRedundantRoomSeparatorsResult
                {
                    Success = false,
                    Applied = false,
                    Message = $"Не удалось починить: {ex.Message}",
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        /// <summary>Room-separation-line elements named by an active "wall overlaps separator" warning.</summary>
        private static HashSet<ElementId> FindRedundantSeparators(Document doc)
        {
            var result = new HashSet<ElementId>();
            foreach (var warning in doc.GetWarnings())
            {
                string guid;
                try { guid = warning.GetFailureDefinitionId().Guid.ToString(); }
                catch { continue; }

                if (!guid.Equals(TargetFailureGuid, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var id in warning.GetFailingElements().Concat(warning.GetAdditionalElements()))
                {
                    var element = doc.GetElement(id);
                    if (element?.Category != null
                        && element.Category.Id.GetIntValue() == (int)BuiltInCategory.OST_RoomSeparationLines)
                    {
                        result.Add(id);
                    }
                }
            }
            return result;
        }

        public string GetName() => "Fix Redundant Room Separators";
    }
}
