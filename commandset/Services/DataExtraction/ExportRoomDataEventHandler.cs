using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class ExportRoomDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private bool _includeUnplacedRooms;
        private bool _includeNotEnclosedRooms;

        public ExportRoomDataResult ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(bool includeUnplacedRooms = false, bool includeNotEnclosedRooms = false)
        {
            _includeUnplacedRooms = includeUnplacedRooms;
            _includeNotEnclosedRooms = includeNotEnclosedRooms;
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
                var rooms = new List<RoomDataModel>();
                double totalArea = 0;

                var levelsByElevation = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                var floorThicknessByLevelId = BuildFloorThicknessByLevel(doc);

                // Collect all rooms in the project
                var roomCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>();

                foreach (Room room in roomCollector)
                {
                    // Skip unplaced rooms if not included
                    if (!_includeUnplacedRooms && room.Area == 0)
                        continue;

                    // Skip not enclosed rooms if not included
                    if (!_includeNotEnclosedRooms && room.Area == 0)
                        continue;

                    double areaSquareMeters = RevitUnitConversion.ToSquareMeters(room.Area);
                    double unboundedMm = RevitUnitConversion.ToMillimeters(room.UnboundedHeight);

                    string upperLimitName = "";
                    double limitOffsetMm = 0;
                    var upperLimitParam = room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL);
                    if (upperLimitParam != null && upperLimitParam.HasValue)
                    {
                        var upperLevel = doc.GetElement(upperLimitParam.AsElementId()) as Level;
                        upperLimitName = upperLevel?.Name ?? "";
                    }
                    var limitOffsetParam = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                    if (limitOffsetParam != null && limitOffsetParam.HasValue)
                    {
                        limitOffsetMm = RevitUnitConversion.ToMillimeters(limitOffsetParam.AsDouble());
                    }

                    double storeyHeightMm = 0;
                    double floorThicknessMm = 0;
                    double clearHeightMm = unboundedMm;
                    string heightSource = "room_unbounded";

                    Level roomLevel = room.Level;
                    if (roomLevel != null)
                    {
                        Level nextLevel = FindNextLevelAbove(levelsByElevation, roomLevel);
                        if (nextLevel != null)
                        {
                            storeyHeightMm = RevitUnitConversion.ToMillimeters(
                                nextLevel.Elevation - roomLevel.Elevation);

                            if (floorThicknessByLevelId.TryGetValue(
#if REVIT2024_OR_GREATER
                                    nextLevel.Id.Value,
#else
                                    nextLevel.Id.IntegerValue,
#endif
                                    out double thicknessMm))
                            {
                                floorThicknessMm = thicknessMm;
                            }

                            if (storeyHeightMm > 0)
                            {
                                // Prefer level-based clear height: floor-to-floor minus slab on upper level.
                                // Default 300 mm when no floor type thickness is available (typical RC slab).
                                double slab = floorThicknessMm > 0 ? floorThicknessMm : 300.0;
                                clearHeightMm = Math.Max(0, storeyHeightMm - slab);
                                heightSource = floorThicknessMm > 0
                                    ? "level_clear"
                                    : "level_clear_default_slab";
                            }
                        }
                    }

                    var roomData = new RoomDataModel
                    {
#if REVIT2024_OR_GREATER
                        Id = room.Id.Value,
#else
                        Id = room.Id.IntegerValue,
#endif
                        UniqueId = room.UniqueId,
                        Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                        Number = room.Number ?? "",
                        Level = room.Level?.Name ?? "No Level",
                        Area = areaSquareMeters,
                        Volume = RevitUnitConversion.ToCubicMeters(room.Volume),
                        Perimeter = RevitUnitConversion.ToMillimeters(room.Perimeter),
                        UnboundedHeight = unboundedMm,
                        StoreyHeight = storeyHeightMm,
                        FloorThickness = floorThicknessMm,
                        ClearHeight = clearHeightMm,
                        HeightSource = heightSource,
                        UpperLimitLevel = upperLimitName,
                        LimitOffset = limitOffsetMm,
                        Department = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "",
                        Comments = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
                        Phase = doc.GetElement(room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId())?.Name ?? "",
                        Occupancy = room.get_Parameter(BuiltInParameter.ROOM_OCCUPANCY)?.AsString() ?? ""
                    };

                    rooms.Add(roomData);
                    totalArea += areaSquareMeters;
                }

                ResultInfo = new ExportRoomDataResult
                {
                    TotalRooms = rooms.Count,
                    TotalArea = totalArea,
                    Rooms = rooms,
                    Success = true,
                    Message = $"Successfully exported {rooms.Count} rooms"
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new ExportRoomDataResult
                {
                    Success = false,
                    Message = $"Error exporting room data: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private static Level FindNextLevelAbove(List<Level> levelsByElevation, Level roomLevel)
        {
            foreach (var level in levelsByElevation)
            {
                if (level.Elevation > roomLevel.Elevation + 1e-6)
                    return level;
            }
            return null;
        }

        /// <summary>
        /// Median compound-structure thickness of Floor elements hosted on each level (mm).
        /// </summary>
        private static Dictionary<long, double> BuildFloorThicknessByLevel(Document doc)
        {
            var thicknesses = new Dictionary<long, List<double>>();
            var floors = new FilteredElementCollector(doc)
                .OfClass(typeof(Floor))
                .WhereElementIsNotElementType()
                .Cast<Floor>();

            foreach (Floor floor in floors)
            {
                ElementId levelId = floor.LevelId;
                if (levelId == ElementId.InvalidElementId)
                    continue;

                var floorType = doc.GetElement(floor.GetTypeId()) as FloorType;
                var structure = floorType?.GetCompoundStructure();
                if (structure == null)
                    continue;

                double thicknessMm = RevitUnitConversion.ToMillimeters(structure.GetWidth());
                if (thicknessMm <= 0)
                    continue;

#if REVIT2024_OR_GREATER
                long key = levelId.Value;
#else
                long key = levelId.IntegerValue;
#endif
                if (!thicknesses.TryGetValue(key, out var list))
                {
                    list = new List<double>();
                    thicknesses[key] = list;
                }
                list.Add(thicknessMm);
            }

            var medians = new Dictionary<long, double>();
            foreach (var kv in thicknesses)
            {
                var list = kv.Value;
                list.Sort();
                medians[kv.Key] = list[list.Count / 2];
            }
            return medians;
        }

        public string GetName()
        {
            return "Export Room Data";
        }
    }
}
