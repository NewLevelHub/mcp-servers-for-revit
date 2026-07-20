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
        private const double FloorAreaToleranceM2 = 1.0;

        private sealed class CategoryConfig
        {
            public BuiltInCategory BuiltInCategory { get; set; }

            /// <summary>
            /// Curtain wall systems live in OST_Walls but are validated as their own
            /// pseudo-category (витражи в РД — не окна и не все стены).
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

                var targetLevel = ResolveLevel(doc);
                var schedule = FindSchedule(doc, categoryConfig, _scheduleName);
                if (schedule == null)
                {
                    var scheduleHint = string.IsNullOrWhiteSpace(_scheduleName)
                        ? $"No schedule found for category {_category}."
                        : $"No schedule named '{_scheduleName}' found for category {_category}.";
                    throw new InvalidOperationException(scheduleHint);
                }

                if (categoryConfig.BuiltInCategory == BuiltInCategory.OST_Floors && !categoryConfig.CurtainWallsOnly)
                {
                    ResultInfo = ValidateFloorAreas(doc, schedule, targetLevel);
                }
                else
                {
                    ResultInfo = ValidateElementIds(doc, categoryConfig, schedule, targetLevel);
                }
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

        private ValidateScheduleResult ValidateElementIds(
            Document doc,
            CategoryConfig categoryConfig,
            ViewSchedule schedule,
            Level targetLevel)
        {
            var modelIds = CollectModelElementIds(doc, categoryConfig, targetLevel);
            var scheduleIds = CollectScheduleElementIds(doc, schedule, categoryConfig, targetLevel);

            var missingIds = modelIds
                .Except(scheduleIds)
                .Select(GetElementIdValue)
                .OrderBy(id => id)
                .ToList();

            var modelCount = modelIds.Count;
            var scheduleCount = scheduleIds.Count;

            return new ValidateScheduleResult
            {
                Category = _category,
                ScheduleName = schedule.Name,
                LevelName = targetLevel?.Name,
                Mode = "elements",
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

        /// <summary>
        /// Floor экспликация: compare finish-floor areas (m²) by type, not counts vs key schedule (REV-49).
        /// </summary>
        private ValidateScheduleResult ValidateFloorAreas(Document doc, ViewSchedule schedule, Level targetLevel)
        {
            var modelFloors = CollectFloorFinishElements(doc, targetLevel, scheduleId: null);
            var scheduleFloors = CollectFloorFinishElements(doc, targetLevel, schedule.Id);

            var modelByType = AggregateAreasByType(doc, modelFloors);
            var scheduleByType = AggregateAreasByType(doc, scheduleFloors);

            var allTypes = modelByType.Keys
                .Union(scheduleByType.Keys)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var typeRows = new List<ValidateScheduleTypeAreaRow>();
            foreach (var typeName in allTypes)
            {
                modelByType.TryGetValue(typeName, out var typeModelArea);
                scheduleByType.TryGetValue(typeName, out var typeScheduleArea);
                typeRows.Add(new ValidateScheduleTypeAreaRow
                {
                    Type = typeName,
                    ModelAreaM2 = Math.Round(typeModelArea, 2),
                    ScheduleAreaM2 = Math.Round(typeScheduleArea, 2),
                    DiffM2 = Math.Round(typeModelArea - typeScheduleArea, 2)
                });
            }

            var modelArea = Math.Round(modelByType.Values.Sum(), 2);
            var scheduleArea = Math.Round(scheduleByType.Values.Sum(), 2);
            var areaDiff = Math.Round(modelArea - scheduleArea, 2);
            var mismatchedTypes = typeRows.Count(r => Math.Abs(r.DiffM2) > FloorAreaToleranceM2);

            var modelIds = modelFloors.Select(f => f.Id).ToHashSet();
            var scheduleIds = scheduleFloors.Select(f => f.Id).ToHashSet();
            var missingIds = modelIds
                .Except(scheduleIds)
                .Select(GetElementIdValue)
                .OrderBy(id => id)
                .ToList();

            var areasMatch = Math.Abs(areaDiff) <= FloorAreaToleranceM2 && mismatchedTypes == 0;
            string message;
            if (scheduleFloors.Count == 0 && IsKeyOrStyleScheduleName(schedule.Name))
            {
                message =
                    $"Schedule '{schedule.Name}' looks like a key/style schedule, not экспликация полов. " +
                    $"Model finish floors: {modelFloors.Count} ({modelArea:0.##} m²). " +
                    "Pass scheduleName of the floor экспликация / area schedule to compare areas.";
            }
            else if (areasMatch)
            {
                message =
                    $"Floor экспликация '{schedule.Name}' matches model finish areas " +
                    $"({modelArea:0.##} m², {modelFloors.Count} floors, tolerance ±{FloorAreaToleranceM2:0.#} m²).";
            }
            else
            {
                message =
                    $"Floor экспликация '{schedule.Name}' area mismatch: model {modelArea:0.##} m² vs schedule {scheduleArea:0.##} m² " +
                    $"(diff {areaDiff:0.##} m², {mismatchedTypes} type(s) beyond ±{FloorAreaToleranceM2:0.#} m²).";
            }

            return new ValidateScheduleResult
            {
                Category = _category,
                ScheduleName = schedule.Name,
                LevelName = targetLevel?.Name,
                Mode = "floor_areas",
                ModelCount = modelFloors.Count,
                ScheduleCount = scheduleFloors.Count,
                Diff = modelFloors.Count - scheduleFloors.Count,
                MissingIds = missingIds,
                ModelAreaM2 = modelArea,
                ScheduleAreaM2 = scheduleArea,
                AreaDiffM2 = areaDiff,
                TypeAreas = typeRows,
                Success = true,
                Message = message
            };
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

            if (categoryConfig.BuiltInCategory == BuiltInCategory.OST_Floors)
            {
                var explication = categorySchedules.FirstOrDefault(s => NameLooksLikeFloorExplication(s.Name));
                if (explication != null)
                    return explication;

                var nonKey = categorySchedules.FirstOrDefault(s => !IsKeyOrStyleScheduleName(s.Name));
                if (nonKey != null)
                    return nonKey;
            }

            return categorySchedules.FirstOrDefault();
        }

        private static bool NameLooksLikeFloorExplication(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var n = name.ToLowerInvariant();
            return n.Contains("экспликац")
                   || n.Contains("explicat")
                   || (n.Contains("ведомост") && n.Contains("пол"))
                   || (n.Contains("спецификац") && n.Contains("пол") && !IsKeyOrStyleScheduleName(n));
        }

        private static bool IsKeyOrStyleScheduleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var n = name.ToLowerInvariant();
            return n.Contains("ключев")
                   || n.Contains("стил")
                   || n.Contains("key schedule")
                   || n.Contains("keynote");
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

        private static List<Floor> CollectFloorFinishElements(Document doc, Level targetLevel, ElementId scheduleId)
        {
            IList<Element> elements = scheduleId == null
                ? new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .ToElements()
                : new FilteredElementCollector(doc, scheduleId)
                    .WhereElementIsNotElementType()
                    .ToElements();

            var floors = elements
                .OfType<Floor>()
                .Where(FloorFinishClassifier.IsFloorFinish)
                .Cast<Element>()
                .ToList();

            var filteredIds = FilterElementsByLevel(doc, floors, targetLevel);
            return filteredIds
                .Select(id => doc.GetElement(id) as Floor)
                .Where(f => f != null)
                .ToList();
        }

        private static Dictionary<string, double> AggregateAreasByType(Document doc, IEnumerable<Floor> floors)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var floor in floors)
            {
                var floorType = doc.GetElement(floor.GetTypeId()) as FloorType;
                var typeName = floorType?.Name ?? floor.Name ?? "(unnamed)";
                var area = GetFloorAreaM2(floor);
                if (result.ContainsKey(typeName))
                    result[typeName] += area;
                else
                    result[typeName] = area;
            }

            return result;
        }

        private static double GetFloorAreaM2(Floor floor)
        {
            double areaInternal = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
            return areaInternal > 0 ? RevitUnitConversion.ToSquareMeters(areaInternal) : 0;
        }

        /// <summary>
        /// Applies the same filters as the export commands: opening-fill for doors/windows (REV-41),
        /// finish floors only (REV-49), and curtain wall systems for витражи (REV-53).
        /// </summary>
        private static bool IsSchedulableModelElement(Element element, CategoryConfig categoryConfig)
        {
            if (categoryConfig.CurtainWallsOnly)
                return CurtainWallClassifier.IsCurtainWall(element);

            return categoryConfig.BuiltInCategory switch
            {
                BuiltInCategory.OST_Doors => OpeningFillClassifier.IsSchedulableDoor(element),
                BuiltInCategory.OST_Windows => OpeningFillClassifier.IsSchedulableWindow(element),
                BuiltInCategory.OST_Floors => element is Floor floor && FloorFinishClassifier.IsFloorFinish(floor),
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
                .Where(element => IsSchedulableModelElement(element, categoryConfig))
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
