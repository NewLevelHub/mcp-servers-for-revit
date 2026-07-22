using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    /// Exports the egress walkability graph of a floor: rooms with their outer
    /// boundary polygons and doors with from/to room links (all coordinates mm).
    /// The server builds the path graph and traces evacuation routes on it.
    /// </summary>
    public class ExportEgressGraphEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _levelName;

        public ExportEgressGraphResult ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(string levelName = null)
        {
            _levelName = levelName?.Trim() ?? string.Empty;
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
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var doc = app.ActiveUIDocument.Document;
                ResultInfo = Compute(doc, _levelName);
            }
            catch (Exception ex)
            {
                ResultInfo = new ExportEgressGraphResult
                {
                    Success = false,
                    Message = $"Error exporting egress graph: {ex.Message}"
                };
            }
            finally
            {
                if (ResultInfo != null)
                {
                    stopwatch.Stop();
                    ResultInfo.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;
                }

                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public static ExportEgressGraphResult Compute(Document doc, string levelName)
        {
            var warnings = new List<string>();

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(room => room.Area > 0)
                .Where(room => string.IsNullOrEmpty(levelName) ||
                               string.Equals(room.Level?.Name, levelName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var boundaryOptions = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };

            var roomExports = new List<EgressRoomExport>();
            foreach (var room in rooms)
            {
                var boundary = ExtractOuterBoundary(room, boundaryOptions);
                if (boundary.Count < 3)
                {
                    warnings.Add($"Room '{room.Name}' ({room.Id.GetValue()}): no readable boundary, skipped.");
                    continue;
                }

                roomExports.Add(new EgressRoomExport
                {
                    Id = room.Id.GetValue(),
                    UniqueId = room.UniqueId,
                    Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name ?? string.Empty,
                    Number = room.Number ?? string.Empty,
                    Level = room.Level?.Name ?? string.Empty,
                    Centroid = ComputeCentroid(boundary),
                    Boundary = boundary
                });
            }

            var roomIds = new HashSet<long>(roomExports.Select(room => room.Id));

            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(door => string.IsNullOrEmpty(levelName) ||
                               string.Equals(GetDoorLevelName(door), levelName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var doorExports = new List<EgressDoorExport>();
            foreach (var door in doors)
            {
                if (door.Location is not LocationPoint location)
                    continue;

                long? fromId = door.FromRoom?.Id.GetValue();
                long? toId = door.ToRoom?.Id.GetValue();

                // Keep only doors touching at least one exported room; a door to an
                // out-of-scope room keeps the link so the server can flag it.
                if ((fromId == null || !roomIds.Contains(fromId.Value)) &&
                    (toId == null || !roomIds.Contains(toId.Value)))
                    continue;

                doorExports.Add(new EgressDoorExport
                {
                    Id = door.Id.GetValue(),
                    UniqueId = door.UniqueId,
                    Name = $"{door.Symbol?.Family?.Name} : {door.Symbol?.Name}".Trim(' ', ':'),
                    Level = GetDoorLevelName(door),
                    X = RevitUnitConversion.ToMillimeters(location.Point.X),
                    Y = RevitUnitConversion.ToMillimeters(location.Point.Y),
                    FromRoomId = fromId,
                    ToRoomId = toId,
                    WidthMm = GetDoorWidthMm(door),
                    IsExteriorWall = IsHostWallExterior(door)
                });
            }

            if (doorExports.Count == 0)
                warnings.Add("No doors linked to exported rooms were found on the level.");

            return new ExportEgressGraphResult
            {
                Success = true,
                Message =
                    $"Egress graph: {roomExports.Count} rooms, {doorExports.Count} doors" +
                    (string.IsNullOrEmpty(levelName) ? "." : $" on level '{levelName}'."),
                LevelName = levelName ?? string.Empty,
                Rooms = roomExports,
                Doors = doorExports,
                Warnings = warnings
            };
        }

        /// <summary>Outer loop = the boundary loop with the largest bounding box.</summary>
        private static List<EgressPoint> ExtractOuterBoundary(Room room, SpatialElementBoundaryOptions options)
        {
            var loops = room.GetBoundarySegments(options);
            if (loops == null || loops.Count == 0)
                return new List<EgressPoint>();

            List<EgressPoint> best = null;
            double bestExtent = -1;

            foreach (IList<BoundarySegment> loop in loops)
            {
                if (loop == null || loop.Count < 3)
                    continue;

                var points = new List<EgressPoint>();
                foreach (var segment in loop)
                {
                    var curve = segment?.GetCurve();
                    if (curve == null)
                        continue;

                    // Tessellate keeps arcs walkable as short chords.
                    var tessellated = curve.Tessellate();
                    for (var i = 0; i < tessellated.Count - 1; i++)
                    {
                        points.Add(new EgressPoint
                        {
                            X = RevitUnitConversion.ToMillimeters(tessellated[i].X),
                            Y = RevitUnitConversion.ToMillimeters(tessellated[i].Y)
                        });
                    }
                }

                if (points.Count < 3)
                    continue;

                double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
                double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
                double extent = (maxX - minX) * (maxY - minY);
                if (extent > bestExtent)
                {
                    bestExtent = extent;
                    best = points;
                }
            }

            return best ?? new List<EgressPoint>();
        }

        private static EgressPoint ComputeCentroid(IReadOnlyList<EgressPoint> points)
        {
            return new EgressPoint
            {
                X = points.Average(point => point.X),
                Y = points.Average(point => point.Y)
            };
        }

        private static string GetDoorLevelName(FamilyInstance door)
        {
            var levelId = door.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
                return string.Empty;
            return (door.Document.GetElement(levelId) as Level)?.Name ?? string.Empty;
        }

        private static double? GetDoorWidthMm(FamilyInstance door)
        {
            var width = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()
                ?? door.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble();
            return width is > 0 ? RevitUnitConversion.ToMillimeters(width.Value) : null;
        }

        private static bool IsHostWallExterior(FamilyInstance door)
        {
            if (door.Host is not Wall wall)
                return false;

            var function = wall.WallType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM)?.AsInteger();
            return function == (int)WallFunction.Exterior;
        }

        public string GetName() => "Export Egress Graph";
    }
}
