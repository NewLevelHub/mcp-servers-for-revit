using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services;

/// <summary>
/// Derives grid centerline positions from walls — architectural practice:
/// grids sit on load-bearing wall centerlines; extents overshoot the building slightly.
/// </summary>
public static class GridAlignmentHelper
{
    public const double FeetToMm = 304.8;
    public const double DefaultClusterToleranceMm = 280.0;
    public const double DefaultExtentOvershootMm = 4000.0;
    public const double DefaultMinWallThicknessMm = 400.0;
    private const double AxisAlignRatio = 0.85;

    public sealed class WallAxisPlan
    {
        public List<double> XPositionsMm { get; set; } = new();
        public List<double> YPositionsMm { get; set; } = new();
        public double XExtentMinMm { get; set; }
        public double XExtentMaxMm { get; set; }
        public double YExtentMinMm { get; set; }
        public double YExtentMaxMm { get; set; }
        public int WallsConsidered { get; set; }
        public int WallsUsed { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Collect structural/bearing wall centerlines on a level and cluster into grid positions.
    /// </summary>
    public static WallAxisPlan ComputeFromWalls(
        Document doc,
        ElementId levelId,
        string wallFilter = "structural",
        double minWallThicknessMm = DefaultMinWallThicknessMm,
        double clusterToleranceMm = DefaultClusterToleranceMm,
        double extentOvershootMm = DefaultExtentOvershootMm)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));
        if (levelId == null || levelId == ElementId.InvalidElementId)
            throw new ArgumentException("A valid level is required to derive grids from walls.");

        var tolMm = clusterToleranceMm > 0 ? clusterToleranceMm : DefaultClusterToleranceMm;
        var overshoot = extentOvershootMm >= 0 ? extentOvershootMm : DefaultExtentOvershootMm;
        var minThickness = minWallThicknessMm > 0 ? minWallThicknessMm : DefaultMinWallThicknessMm;

        var walls = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .Where(w => w.get_BoundingBox(null) != null)
            .Where(w => WallIsOnLevel(w, levelId))
            .ToList();

        var plan = new WallAxisPlan { WallsConsidered = walls.Count };
        var xRaw = new List<(double posMm, double lengthMm)>();
        var yRaw = new List<(double posMm, double lengthMm)>();
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        var used = 0;

        foreach (var wall in walls)
        {
            if (!PassesWallFilter(wall, wallFilter, minThickness))
                continue;

            if (wall.Location is not LocationCurve loc || loc.Curve is not Line line || !line.IsBound)
                continue;

            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            var dx = Math.Abs(p1.X - p0.X);
            var dy = Math.Abs(p1.Y - p0.Y);
            var length = line.Length;
            if (length < 1e-6)
                continue;

            ExpandBounds(p0, ref minX, ref maxX, ref minY, ref maxY);
            ExpandBounds(p1, ref minX, ref maxX, ref minY, ref maxY);

            var mid = (p0 + p1) * 0.5;
            var lengthMm = length * FeetToMm;

            // Vertical wall (parallel to Y) → vertical grid at constant X
            if (dy >= AxisAlignRatio * (dx + dy) || dy >= dx)
            {
                xRaw.Add((mid.X * FeetToMm, lengthMm));
                used++;
            }
            // Horizontal wall (parallel to X) → horizontal grid at constant Y
            else if (dx >= AxisAlignRatio * (dx + dy) || dx > dy)
            {
                yRaw.Add((mid.Y * FeetToMm, lengthMm));
                used++;
            }
        }

        plan.WallsUsed = used;
        plan.XPositionsMm = ClusterPositions(xRaw, tolMm);
        plan.YPositionsMm = ClusterPositions(yRaw, tolMm);

        if (plan.XPositionsMm.Count == 0 && plan.YPositionsMm.Count == 0)
        {
            plan.Warnings.Add(
                "No wall centerlines matched the filter. Check level, wallFilter, or minWallThicknessMm.");
            return plan;
        }

        if (double.IsInfinity(minX) || double.IsInfinity(minY))
        {
            plan.Warnings.Add("Could not compute wall bounding box for extents.");
            return plan;
        }

        plan.XExtentMinMm = minX * FeetToMm - overshoot;
        plan.XExtentMaxMm = maxX * FeetToMm + overshoot;
        plan.YExtentMinMm = minY * FeetToMm - overshoot;
        plan.YExtentMaxMm = maxY * FeetToMm + overshoot;

        if (plan.XPositionsMm.Count == 0)
            plan.Warnings.Add("No vertical (X) grid positions found from walls.");
        if (plan.YPositionsMm.Count == 0)
            plan.Warnings.Add("No horizontal (Y) grid positions found from walls.");

        return plan;
    }

    /// <summary>
    /// Merge nearby positions (weighted by wall length). Exposed for unit tests.
    /// </summary>
    public static List<double> ClusterPositions(
        IEnumerable<(double posMm, double lengthMm)> samples,
        double toleranceMm)
    {
        var list = samples?
            .Where(s => !double.IsNaN(s.posMm) && !double.IsInfinity(s.posMm))
            .OrderBy(s => s.posMm)
            .ToList() ?? new List<(double, double)>();

        if (list.Count == 0)
            return new List<double>();

        var tol = toleranceMm > 0 ? toleranceMm : DefaultClusterToleranceMm;
        var clusters = new List<(double sumPosLen, double sumLen, double first)>();

        foreach (var (posMm, lengthMm) in list)
        {
            var weight = Math.Max(lengthMm, 1.0);
            if (clusters.Count == 0)
            {
                clusters.Add((posMm * weight, weight, posMm));
                continue;
            }

            var last = clusters[clusters.Count - 1];
            var centroid = last.sumPosLen / last.sumLen;
            if (Math.Abs(posMm - centroid) <= tol || Math.Abs(posMm - last.first) <= tol)
            {
                clusters[clusters.Count - 1] = (
                    last.sumPosLen + posMm * weight,
                    last.sumLen + weight,
                    last.first);
            }
            else
            {
                clusters.Add((posMm * weight, weight, posMm));
            }
        }

        return clusters
            .Select(c => Math.Round(c.sumPosLen / c.sumLen, 3))
            .ToList();
    }

    public static bool PassesWallFilter(Wall wall, string wallFilter, double minThicknessMm)
    {
        var filter = string.IsNullOrWhiteSpace(wallFilter)
            ? "structural"
            : wallFilter.Trim().ToLowerInvariant();

        var widthMm = wall.Width * FeetToMm;
        var typeName = wall.WallType?.Name ?? string.Empty;
        var function = wall.WallType?.Function;
        var isBearingUsage = IsBearingStructuralUsage(wall);
        var nameLooksStructural = TypeNameLooksStructural(typeName);

        return filter switch
        {
            // Thickness is authoritative — type name alone must not pull in t=200 partitions labeled «бетон».
            "all" => widthMm >= minThicknessMm,
            "exterior" => (function == WallFunction.Exterior ||
                           typeName.IndexOf("наруж", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           typeName.IndexOf("exterior", StringComparison.OrdinalIgnoreCase) >= 0)
                          && widthMm >= Math.Min(minThicknessMm, 200.0),
            // Working-doc axes: thick cores only (name must not bypass thickness).
            "structural" or "bearing" => widthMm >= minThicknessMm,
            _ => widthMm >= minThicknessMm
        };
    }

    private static bool IsBearingStructuralUsage(Wall wall)
    {
        var p = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM);
        if (p == null || !p.HasValue)
            return false;

        // 0 = Non-bearing; anything else (Bearing, Shear, Combined) counts
        return p.AsInteger() != 0;
    }

    private static bool TypeNameLooksStructural(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        string[] tokens =
        {
            "бетон", "concrete", "несущ", "structural", "жб", "монолит", "кирпич", "brick"
        };

        foreach (var token in tokens)
        {
            if (typeName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        // Skip explicit partitions / thin fillers
        string[] skip =
        {
            "перегород", "partition", "витраж", "curtain"
        };
        foreach (var token in skip)
        {
            if (typeName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }

        return false;
    }

    private static bool WallIsOnLevel(Wall wall, ElementId levelId)
    {
        var wallLevel = wall.LevelId;
        if (wallLevel != null && wallLevel != ElementId.InvalidElementId && wallLevel == levelId)
            return true;

        // Multi-storey walls: include if they intersect the level elevation
        var level = wall.Document.GetElement(levelId) as Level;
        if (level == null)
            return false;

        var bbox = wall.get_BoundingBox(null);
        if (bbox == null)
            return false;

        const double eps = 0.05; // ~15 mm
        return bbox.Min.Z - eps <= level.Elevation && bbox.Max.Z + eps >= level.Elevation;
    }

    private static void ExpandBounds(
        XYZ p,
        ref double minX,
        ref double maxX,
        ref double minY,
        ref double maxY)
    {
        if (p.X < minX) minX = p.X;
        if (p.X > maxX) maxX = p.X;
        if (p.Y < minY) minY = p.Y;
        if (p.Y > maxY) maxY = p.Y;
    }
}
