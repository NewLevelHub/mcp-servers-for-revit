using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class DoorEgressInfoEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _levelNameFilter = string.Empty;

        public GetDoorEgressInfoResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(string levelName = "")
        {
            _levelNameFilter = levelName ?? string.Empty;
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
                var doorInstances = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .ToList();
                var placedRooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(room => room.Area > 0)
                    .ToList();

                var result = new List<DoorEgressInfo>();
                foreach (var door in doorInstances)
                {
                    // Slopes / trims (откосы) live in OST_Doors but are not door
                    // blocks — exclude them so width checks do not count them (REV-41).
                    if (!OpeningFillClassifier.IsSchedulableDoor(door))
                        continue;

                    var levelName = doc.GetElement(door.LevelId)?.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(_levelNameFilter) &&
                        !string.Equals(levelName, _levelNameFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var widthMm = GetDoorWidthMm(door);
                    var (clearWidthMm, widthSource) = GetDoorClearWidthMm(door, widthMm);
                    var levelRooms = placedRooms
                        .Where(room => string.Equals(
                            room.Level?.Name,
                            levelName,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var (
                        maneuveringDepthMm,
                        maneuveringWidthMm,
                        maneuveringRoom,
                        maneuveringRequiredDepthMm,
                        maneuveringApproach) = MeasureManeuveringSpace(door, levelRooms);
                    var hostWallId = door.Host is Wall wall ? (long?)wall.Id.GetValue() : null;

                    result.Add(new DoorEgressInfo
                    {
                        Id = door.Id.GetValue(),
                        UniqueId = door.UniqueId,
                        Family = door.Symbol?.Family?.Name ?? string.Empty,
                        Type = door.Symbol?.Name ?? string.Empty,
                        Level = levelName,
                        HostWallId = hostWallId,
                        OpeningWidthMm = widthMm,
                        ClearWidthMm = clearWidthMm,
                        WidthSource = widthSource,
                        IsOnEgressPath = IsOnEgressPath(door),
                        ManeuveringDepthMm = maneuveringDepthMm,
                        ManeuveringWidthMm = maneuveringWidthMm,
                        ManeuveringRoom = maneuveringRoom,
                        ManeuveringRequiredDepthMm = maneuveringRequiredDepthMm,
                        ManeuveringApproach = maneuveringApproach,
                        Location = FormatLocation(door.Location)
                    });
                }

                var ramps = CollectRamps(doc);
                ResultInfo = new GetDoorEgressInfoResult
                {
                    Success = true,
                    Message = $"Collected accessibility info for {result.Count} doors and {ramps.Count} ramps.",
                    TotalDoors = result.Count,
                    Doors = result,
                    Ramps = ramps
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new GetDoorEgressInfoResult
                {
                    Success = false,
                    Message = $"Failed to collect door egress info: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Get Door Egress Info";

        private static double? GetDoorWidthMm(FamilyInstance door)
        {
            var widthParam = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                ?? door.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                ?? door.LookupParameter("Width")
                ?? door.LookupParameter("Ширина");

            if (widthParam == null || !widthParam.HasValue)
                return null;

            if (widthParam.StorageType != StorageType.Double)
                return null;

            return RevitUnitConversion.ToMillimeters(widthParam.AsDouble());
        }

        private static (double? widthMm, string source) GetDoorClearWidthMm(
            FamilyInstance door,
            double? nominalWidthMm)
        {
            string[] names =
            {
                "Ширина в свету",
                "Ширина прохода в свету",
                "ADSK_Размер_Ширина в свету",
                "Clear Width",
                "Clear Opening Width"
            };

            foreach (var name in names)
            {
                var parameter = door.LookupParameter(name) ?? door.Symbol?.LookupParameter(name);
                if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double)
                    continue;

                var widthMm = RevitUnitConversion.ToMillimeters(parameter.AsDouble());
                if (widthMm > 0)
                    return (widthMm, $"parameter:{name}");
            }

            var fuzzyParameter = FindLengthParameter(
                door,
                parameterName =>
                {
                    var text = parameterName.ToLowerInvariant();
                    return text.Contains("в свету")
                        || text.Contains("clear width")
                        || text.Contains("clear opening");
                });
            if (fuzzyParameter.HasValue && fuzzyParameter.Value > 0)
                return (fuzzyParameter.Value, "parameter:fuzzy-clear-width");

            string[] jambWidthNames =
            {
                "Толщина стойки коробки",
                "Ширина стойки коробки",
                "Jamb Width",
                "Frame Jamb Width"
            };
            foreach (var jambName in jambWidthNames)
            {
                var jambWidth = ReadLengthParameterMm(door, jambName);
                if (!jambWidth.HasValue || jambWidth.Value <= 0 || !nominalWidthMm.HasValue)
                    continue;
                var calculated = nominalWidthMm.Value - 2 * jambWidth.Value;
                if (calculated > 0)
                    return (calculated, $"calculated:nominal-minus-2x-{jambName}");
            }

            // Preserve a measurable fallback for legacy families, but expose its source so
            // the audit can warn that the value is nominal rather than claim false precision.
            return (nominalWidthMm, nominalWidthMm.HasValue ? "nominal_fallback" : "missing");
        }

        private static double? ReadLengthParameterMm(FamilyInstance door, string name)
        {
            var parameter = door.LookupParameter(name) ?? door.Symbol?.LookupParameter(name);
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double)
                return null;
            return RevitUnitConversion.ToMillimeters(parameter.AsDouble());
        }

        private static double? FindLengthParameter(
            FamilyInstance door,
            Func<string, bool> predicate)
        {
            foreach (var element in new Element[] { door, door.Symbol })
            {
                if (element == null)
                    continue;
                foreach (Parameter parameter in element.Parameters)
                {
                    var name = parameter.Definition?.Name ?? string.Empty;
                    if (!predicate(name) ||
                        !parameter.HasValue ||
                        parameter.StorageType != StorageType.Double)
                    {
                        continue;
                    }
                    return RevitUnitConversion.ToMillimeters(parameter.AsDouble());
                }
            }
            return null;
        }

        private static (
            double? depthMm,
            double? widthMm,
            string roomName,
            double? requiredDepthMm,
            string approach)
            MeasureManeuveringSpace(FamilyInstance door, IReadOnlyCollection<Room> candidateRooms)
        {
            if (door.Location is not LocationPoint location)
                return (null, null, string.Empty, null, string.Empty);

            var rooms = new[] { door.FromRoom, door.ToRoom }
                .Where(room => room != null && room.Area > 0)
                .Distinct()
                .ToList();
            foreach (var room in candidateRooms)
            {
                if (!rooms.Contains(room))
                    rooms.Add(room);
            }
            if (rooms.Count == 0)
                return (null, null, string.Empty, null, string.Empty);

            var facing = NormalizeHorizontal(door.FacingOrientation);
            var hand = NormalizeHorizontal(door.HandOrientation);
            if (facing == null || hand == null)
                return (null, null, string.Empty, null, string.Empty);

            // A fixed 100 mm probe stayed inside 200/500 mm host walls, so
            // IsPointInRoom rejected otherwise valid FromRoom/ToRoom values.
            // Start beyond the host face and try progressively deeper probes
            // for layered/irregular walls.
            var hostHalfThicknessFeet =
                door.Host is Wall hostWall ? hostWall.Width / 2.0 : 100.0 / 304.8;
            var probeClearancesFeet = new[]
            {
                25.0 / 304.8,
                100.0 / 304.8,
                300.0 / 304.8
            };
            const double sampleStepFeet = 25.0 / 304.8;
            const double maxDistanceFeet = 10000.0 / 304.8;
            var origin = new XYZ(location.Point.X, location.Point.Y, location.Point.Z + 1.0);

            var measurements = new List<(
                double depth,
                double width,
                string room,
                double requiredDepth,
                string approach)>();
            foreach (var room in rooms)
            {
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    var inward = facing.Multiply(sign);
                    XYZ seed = null;
                    double usedClearanceFeet = 0;
                    foreach (var clearanceFeet in probeClearancesFeet)
                    {
                        var candidate = origin.Add(
                            inward.Multiply(hostHalfThicknessFeet + clearanceFeet));
                        if (!room.IsPointInRoom(candidate))
                            continue;
                        seed = candidate;
                        usedClearanceFeet = clearanceFeet;
                        break;
                    }
                    if (seed == null)
                        continue;

                    // Measure from the wall face, not the wall centreline.
                    var depth = usedClearanceFeet +
                        MeasureInsideRoom(room, seed, inward, sampleStepFeet, maxDistanceFeet);
                    var left = MeasureInsideRoom(room, seed, hand, sampleStepFeet, maxDistanceFeet);
                    var right = MeasureInsideRoom(
                        room,
                        seed,
                        hand.Multiply(-1),
                        sampleStepFeet,
                        maxDistanceFeet);
                    measurements.Add((
                        RevitUnitConversion.ToMillimeters(depth),
                        RevitUnitConversion.ToMillimeters(left + right),
                        room.Name ?? string.Empty,
                        sign > 0 ? 1500.0 : 1200.0,
                        sign > 0 ? "pull/family-facing" : "push/opposite-facing"));
                    break;
                }
            }

            if (measurements.Count == 0)
                return (null, null, string.Empty, null, string.Empty);

            // Both sides of a door must remain usable. Report the limiting adjacent side.
            var limiting = measurements
                .OrderBy(item => Math.Min(
                    item.depth / item.requiredDepth,
                    item.width / 1500.0))
                .First();
            return (
                limiting.depth,
                limiting.width,
                limiting.room,
                limiting.requiredDepth,
                limiting.approach);
        }

        private static XYZ NormalizeHorizontal(XYZ vector)
        {
            if (vector == null)
                return null;
            var horizontal = new XYZ(vector.X, vector.Y, 0);
            return horizontal.GetLength() > 1e-9 ? horizontal.Normalize() : null;
        }

        private static double MeasureInsideRoom(
            Room room,
            XYZ start,
            XYZ direction,
            double stepFeet,
            double maxDistanceFeet)
        {
            double distance = 0;
            while (distance + stepFeet <= maxDistanceFeet)
            {
                var candidate = start.Add(direction.Multiply(distance + stepFeet));
                if (!room.IsPointInRoom(candidate))
                    break;
                distance += stepFeet;
            }
            return distance;
        }

        private List<RampAccessibilityInfo> CollectRamps(Document doc)
        {
            var result = new List<RampAccessibilityInfo>();
            var ramps = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Ramps)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var ramp in ramps)
            {
                var levelName = GetElementLevelName(doc, ramp);
                if (!string.IsNullOrWhiteSpace(_levelNameFilter) &&
                    !string.Equals(levelName, _levelNameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (slopePercent, slopeSource, riseMm, runMm) = GetRampSlope(ramp);
                result.Add(new RampAccessibilityInfo
                {
                    Id = ramp.Id.GetValue(),
                    UniqueId = ramp.UniqueId,
                    Name = ramp.Name ?? $"Ramp {ramp.Id.GetValue()}",
                    Level = levelName,
                    SlopePercent = slopePercent,
                    SlopeSource = slopeSource,
                    RiseMm = riseMm,
                    RunMm = runMm,
                    IsExceptionAllowed = IsRampExceptionAllowed(ramp)
                });
            }
            return result;
        }

        private static string GetElementLevelName(Document doc, Element element)
        {
            string[] names = { "Base Level", "Базовый уровень", "Level", "Уровень" };
            foreach (var name in names)
            {
                var parameter = element.LookupParameter(name);
                if (parameter?.StorageType != StorageType.ElementId)
                    continue;
                var level = doc.GetElement(parameter.AsElementId()) as Level;
                if (level != null)
                    return level.Name;
            }
            return string.Empty;
        }

        private static (double? slopePercent, string source, double? riseMm, double? runMm)
            GetRampSlope(Element ramp)
        {
            var box = ramp.get_BoundingBox(null);
            double? riseMm = null;
            double? runMm = null;
            if (box != null)
            {
                var rise = Math.Abs(box.Max.Z - box.Min.Z);
                var run = Math.Max(
                    Math.Abs(box.Max.X - box.Min.X),
                    Math.Abs(box.Max.Y - box.Min.Y));
                if (rise > 1e-9)
                    riseMm = RevitUnitConversion.ToMillimeters(rise);
                if (run > 1e-9)
                    runMm = RevitUnitConversion.ToMillimeters(run);
            }

            var geometrySlope = GetMaxSlopePercent(ramp.get_Geometry(new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            }));
            if (geometrySlope.HasValue)
                return (geometrySlope.Value, "geometry_face", riseMm, runMm);

            string[] slopeNames =
            {
                "Actual Slope",
                "Фактический уклон",
                "Slope",
                "Уклон"
            };
            foreach (var name in slopeNames)
            {
                var parameter = ramp.LookupParameter(name)
                    ?? ramp.Document.GetElement(ramp.GetTypeId())?.LookupParameter(name);
                if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double)
                    continue;

                var raw = parameter.AsDouble();
                if (raw <= 0)
                    continue;
                // Revit slope parameters are commonly stored as rise/run; inverse-slope
                // family parameters are commonly displayed as a denominator (e.g. 12).
                var percent = raw > 1 ? 100.0 / raw : raw * 100.0;
                return (percent, $"parameter:{name}", riseMm, runMm);
            }

            if (!riseMm.HasValue || !runMm.HasValue)
                return (null, "missing", null, null);

            return (
                riseMm.Value / runMm.Value * 100.0,
                "geometry_bbox",
                riseMm,
                runMm);
        }

        private static bool IsRampExceptionAllowed(Element ramp)
        {
            var comments = ramp.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString() ?? string.Empty;
            var typeComments = ramp.Document.GetElement(ramp.GetTypeId())
                ?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)
                ?.AsString() ?? string.Empty;
            var text = $"{comments} {typeComments}".ToLowerInvariant();
            return text.Contains("стеснен")
                || text.Contains("исключ")
                || text.Contains("exception");
        }

        private static double? GetMaxSlopePercent(GeometryElement geometry)
        {
            if (geometry == null)
                return null;

            double maxSlope = 0;
            foreach (var geometryObject in geometry)
            {
                if (geometryObject is GeometryInstance instance)
                {
                    var nested = GetMaxSlopePercent(instance.GetInstanceGeometry());
                    if (nested.HasValue)
                        maxSlope = Math.Max(maxSlope, nested.Value);
                    continue;
                }

                if (geometryObject is not Solid solid || solid.Faces.Size == 0)
                    continue;

                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace planar)
                        continue;
                    var normal = planar.FaceNormal;
                    var vertical = Math.Abs(normal.Z);
                    if (vertical < 1e-6 || vertical > 0.999999)
                        continue;
                    var horizontal = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y);
                    var slope = horizontal / vertical * 100.0;
                    // Exclude near-vertical side/chamfer faces; accessible ramps are <= 8%.
                    if (slope > 0.01 && slope < 25)
                        maxSlope = Math.Max(maxSlope, slope);
                }
            }
            return maxSlope > 0 ? maxSlope : null;
        }

        private static bool IsOnEgressPath(FamilyInstance door)
        {
            var fromName = door.FromRoom?.Name?.ToLowerInvariant() ?? string.Empty;
            var toName = door.ToRoom?.Name?.ToLowerInvariant() ?? string.Empty;
            var comments = door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString()?.ToLowerInvariant() ?? string.Empty;

            return ContainsEgressKeyword(fromName)
                || ContainsEgressKeyword(toName)
                || comments.Contains("эвак")
                || comments.Contains("egress");
        }

        private static bool ContainsEgressKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Contains("коридор")
                || value.Contains("лест")
                || value.Contains("эвак")
                || value.Contains("corridor")
                || value.Contains("stair")
                || value.Contains("egress")
                || value.Contains("hall");
        }

        private static string FormatLocation(Location location)
        {
            if (location is not LocationPoint point)
                return string.Empty;

            var mmX = RevitUnitConversion.ToMillimeters(point.Point.X);
            var mmY = RevitUnitConversion.ToMillimeters(point.Point.Y);
            var mmZ = RevitUnitConversion.ToMillimeters(point.Point.Z);
            return $"{mmX:F0}, {mmY:F0}, {mmZ:F0}";
        }
    }
}
