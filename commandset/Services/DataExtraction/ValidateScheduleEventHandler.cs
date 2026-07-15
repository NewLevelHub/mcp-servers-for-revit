using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Services;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class ValidateScheduleEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private sealed class CategoryConfig
        {
            public BuiltInCategory BuiltInCategory { get; set; }

            /// <summary>
            ///     Curtain wall systems live in OST_Walls but are validated as their own
            ///     pseudo-category (витражи в РД — не окна и не все стены).
            /// </summary>
            public bool CurtainWallsOnly { get; set; }
        }

        private static readonly Dictionary<string, CategoryConfig> CategoryMap =
            new Dictionary<string, CategoryConfig>(StringComparer.OrdinalIgnoreCase)
            {
                { "Doors", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Doors } },
                { "Windows", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Windows } },
                { "Floors", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Floors } },
                { "OST_Doors", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Doors } },
                { "OST_Windows", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Windows } },
                { "OST_Floors", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Floors } },
                { "CurtainWalls", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Walls, CurtainWallsOnly = true } },
                { "CurtainWall", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Walls, CurtainWallsOnly = true } },
                { "Витражи", new CategoryConfig { BuiltInCategory = BuiltInCategory.OST_Walls, CurtainWallsOnly = true } },
            };

        private string _category;
        private string _scheduleName;
        private string _levelName;
        private long? _levelId;

        public ValidateScheduleResult ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(string category, string scheduleName = null, string levelName = null, long? levelId = null)
        {
            _category = category;
            _scheduleName = scheduleName;
            _levelName = levelName;
            _levelId = levelId;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;

                if (string.IsNullOrWhiteSpace(_category))
                {
                    throw new ArgumentException(
                        "Category is required. Supported values: Doors, Windows, Floors, CurtainWalls.");
                }

                if (!CategoryMap.TryGetValue(_category.Trim(), out var categoryConfig))
                {
                    throw new ArgumentException(
                        $"Unsupported category '{_category}'. Supported values: Doors, Windows, Floors, CurtainWalls.");
                }

                var builtInCategory = categoryConfig.BuiltInCategory;
                var targetLevel = ResolveLevel(doc);
                var schedule = FindSchedule(doc, categoryConfig, _scheduleName);
                if (schedule == null)
                {
                    var scheduleHint = string.IsNullOrWhiteSpace(_scheduleName)
                        ? $"No schedule found for category {_category}."
                        : $"No schedule named '{_scheduleName}' found for category {_category}.";
                    throw new InvalidOperationException(scheduleHint);
                }

                var modelIds = CollectModelElementIds(doc, categoryConfig, targetLevel);
                var scheduleIds = CollectScheduleElementIds(doc, schedule, categoryConfig, targetLevel);

                var missingIds = modelIds
                    .Except(scheduleIds)
                    .Select(GetElementIdValue)
                    .OrderBy(id => id)
                    .ToList();

                var modelCount = modelIds.Count;
                var scheduleCount = scheduleIds.Count;

                ResultInfo = new ValidateScheduleResult
                {
                    Category = _category,
                    ScheduleName = schedule.Name,
                    LevelName = targetLevel?.Name,
                    ModelCount = modelCount,
                    ScheduleCount = scheduleCount,
                    Diff = modelCount - scheduleCount,
                    MissingIds = missingIds,
                    Success = true,
                    Message = missingIds.Count == 0
                        ? $"Schedule '{schedule.Name}' matches the model for {_category} ({modelCount} elements)."
                        : $"Schedule '{schedule.Name}' is missing {missingIds.Count} of {modelCount} model elements for {_category}."
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new ValidateScheduleResult
                {
                    Category = _category,
                    ScheduleName = _scheduleName,
                    LevelName = _levelName,
                    Success = false,
                    Message = $"Error validating schedule: {ex.Message}"
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
            return "Validate Schedule";
        }

        private Level ResolveLevel(Document doc)
        {
            if (_levelId.HasValue)
            {
#if REVIT2024_OR_GREATER
                var levelById = doc.GetElement(new ElementId(_levelId.Value)) as Level;
#else
                var levelById = doc.GetElement(new ElementId((int)_levelId.Value)) as Level;
#endif
                if (levelById == null)
                {
                    throw new ArgumentException($"Level with id {_levelId.Value} was not found.");
                }

                return levelById;
            }

            if (!string.IsNullOrWhiteSpace(_levelName))
            {
                var levelByName = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(level => level.Name.Equals(_levelName.Trim(), StringComparison.OrdinalIgnoreCase));

                if (levelByName == null)
                {
                    throw new ArgumentException($"Level '{_levelName}' was not found.");
                }

                return levelByName;
            }

            return null;
        }

        private static ViewSchedule FindSchedule(Document doc, CategoryConfig categoryConfig, string scheduleName)
        {
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(schedule => !schedule.IsTemplate)
                .ToList();

            if (!string.IsNullOrWhiteSpace(scheduleName))
            {
                return schedules.FirstOrDefault(schedule =>
                    schedule.Name.Equals(scheduleName.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            var category = Category.GetCategory(doc, categoryConfig.BuiltInCategory);
            var categorySchedules = schedules
                .Where(schedule => schedule.Definition.CategoryId == category.Id)
                .ToList();

            if (categoryConfig.CurtainWallsOnly)
            {
                // Curtain wall specs are Walls-category schedules; prefer the dedicated
                // 'Спецификация витражей' over generic wall schedules.
                var curtainSchedule = categorySchedules.FirstOrDefault(schedule =>
                    schedule.Name.IndexOf("витраж", StringComparison.OrdinalIgnoreCase) >= 0);
                if (curtainSchedule != null)
                    return curtainSchedule;
            }

            return categorySchedules.FirstOrDefault();
        }

        private HashSet<ElementId> CollectModelElementIds(Document doc, CategoryConfig categoryConfig, Level targetLevel)
        {
            var elements = new FilteredElementCollector(doc)
                .OfCategory(categoryConfig.BuiltInCategory)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(element => IsSchedulableModelElement(element, categoryConfig))
                .ToList();

            return FilterElementsByLevel(doc, elements, targetLevel);
        }

        /// <summary>
        /// Applies the same filters as the export commands: opening-fill filter for
        /// doors/windows (REV-41) and the curtain wall system filter for витражи —
        /// only Wall elements with WallKind.Curtain count, never panels or mullions.
        /// </summary>
        private static bool IsSchedulableModelElement(Element element, CategoryConfig categoryConfig)
        {
            if (categoryConfig.CurtainWallsOnly)
                return CurtainWallClassifier.IsCurtainWall(element);

            return categoryConfig.BuiltInCategory switch
            {
                BuiltInCategory.OST_Doors => OpeningFillClassifier.IsSchedulableDoor(element),
                BuiltInCategory.OST_Windows => OpeningFillClassifier.IsSchedulableWindow(element),
                _ => true
            };
        }

        private HashSet<ElementId> CollectScheduleElementIds(
            Document doc,
            ViewSchedule schedule,
            CategoryConfig categoryConfig,
            Level targetLevel)
        {
            var elements = new FilteredElementCollector(doc, schedule.Id)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(element => !categoryConfig.CurtainWallsOnly || CurtainWallClassifier.IsCurtainWall(element))
                .ToList();

            return FilterElementsByLevel(doc, elements, targetLevel);
        }

        private static HashSet<ElementId> FilterElementsByLevel(Document doc, ICollection<Element> elements, Level targetLevel)
        {
            if (targetLevel == null)
            {
                return elements.Select(element => element.Id).ToHashSet();
            }

            var targetLevelId = targetLevel.Id.GetIntValue();
            return elements
                .Where(element =>
                {
                    var levelInfo = AIElementFilterEventHandler.GetElementLevel(doc, element);
                    return levelInfo != null && levelInfo.Id == targetLevelId;
                })
                .Select(element => element.Id)
                .ToHashSet();
        }

        private static long GetElementIdValue(ElementId elementId) => elementId.GetValue();
    }
}
