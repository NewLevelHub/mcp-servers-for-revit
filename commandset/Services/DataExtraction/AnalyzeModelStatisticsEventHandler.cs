using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class AnalyzeModelStatisticsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private bool _includeDetailedTypes;

        public AnalyzeModelStatisticsResult ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(bool includeDetailedTypes = true)
        {
            _includeDetailedTypes = includeDetailedTypes;
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
                string projectName = doc.Title;

                int totalElements = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                int totalTypes = new FilteredElementCollector(doc)
                    .WhereElementIsElementType()
                    .GetElementCount();

                int totalViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Count(v => !v.IsTemplate);

                int totalSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .GetElementCount();

                var categoryStats = new Dictionary<string, CategoryStatistics>();
                var familyNames = new HashSet<string>();
                var levelCounts = new Dictionary<ElementId, int>();

                // Single pass: category/type stats + level counts (no ToElements, no per-level re-scan).
                foreach (Element elem in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    if (elem.Category != null)
                    {
                        string catName = elem.Category.Name;

                        if (!categoryStats.TryGetValue(catName, out var stat))
                        {
                            stat = new CategoryStatistics { CategoryName = catName };
                            categoryStats[catName] = stat;
                        }

                        stat.ElementCount++;

                        if (elem is FamilyInstance fi)
                        {
                            string familyName = fi.Symbol?.Family?.Name;
                            string typeName = fi.Symbol?.Name;

                            if (!string.IsNullOrEmpty(familyName))
                                familyNames.Add(familyName);

                            if (_includeDetailedTypes && !string.IsNullOrEmpty(typeName))
                            {
                                var existingType = stat.Types
                                    .FirstOrDefault(t => t.TypeName == typeName && t.FamilyName == familyName);

                                if (existingType != null)
                                {
                                    existingType.InstanceCount++;
                                }
                                else
                                {
                                    stat.Types.Add(new TypeStatistics
                                    {
                                        TypeName = typeName,
                                        FamilyName = familyName,
                                        InstanceCount = 1
                                    });
                                }
                            }
                        }
                    }

                    ElementId levelId = elem.LevelId;
                    if (levelId != null && levelId != ElementId.InvalidElementId)
                    {
                        if (levelCounts.TryGetValue(levelId, out int count))
                            levelCounts[levelId] = count + 1;
                        else
                            levelCounts[levelId] = 1;
                    }
                }

                foreach (var stat in categoryStats.Values)
                {
                    stat.TypeCount = stat.Types.Select(t => t.TypeName).Distinct().Count();
                    stat.FamilyCount = stat.Types.Select(t => t.FamilyName).Distinct().Count();
                }

                var levelStats = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(level => new LevelStatistics
                    {
                        LevelName = level.Name,
                        Elevation = level.Elevation,
                        ElementCount = levelCounts.TryGetValue(level.Id, out int count) ? count : 0
                    })
                    .ToList();

                ResultInfo = new AnalyzeModelStatisticsResult
                {
                    ProjectName = projectName,
                    TotalElements = totalElements,
                    TotalTypes = totalTypes,
                    TotalFamilies = familyNames.Count,
                    TotalViews = totalViews,
                    TotalSheets = totalSheets,
                    Categories = categoryStats.Values.OrderByDescending(c => c.ElementCount).ToList(),
                    Levels = levelStats,
                    Success = true,
                    Message = $"Successfully analyzed model with {totalElements} elements across {categoryStats.Count} categories"
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new AnalyzeModelStatisticsResult
                {
                    Success = false,
                    Message = $"Error analyzing model statistics: {ex.Message}"
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
            return "Analyze Model Statistics";
        }
    }
}
