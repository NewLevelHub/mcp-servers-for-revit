using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPCommandSet.Utils
{
    public static class RoomFootprintCalculator
    {
        /// <summary>
        /// Bounding-box footprint of a room in millimeters.
        /// Width is the smaller span, depth is the larger one — the same
        /// convention as get_room_geometry_metrics (REV-31).
        /// </summary>
        public static (double widthMm, double depthMm) Calculate(Room room)
        {
            var options = new SpatialElementBoundaryOptions();
            var boundaries = room.GetBoundarySegments(options);

            if (boundaries == null || boundaries.Count == 0)
                return (0, 0);

            var points = new List<XYZ>();
            foreach (var loop in boundaries)
            {
                foreach (var segment in loop)
                {
                    var curve = segment?.GetCurve();
                    if (curve == null)
                        continue;

                    points.Add(curve.GetEndPoint(0));
                    points.Add(curve.GetEndPoint(1));
                }
            }

            if (points.Count == 0)
                return (0, 0);

            double minX = points.Min(p => p.X);
            double maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y);
            double maxY = points.Max(p => p.Y);

            double xSpanMm = RevitUnitConversion.ToMillimeters(maxX - minX);
            double ySpanMm = RevitUnitConversion.ToMillimeters(maxY - minY);

            return xSpanMm <= ySpanMm ? (xSpanMm, ySpanMm) : (ySpanMm, xSpanMm);
        }
    }
}
