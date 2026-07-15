using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class CreateScheduleDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private ScheduleElementCategory _category;

        public ScheduleExportResult ResultInfo { get; private set; } = new ScheduleExportResult();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(ScheduleElementCategory category)
        {
            _category = category;
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
                var instanceRows = _category switch
                {
                    ScheduleElementCategory.Doors => CollectDoorRows(doc),
                    ScheduleElementCategory.Windows => CollectWindowRows(doc),
                    ScheduleElementCategory.Floors => CollectFloorRows(doc),
                    _ => new List<ScheduleInstanceRow>()
                };

                var instances = instanceRows
                    .Select(ToInstanceExport)
                    .OrderBy(i => i.Level)
                    .ThenBy(i => i.FamilyName)
                    .ThenBy(i => i.Type)
                    .ThenBy(i => i.Id)
                    .ToList();

                var groups = _category == ScheduleElementCategory.Floors
                    ? BuildFloorGroups(instanceRows)
                    : BuildDefaultGroups(instanceRows);

                var totalUnmarked = instanceRows.Count(r => string.IsNullOrWhiteSpace(r.Mark));
                double? totalAreaM2 = _category == ScheduleElementCategory.Floors
                    ? Math.Round(instanceRows.Sum(r => r.AreaM2 ?? 0), 2)
                    : (double?)null;

                var message = _category == ScheduleElementCategory.Floors
                    ? $"Successfully exported floor finish экспликация: {instanceRows.Count} floors, {totalAreaM2:0.##} m² in {groups.Count} type/level groups ({totalUnmarked} without mark)"
                    : $"Successfully exported {instanceRows.Count} {_category.ToString().ToLowerInvariant()} in {groups.Count} groups ({totalUnmarked} without mark)";

                ResultInfo = new ScheduleExportResult
                {
                    Category = _category.ToString().ToLowerInvariant(),
                    TotalCount = instanceRows.Count,
                    UnmarkedCount = totalUnmarked,
                    TotalAreaM2 = totalAreaM2,
                    Instances = instances,
                    Groups = groups,
                    Success = true,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new ScheduleExportResult
                {
                    Category = _category.ToString().ToLowerInvariant(),
                    Success = false,
                    Message = $"Error exporting schedule data: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Create Schedule Data";

        private static List<ScheduleInstanceRow> CollectDoorRows(Document doc) =>
            CollectFamilyInstanceRows(doc, BuiltInCategory.OST_Doors, GetDoorSize, OpeningFillClassifier.IsSchedulableDoor);

        private static List<ScheduleInstanceRow> CollectWindowRows(Document doc) =>
            CollectFamilyInstanceRows(doc, BuiltInCategory.OST_Windows, GetWindowSize, OpeningFillClassifier.IsSchedulableWindow);

        /// <summary>
        /// Floor finish экспликация only: excludes slabs / ceiling insulation / facade (REV-49).
        /// </summary>
        private static List<ScheduleInstanceRow> CollectFloorRows(Document doc)
        {
            var rows = new List<ScheduleInstanceRow>();
            var floors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Floors)
                .WhereElementIsNotElementType()
                .Cast<Floor>();

            foreach (var floor in floors)
            {
                var floorType = doc.GetElement(floor.GetTypeId()) as FloorType;
                var typeName = floorType?.Name ?? "";
                var familyName = floorType?.FamilyName ?? "";
                if (!FloorFinishClassifier.IsFloorFinish(typeName, familyName))
                    continue;

                var level = doc.GetElement(floor.LevelId) as Level;
                var areaM2 = GetFloorAreaM2(floor);
                rows.Add(new ScheduleInstanceRow
                {
                    ElementId = GetElementIdValue(floor.Id),
                    Mark = GetElementMark(floor),
                    FamilyName = familyName,
                    Type = typeName,
                    Size = areaM2 > 0 ? $"{Math.Round(areaM2, 2)} m²" : "",
                    AreaM2 = Math.Round(areaM2, 2),
                    Level = level?.Name ?? "No Level",
                    TypeId = GetElementIdValue(floor.GetTypeId()),
                    Layers = BuildFloorLayers(doc, floorType)
                });
            }

            return rows;
        }

        private static List<ScheduleInstanceRow> CollectFamilyInstanceRows(
            Document doc,
            BuiltInCategory category,
            Func<FamilyInstance, string> sizeResolver,
            Func<FamilyInstance, bool> includePredicate)
        {
            var rows = new List<ScheduleInstanceRow>();
            var instances = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(includePredicate);

            foreach (var instance in instances)
            {
                var symbol = instance.Symbol;
                rows.Add(new ScheduleInstanceRow
                {
                    ElementId = GetElementIdValue(instance.Id),
                    Mark = GetElementMark(instance),
                    FamilyName = symbol?.FamilyName ?? "",
                    Type = symbol?.Name ?? "",
                    Size = sizeResolver(instance),
                    Level = GetInstanceLevelName(doc, instance),
                    TypeId = GetElementIdValue(symbol?.Id ?? ElementId.InvalidElementId)
                });
            }

            return rows;
        }

        private static List<ScheduleGroupRow> BuildDefaultGroups(List<ScheduleInstanceRow> instanceRows)
        {
            return instanceRows
                .GroupBy(r => new { r.TypeId, r.Level, r.Size, r.Type, r.FamilyName })
                .Select(g =>
                {
                    var elementIds = g.Select(x => x.ElementId).OrderBy(id => id).ToList();
                    var unmarkedCount = g.Count(x => string.IsNullOrWhiteSpace(x.Mark));
                    return new ScheduleGroupRow
                    {
                        TypeId = g.Key.TypeId,
                        FamilyName = g.Key.FamilyName,
                        Type = g.Key.Type,
                        Size = g.Key.Size,
                        Level = g.Key.Level,
                        Count = g.Count(),
                        UnmarkedCount = unmarkedCount,
                        ElementIds = elementIds,
                        Mark = BuildGroupMark(g.Select(x => x.Mark))
                    };
                })
                .OrderBy(g => g.Level)
                .ThenBy(g => g.FamilyName)
                .ThenBy(g => g.Type)
                .ToList();
        }

        /// <summary>
        /// Group floor finishes by type + level and sum areas (экспликация), not by per-instance size.
        /// </summary>
        private static List<ScheduleGroupRow> BuildFloorGroups(List<ScheduleInstanceRow> instanceRows)
        {
            return instanceRows
                .GroupBy(r => new { r.TypeId, r.Level, r.Type, r.FamilyName })
                .Select(g =>
                {
                    var areaM2 = Math.Round(g.Sum(x => x.AreaM2 ?? 0), 2);
                    var elementIds = g.Select(x => x.ElementId).OrderBy(id => id).ToList();
                    var unmarkedCount = g.Count(x => string.IsNullOrWhiteSpace(x.Mark));
                    var layers = g.Select(x => x.Layers).FirstOrDefault(l => l != null && l.Count > 0);
                    return new ScheduleGroupRow
                    {
                        TypeId = g.Key.TypeId,
                        FamilyName = g.Key.FamilyName,
                        Type = g.Key.Type,
                        Size = areaM2 > 0 ? $"{areaM2} m²" : "",
                        AreaM2 = areaM2,
                        Level = g.Key.Level,
                        Count = g.Count(),
                        UnmarkedCount = unmarkedCount,
                        ElementIds = elementIds,
                        Mark = BuildGroupMark(g.Select(x => x.Mark)),
                        Layers = layers
                    };
                })
                .OrderBy(g => g.Level)
                .ThenBy(g => g.Type)
                .ToList();
        }

        private static ScheduleInstanceExport ToInstanceExport(ScheduleInstanceRow row) =>
            new ScheduleInstanceExport
            {
                Id = row.ElementId,
                Mark = row.Mark,
                FamilyName = row.FamilyName,
                Type = row.Type,
                Size = row.Size,
                Level = row.Level,
                TypeId = row.TypeId,
                AreaM2 = row.AreaM2,
                Layers = row.Layers
            };

        private static string GetDoorSize(FamilyInstance instance)
        {
            double width = GetParameterDouble(instance, BuiltInParameter.DOOR_WIDTH);
            double height = GetParameterDouble(instance, BuiltInParameter.DOOR_HEIGHT);
            return FormatWidthHeightMm(width, height, instance.Symbol?.Name);
        }

        private static string GetWindowSize(FamilyInstance instance)
        {
            double width = GetParameterDouble(instance, BuiltInParameter.WINDOW_WIDTH);
            double height = GetParameterDouble(instance, BuiltInParameter.WINDOW_HEIGHT);
            if (width <= 0) width = GetParameterDouble(instance, BuiltInParameter.FAMILY_WIDTH_PARAM);
            if (height <= 0) height = GetParameterDouble(instance, BuiltInParameter.FAMILY_HEIGHT_PARAM);
            return FormatWidthHeightMm(width, height, instance.Symbol?.Name);
        }

        private static string FormatWidthHeightMm(double widthFeet, double heightFeet, string fallback)
        {
            if (widthFeet > 0 && heightFeet > 0)
            {
                int widthMm = (int)Math.Round(widthFeet * 304.8);
                int heightMm = (int)Math.Round(heightFeet * 304.8);
                return $"{widthMm} x {heightMm} mm";
            }

            return fallback ?? "";
        }

        private static double GetFloorAreaM2(Floor floor)
        {
            double areaInternal = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
            return areaInternal > 0 ? RevitUnitConversion.ToSquareMeters(areaInternal) : 0;
        }

        private static List<FloorLayerExport> BuildFloorLayers(Document doc, FloorType floorType)
        {
            var structure = floorType?.GetCompoundStructure();
            if (structure == null)
                return null;

            var layers = new List<FloorLayerExport>();
            for (int i = 0; i < structure.LayerCount; i++)
            {
                var layer = structure.GetLayers()[i];
                var materialName = "";
                if (layer.MaterialId != null && layer.MaterialId != ElementId.InvalidElementId)
                {
                    var material = doc.GetElement(layer.MaterialId) as Material;
                    materialName = material?.Name ?? "";
                }

                layers.Add(new FloorLayerExport
                {
                    Function = layer.Function.ToString(),
                    Material = materialName,
                    ThicknessMm = Math.Round(RevitUnitConversion.ToMillimeters(layer.Width), 1)
                });
            }

            return layers.Count > 0 ? layers : null;
        }

        private static string GetInstanceLevelName(Document doc, FamilyInstance instance)
        {
            var levelId = instance.LevelId;
            if (levelId != null && levelId != ElementId.InvalidElementId)
            {
                return (doc.GetElement(levelId) as Level)?.Name ?? "No Level";
            }

            var host = instance.Host;
            if (host != null)
            {
                var hostLevelId = host.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId();
                if (hostLevelId != null && hostLevelId != ElementId.InvalidElementId)
                {
                    return (doc.GetElement(hostLevelId) as Level)?.Name ?? "No Level";
                }
            }

            return "No Level";
        }

        private static string GetParameterString(Element element, BuiltInParameter parameter)
        {
            return element.get_Parameter(parameter)?.AsString() ?? "";
        }

        /// <summary>
        /// Reads mark when present, but never excludes the element when mark is empty.
        /// </summary>
        private static string GetElementMark(Element element)
        {
            var candidates = new[]
            {
                GetParameterString(element, BuiltInParameter.ALL_MODEL_MARK),
                GetParameterString(element, BuiltInParameter.DOOR_NUMBER),
                element.LookupParameter("Марка")?.AsString() ?? "",
                element.LookupParameter("Mark")?.AsString() ?? ""
            };

            return candidates.FirstOrDefault(mark => !string.IsNullOrWhiteSpace(mark)) ?? "";
        }

        private static double GetParameterDouble(Element element, BuiltInParameter parameter)
        {
            var param = element.get_Parameter(parameter);
            return param?.AsDouble() ?? 0;
        }

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        private static string BuildGroupMark(IEnumerable<string> marks)
        {
            var distinct = marks
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .OrderBy(m => m)
                .ToList();

            return distinct.Count == 0 ? "" : string.Join(", ", distinct);
        }

        private sealed class ScheduleInstanceRow
        {
            public long ElementId { get; set; }
            public string Mark { get; set; } = "";
            public string FamilyName { get; set; } = "";
            public string Type { get; set; } = "";
            public string Size { get; set; } = "";
            public double? AreaM2 { get; set; }
            public string Level { get; set; } = "";
            public long TypeId { get; set; }
            public List<FloorLayerExport> Layers { get; set; }
        }
    }
}
