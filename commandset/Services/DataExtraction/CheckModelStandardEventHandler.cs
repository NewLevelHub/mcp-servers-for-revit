using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    /// Walks the open model once and reports raw facts an architect currently checks by eye on
    /// acceptance: loaded types and how many are actually placed, elements missing a level,
    /// which workset each category really lives in, groups, views and links (REV-179).
    /// </summary>
    /// <remarks>
    /// Deliberately reports counts, not element lists, wherever a real model could have
    /// thousands of rows (types, worksets×categories, elements-without-level) — a small sample
    /// of ids rides along for "click and see" without the payload growing with model size. What
    /// counts as a violation (a name pattern, a threshold) is not decided here: that grading
    /// lives in <c>server/src/quality/standardRules.ts</c>, config-driven and unit-tested
    /// without Revit, exactly as REV-179 asks for. This handler only answers "what is true".
    /// </remarks>
    public class CheckModelStandardEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public CheckModelStandardResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters()
        {
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var result = new CheckModelStandardResult { Success = true };

                var worksharingEnabled = doc.IsWorkshared;
                result.WorksharingEnabled = worksharingEnabled;

                var worksetNameById = new Dictionary<int, string>();
                if (worksharingEnabled)
                {
                    foreach (Workset workset in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                        worksetNameById[workset.Id.IntegerValue] = workset.Name;
                }

                var instanceCountByTypeId = new Dictionary<long, int>();
                var elementsWithoutLevel = new Dictionary<string, ModelStandardCategoryCount>();
                var modelCategoryTotals = new Dictionary<string, int>();
                var worksetByCategory = new Dictionary<(string category, string workset), ModelStandardWorksetCategoryCount>();
                var worksetTotals = new Dictionary<string, int>();

                foreach (var elem in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    var category = elem.Category;
                    var categoryName = category?.Name ?? elem.GetType().Name;

                    var typeId = elem.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        instanceCountByTypeId.TryGetValue(typeId.GetIntValue(), out var count);
                        instanceCountByTypeId[typeId.GetIntValue()] = count + 1;
                    }

                    // Self-calibrating, not a hardcoded category list (REV-179, caught live on a
                    // real 35k-element model): materials, legend components, analytical-model
                    // nodes and in-progress sketch geometry are all CategoryType.Model but never
                    // have a level, for any project — flagging them every time was pure noise.
                    // Instead: count both sides per category, and only call "no level" a mistake
                    // for a category where SOME of its own elements do have one — that is what
                    // proves a level is the norm there at all.
                    if (category != null && category.CategoryType == CategoryType.Model)
                    {
                        modelCategoryTotals.TryGetValue(categoryName, out var catTotal);
                        modelCategoryTotals[categoryName] = catTotal + 1;

                        if (elem.LevelId == ElementId.InvalidElementId)
                            Bump(elementsWithoutLevel, categoryName, elem.Id.GetIntValue());
                    }

                    if (worksharingEnabled)
                    {
                        var worksetId = elem.WorksetId;
                        if (worksetId != WorksetId.InvalidWorksetId
                            && worksetNameById.TryGetValue(worksetId.IntegerValue, out var worksetName))
                        {
                            var key = (categoryName, worksetName);
                            if (!worksetByCategory.TryGetValue(key, out var bucket))
                            {
                                bucket = new ModelStandardWorksetCategoryCount { Category = categoryName, WorksetName = worksetName };
                                worksetByCategory[key] = bucket;
                            }
                            bucket.Count++;
                            if (bucket.SampleElementIds.Count < 5)
                                bucket.SampleElementIds.Add(elem.Id.GetIntValue());

                            worksetTotals.TryGetValue(worksetName, out var total);
                            worksetTotals[worksetName] = total + 1;
                        }
                    }
                }

                result.ElementsWithoutLevel = elementsWithoutLevel.Values
                    .Where(c => modelCategoryTotals.TryGetValue(c.Category, out var total) && c.Count < total)
                    .OrderByDescending(c => c.Count)
                    .ToList();
                result.WorksetByCategory = worksetByCategory.Values.OrderByDescending(c => c.Count).ToList();

                foreach (var name in worksetNameById.Values)
                {
                    worksetTotals.TryGetValue(name, out var count);
                    result.Worksets.Add(new ModelStandardWorksetInfo
                    {
                        Name = name,
                        Kind = WorksetKind.UserWorkset.ToString(),
                        ElementCount = count,
                    });
                }
                result.Worksets = result.Worksets.OrderByDescending(w => w.ElementCount).ToList();

                CollectTypes(doc, instanceCountByTypeId, result);
                CollectGroups(doc, result);
                CollectViews(doc, result);
                CollectLinks(doc, result);

                stopwatch.Stop();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                ResultInfo = result;
            }
            catch (Exception ex)
            {
                ResultInfo = new CheckModelStandardResult
                {
                    Success = false,
                    Message = $"Не удалось проверить модель: {ex.Message}",
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private static void Bump(Dictionary<string, ModelStandardCategoryCount> map, string category, int elementId)
        {
            if (!map.TryGetValue(category, out var bucket))
            {
                bucket = new ModelStandardCategoryCount { Category = category };
                map[category] = bucket;
            }
            bucket.Count++;
            if (bucket.SampleElementIds.Count < 5)
                bucket.SampleElementIds.Add(elementId);
        }

        private static void CollectTypes(Document doc, Dictionary<long, int> instanceCountByTypeId, CheckModelStandardResult result)
        {
            foreach (ElementType type in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                // REV-179, caught live: line-pattern/fill-pattern elements and a few other Revit
                // internals show up here with no Category at all. They are not "family types" in
                // any sense an architect means by that word, and counting them flooded both the
                // unused-type and duplicate-name checks — 1226 "optional" findings on one model,
                // almost all of them the same handful of built-in pattern names repeated.
                if (type.Category == null)
                    continue;

                instanceCountByTypeId.TryGetValue(type.Id.GetIntValue(), out var instanceCount);
                result.Types.Add(new ModelStandardTypeInfo
                {
                    Category = type.Category.Name,
                    FamilyName = (type as FamilySymbol)?.Family?.Name ?? string.Empty,
                    TypeName = type.Name,
                    TypeId = type.Id.GetIntValue(),
                    InstanceCount = instanceCount,
                });
            }
        }

        private static void CollectGroups(Document doc, CheckModelStandardResult result)
        {
            var byType = new Dictionary<long, ModelStandardGroupInfo>();
            foreach (Group group in new FilteredElementCollector(doc).OfClass(typeof(Group)))
            {
                var groupType = group.GroupType;
                if (groupType == null)
                    continue;

                var typeId = groupType.Id.GetIntValue();
                if (!byType.TryGetValue(typeId, out var info))
                {
                    var isDetail = group.Category?.Id.GetIntValue() == (int)BuiltInCategory.OST_IOSDetailGroups;
                    info = new ModelStandardGroupInfo
                    {
                        Name = groupType.Name,
                        Kind = isDetail ? "Detail" : "Model",
                        MemberCount = SafeMemberCount(groupType),
                    };
                    byType[typeId] = info;
                }
                info.InstanceCount++;
            }
            result.Groups = byType.Values.OrderByDescending(g => g.InstanceCount).ToList();
        }

        private static int SafeMemberCount(GroupType groupType)
        {
            try
            {
                return groupType.Groups?.Cast<Group>().FirstOrDefault()?.GetMemberIds().Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static void CollectViews(Document doc, CheckModelStandardResult result)
        {
            foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                if (view.IsTemplate)
                    continue;

                int? scale = null;
                try
                {
                    if (view.CanBePrinted)
                        scale = view.Scale;
                }
                catch
                {
                    // Some view types (schedules, legends) throw on Scale — leave it null.
                }

                string templateName = null;
                var hasTemplate = view.ViewTemplateId != ElementId.InvalidElementId;
                if (hasTemplate)
                {
                    try { templateName = doc.GetElement(view.ViewTemplateId)?.Name; }
                    catch { /* best-effort */ }
                }

                result.Views.Add(new ModelStandardViewInfo
                {
                    Name = view.Name,
                    ViewType = view.ViewType.ToString(),
                    Scale = scale,
                    HasTemplate = hasTemplate,
                    TemplateName = templateName,
                });
            }
        }

        private static void CollectLinks(Document doc, CheckModelStandardResult result)
        {
            foreach (RevitLinkInstance instance in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                string status;
                string name;
                try
                {
                    var linkType = doc.GetElement(instance.GetTypeId()) as RevitLinkType;
                    status = linkType?.GetLinkedFileStatus().ToString() ?? LinkedFileStatus.Invalid.ToString();
                    name = linkType?.Name ?? instance.Name;
                }
                catch
                {
                    status = LinkedFileStatus.Invalid.ToString();
                    name = instance.Name;
                }

                result.Links.Add(new ModelStandardLinkInfo { Name = name, Status = status });
            }
        }

        public string GetName() => "Check Model Standard";
    }
}
