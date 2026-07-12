using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Solid wall segments (fire piers) between glazed openings on balcony/loggia facade walls.
    /// </summary>
    public static class FirePierCalculator
    {
        public enum PierKind
        {
            EndPier,
            BetweenOpenings
        }

        public sealed class PierSegment
        {
            public PierKind Kind { get; set; }
            public long WallId { get; set; }
            public double LengthMm { get; set; }
            public double StartAlongWallMm { get; set; }
            public double EndAlongWallMm { get; set; }
            public List<long> AdjacentOpeningIds { get; set; } = new List<long>();
        }

        private sealed class OpeningSpan
        {
            public long Id { get; set; }
            public double StartMm { get; set; }
            public double EndMm { get; set; }
        }

        public static List<PierSegment> CalculateForRoom(Document doc, Room room)
        {
            var result = new List<PierSegment>();
            if (room == null || doc == null)
                return result;

            var boundaryWalls = GetBoundaryWallIds(room);
            if (boundaryWalls.Count == 0)
                return result;

            foreach (var wallId in boundaryWalls)
            {
                if (doc.GetElement(wallId) is not Wall wall)
                    continue;

                var wallLengthMm = GetWallLengthMm(wall);
                if (wallLengthMm <= 0)
                    continue;

                var openings = CollectOpeningSpans(doc, wall, room, wallLengthMm);
                if (openings.Count == 0)
                    continue;

                openings = MergeOverlapping(openings);
                result.AddRange(BuildPierSegments(wallId, wallLengthMm, openings));
            }

            return result;
        }

        private static HashSet<ElementId> GetBoundaryWallIds(Room room)
        {
            var walls = new HashSet<ElementId>();
            var options = new SpatialElementBoundaryOptions();
            var loops = room.GetBoundarySegments(options);
            if (loops == null)
                return walls;

            foreach (var loop in loops)
            {
                foreach (var segment in loop)
                {
                    var element = room.Document.GetElement(segment.ElementId);
                    if (element is Wall wall)
                    {
                        walls.Add(wall.Id);
                    }
                }
            }

            return walls;
        }

        private static double GetWallLengthMm(Wall wall)
        {
            if (wall.Location is not LocationCurve locationCurve)
                return 0;

            return RevitUnitConversion.ToMillimeters(locationCurve.Curve.Length);
        }

        private static List<OpeningSpan> CollectOpeningSpans(
            Document doc,
            Wall wall,
            Room room,
            double wallLengthMm)
        {
            var spans = new List<OpeningSpan>();
            var wallId = wall.Id;

            var instances = new FilteredElementCollector(doc)
                .WherePasses(new ElementCategoryFilter(BuiltInCategory.OST_Doors))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Concat(
                    new FilteredElementCollector(doc)
                        .WherePasses(new ElementCategoryFilter(BuiltInCategory.OST_Windows))
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>()
                )
                .Where(instance => instance.Host?.Id == wallId)
                .Where(instance => BordersRoom(instance, room));

            foreach (var instance in instances)
            {
                var span = TryGetOpeningSpan(instance, wall, wallLengthMm);
                if (span != null)
                    spans.Add(span);
            }

            return spans;
        }

        private static bool BordersRoom(FamilyInstance instance, Room room)
        {
            var from = instance.FromRoom;
            var to = instance.ToRoom;
            if (from != null && from.Id == room.Id)
                return true;
            if (to != null && to.Id == room.Id)
                return true;

            var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            var fromName = from?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            var toName = to?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;

            return string.Equals(fromName, roomName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(toName, roomName, StringComparison.OrdinalIgnoreCase);
        }

        private static OpeningSpan TryGetOpeningSpan(
            FamilyInstance instance,
            Wall wall,
            double wallLengthMm)
        {
            if (wall.Location is not LocationCurve locationCurve)
                return null;

            var curve = locationCurve.Curve;
            var widthInternal = GetOpeningWidthInternal(instance);
            if (widthInternal <= 0)
                return null;

            XYZ point;
            if (instance.Location is LocationPoint locationPoint)
            {
                point = locationPoint.Point;
            }
            else
            {
                return null;
            }

            var projection = curve.Project(point);
            if (projection == null)
                return null;

            var centerMm = RevitUnitConversion.ToMillimeters(projection.Parameter * curve.Length);
            var halfWidthMm = RevitUnitConversion.ToMillimeters(widthInternal / 2.0);
            var startMm = Math.Max(0, centerMm - halfWidthMm);
            var endMm = Math.Min(wallLengthMm, centerMm + halfWidthMm);

            if (endMm <= startMm)
                return null;

            return new OpeningSpan
            {
                Id = instance.Id.GetValue(),
                StartMm = startMm,
                EndMm = endMm
            };
        }

        private static double GetOpeningWidthInternal(FamilyInstance instance)
        {
            var widthParam = instance.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                ?? instance.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)
                ?? instance.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                ?? instance.Symbol?.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)
                ?? instance.LookupParameter("Width")
                ?? instance.LookupParameter("Ширина");

            if (widthParam == null || !widthParam.HasValue || widthParam.StorageType != StorageType.Double)
                return 0;

            return widthParam.AsDouble();
        }

        private static List<OpeningSpan> MergeOverlapping(List<OpeningSpan> spans)
        {
            if (spans.Count <= 1)
                return spans;

            var sorted = spans.OrderBy(span => span.StartMm).ToList();
            var merged = new List<OpeningSpan> { sorted[0] };

            for (int i = 1; i < sorted.Count; i++)
            {
                var last = merged[merged.Count - 1];
                var current = sorted[i];
                if (current.StartMm <= last.EndMm + 1)
                {
                    merged[merged.Count - 1] = new OpeningSpan
                    {
                        Id = last.Id,
                        StartMm = last.StartMm,
                        EndMm = Math.Max(last.EndMm, current.EndMm)
                    };
                }
                else
                {
                    merged.Add(current);
                }
            }

            return merged;
        }

        private static IEnumerable<PierSegment> BuildPierSegments(
            ElementId wallId,
            double wallLengthMm,
            List<OpeningSpan> openings)
        {
            var segments = new List<PierSegment>();
            var sorted = openings.OrderBy(o => o.StartMm).ToList();

            if (sorted[0].StartMm > 0)
            {
                segments.Add(new PierSegment
                {
                    Kind = PierKind.EndPier,
                    WallId = wallId.GetValue(),
                    StartAlongWallMm = 0,
                    EndAlongWallMm = sorted[0].StartMm,
                    LengthMm = sorted[0].StartMm,
                    AdjacentOpeningIds = new List<long> { sorted[0].Id }
                });
            }

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var gapStart = sorted[i].EndMm;
                var gapEnd = sorted[i + 1].StartMm;
                var gap = gapEnd - gapStart;
                if (gap <= 0)
                    continue;

                segments.Add(new PierSegment
                {
                    Kind = PierKind.BetweenOpenings,
                    WallId = wallId.GetValue(),
                    StartAlongWallMm = gapStart,
                    EndAlongWallMm = gapEnd,
                    LengthMm = gap,
                    AdjacentOpeningIds = new List<long> { sorted[i].Id, sorted[i + 1].Id }
                });
            }

            var last = sorted[sorted.Count - 1];
            if (last.EndMm < wallLengthMm)
            {
                segments.Add(new PierSegment
                {
                    Kind = PierKind.EndPier,
                    WallId = wallId.GetValue(),
                    StartAlongWallMm = last.EndMm,
                    EndAlongWallMm = wallLengthMm,
                    LengthMm = wallLengthMm - last.EndMm,
                    AdjacentOpeningIds = new List<long> { last.Id }
                });
            }

            return segments;
        }
    }
}
