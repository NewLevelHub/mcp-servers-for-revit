using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services.AnnotationComponents;

/// <summary>
///     Collects ordered wall-end and opening-jamb references along an exterior facade
///     for the innermost openings/piers dimension tier (REV-141).
/// </summary>
public static class OpeningFacadeDimensionCollector
{
    private const double FeetToMm = 304.8;
    private const double DefaultDedupToleranceMm = 50;
    private const double MinOpeningOffsetMm = 300;

    public enum FacadeSide
    {
        /// <summary>Bottom (south): measure along X.</summary>
        Bottom,

        /// <summary>Top (north): measure along X.</summary>
        Top,

        /// <summary>Left (west): measure along Y.</summary>
        Left,

        /// <summary>Right (east): measure along Y.</summary>
        Right
    }

    /// <summary>
    ///     Opening-tier offset from envelope: max(300, firstOffsetMm - tierGapMm).
    /// </summary>
    public static double ComputeOpeningOffsetMm(double firstOffsetMm, double tierGapMm)
    {
        var first = firstOffsetMm > 0 ? firstOffsetMm : 1200;
        var gap = tierGapMm > 0 ? tierGapMm : 800;
        return Math.Max(MinOpeningOffsetMm, first - gap);
    }

    /// <summary>
    ///     Sort by position and drop near-duplicates (same cut within tolerance).
    ///     Exposed for unit tests.
    /// </summary>
    public static List<(T Item, double PositionMm)> DedupAndSort<T>(
        IEnumerable<(T Item, double PositionMm)> points,
        double toleranceMm = DefaultDedupToleranceMm)
    {
        var sorted = points
            .OrderBy(p => p.PositionMm)
            .ToList();

        if (sorted.Count == 0)
            return sorted;

        var tol = Math.Max(1, toleranceMm);
        var result = new List<(T Item, double PositionMm)> { sorted[0] };
        for (var i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].PositionMm - result[result.Count - 1].PositionMm) < tol)
                continue;
            result.Add(sorted[i]);
        }

        return result;
    }

    /// <summary>
    ///     Ordered references for a continuous openings/piers chain on one facade side.
    ///     Extreme cuts use <b>exterior</b> faces of return walls (outer envelope), not the
    ///     joined end of the facade wall (often lands on the interior face of the corner).
    /// </summary>
    public static List<(Reference Reference, double PositionMm)> CollectOrderedReferences(
        Document doc,
        ViewPlan view,
        ElementId levelId,
        DimensionGridsEventHandler.EnvelopeMm envelope,
        FacadeSide side,
        double faceToleranceMm = 800)
    {
        if (doc == null || view == null || envelope == null)
            return new List<(Reference, double)>();

        var measureAlongX = side is FacadeSide.Bottom or FacadeSide.Top;
        var measureDirection = measureAlongX ? XYZ.BasisX : XYZ.BasisY;
        var walls = CollectFacadeWalls(doc, view, levelId, envelope, side, faceToleranceMm);

        var raw = new List<(Reference Reference, double PositionMm)>();
        var openingCount = 0;
        var jambCount = 0;

        // Corners: exterior faces of return walls (наружные грани).
        var cornerCount = AddExteriorCornerReferences(
            doc, view, levelId, envelope, side, measureDirection, faceToleranceMm, raw);

        foreach (var wall in walls)
        {
            // Fallback ends only when corners missing (open facade / no return wall).
            if (cornerCount < 2)
            {
                foreach (var end in DimensionAnnotationHelper.GetWallEndReferences(
                             wall, view, measureDirection))
                    raw.Add(end);
            }

            foreach (var opening in CollectOpeningsOnWall(doc, wall))
            {
                openingCount++;
                var jambs = DimensionAnnotationHelper.GetOpeningJambReferences(
                    opening, view, measureDirection);
                jambCount += jambs.Count;
                foreach (var jamb in jambs)
                    raw.Add(jamb);
            }
        }

        var ordered = DedupAndSortPreferExterior(raw, side, DefaultDedupToleranceMm);
        LastCollectionDiagnostics =
            $"facade={side}, walls={walls.Count}, corners={cornerCount}, openings={openingCount}, " +
            $"jambRefs={jambCount}, cuts={ordered.Count}";
        return ordered;
    }

    /// <summary>
    ///     When two cuts fall within tolerance, keep the more exterior one
    ///     (outer envelope for exterior opening tier — never the inner face).
    /// </summary>
    public static List<(T Item, double PositionMm)> DedupAndSortPreferExterior<T>(
        IEnumerable<(T Item, double PositionMm)> points,
        FacadeSide side,
        double toleranceMm = DefaultDedupToleranceMm)
    {
        var sorted = points.OrderBy(p => p.PositionMm).ToList();
        if (sorted.Count == 0)
            return sorted;

        var tol = Math.Max(1, toleranceMm);
        // Toward min side (bottom/left): smaller PositionMm is more exterior at the start;
        // at the end (larger positions), larger PositionMm is more exterior.
        // PreferExterior within a cluster: for overall chain, take min at first cluster and
        // max when merging near-duplicates — pick extreme by comparing to cluster mean vs envelope.
        var result = new List<(T Item, double PositionMm)>();
        var i = 0;
        while (i < sorted.Count)
        {
            var cluster = new List<(T Item, double PositionMm)> { sorted[i] };
            var j = i + 1;
            while (j < sorted.Count
                   && Math.Abs(sorted[j].PositionMm - cluster[0].PositionMm) < tol)
            {
                cluster.Add(sorted[j]);
                j++;
            }

            // Within a near-duplicate cluster: keep the extreme that faces outside.
            // Early in the sorted list (left/bottom end) → min; late (right/top end) → max.
            // Heuristic: if cluster center is closer to the start of the range, prefer min.
            var preferMin = PreferMinInCluster(cluster, sorted);
            var chosen = preferMin
                ? cluster.OrderBy(c => c.PositionMm).First()
                : cluster.OrderByDescending(c => c.PositionMm).First();
            result.Add(chosen);
            i = j;
        }

        return result;
    }

    private static bool PreferMinInCluster<T>(
        List<(T Item, double PositionMm)> cluster,
        List<(T Item, double PositionMm)> allSorted)
    {
        if (allSorted.Count < 2)
            return true;

        var clusterPos = cluster.Average(c => c.PositionMm);
        var mid = (allSorted[0].PositionMm + allSorted[allSorted.Count - 1].PositionMm) / 2.0;
        return clusterPos <= mid;
    }

    private static int AddExteriorCornerReferences(
        Document doc,
        View view,
        ElementId levelId,
        DimensionGridsEventHandler.EnvelopeMm envelope,
        FacadeSide side,
        XYZ measureDirection,
        double faceToleranceMm,
        List<(Reference Reference, double PositionMm)> sink)
    {
        var tol = Math.Max(100, faceToleranceMm);
        var added = 0;

        // Return walls are perpendicular to the facade (form the building corners).
        var returnWalls = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .Where(w => WallIsOnLevel(w, levelId))
            .Where(w => !WallRunsAlongFacade(w, side))
            .Where(w => WallTouchesFacade(w, view, envelope, side, tol))
            .ToList();

        switch (side)
        {
            case FacadeSide.Bottom:
            case FacadeSide.Top:
                added += TryAddBestExteriorCorner(
                    returnWalls, view, envelope, measureDirection,
                    outward: XYZ.BasisX.Negate(),
                    nearEnvelopeMin: true,
                    alongX: true,
                    tol,
                    sink);
                added += TryAddBestExteriorCorner(
                    returnWalls, view, envelope, measureDirection,
                    outward: XYZ.BasisX,
                    nearEnvelopeMin: false,
                    alongX: true,
                    tol,
                    sink);
                break;

            case FacadeSide.Left:
            case FacadeSide.Right:
                added += TryAddBestExteriorCorner(
                    returnWalls, view, envelope, measureDirection,
                    outward: XYZ.BasisY.Negate(),
                    nearEnvelopeMin: true,
                    alongX: false,
                    tol,
                    sink);
                added += TryAddBestExteriorCorner(
                    returnWalls, view, envelope, measureDirection,
                    outward: XYZ.BasisY,
                    nearEnvelopeMin: false,
                    alongX: false,
                    tol,
                    sink);
                break;
        }

        return added;
    }

    private static int TryAddBestExteriorCorner(
        List<Wall> returnWalls,
        View view,
        DimensionGridsEventHandler.EnvelopeMm envelope,
        XYZ measureDirection,
        XYZ outward,
        bool nearEnvelopeMin,
        bool alongX,
        double tolMm,
        List<(Reference Reference, double PositionMm)> sink)
    {
        (Reference Reference, double PositionMm)? best = null;

        foreach (var wall in returnWalls)
        {
            var bbox = wall.get_BoundingBox(view) ?? wall.get_BoundingBox(null);
            if (bbox == null)
                continue;

            double edgeMm;
            double envEdge;
            if (alongX)
            {
                edgeMm = nearEnvelopeMin ? bbox.Min.X * FeetToMm : bbox.Max.X * FeetToMm;
                envEdge = nearEnvelopeMin ? envelope.MinXMm : envelope.MaxXMm;
            }
            else
            {
                edgeMm = nearEnvelopeMin ? bbox.Min.Y * FeetToMm : bbox.Max.Y * FeetToMm;
                envEdge = nearEnvelopeMin ? envelope.MinYMm : envelope.MaxYMm;
            }

            if (Math.Abs(edgeMm - envEdge) > tolMm)
                continue;

            var face = DimensionAnnotationHelper.GetExteriorShellFaceReference(
                wall, outward, measureDirection);
            if (face == null)
                continue;

            if (best == null
                || (nearEnvelopeMin && face.Value.PositionMm < best.Value.PositionMm)
                || (!nearEnvelopeMin && face.Value.PositionMm > best.Value.PositionMm))
            {
                best = face;
            }
        }

        if (best == null)
            return 0;

        sink.Add(best.Value);
        return 1;
    }

    /// <summary>Diagnostics from the last CollectOrderedReferences call (for warnings).</summary>
    public static string LastCollectionDiagnostics { get; private set; } = string.Empty;

    private static List<Wall> CollectFacadeWalls(
        Document doc,
        View view,
        ElementId levelId,
        DimensionGridsEventHandler.EnvelopeMm envelope,
        FacadeSide side,
        double faceToleranceMm)
    {
        var tol = Math.Max(100, faceToleranceMm);
        var walls = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .Where(w => WallIsOnLevel(w, levelId))
            .Where(w => WallTouchesFacade(w, view, envelope, side, tol))
            .Where(w => WallRunsAlongFacade(w, side))
            .ToList();

        return walls;
    }

    private static bool WallTouchesFacade(
        Wall wall,
        View view,
        DimensionGridsEventHandler.EnvelopeMm envelope,
        FacadeSide side,
        double tolMm)
    {
        var bbox = wall.get_BoundingBox(view) ?? wall.get_BoundingBox(null);
        if (bbox == null)
            return false;

        var minX = bbox.Min.X * FeetToMm;
        var maxX = bbox.Max.X * FeetToMm;
        var minY = bbox.Min.Y * FeetToMm;
        var maxY = bbox.Max.Y * FeetToMm;

        return side switch
        {
            FacadeSide.Bottom => minY <= envelope.MinYMm + tolMm && maxY >= envelope.MinYMm - tolMm,
            FacadeSide.Top => maxY >= envelope.MaxYMm - tolMm && minY <= envelope.MaxYMm + tolMm,
            FacadeSide.Left => minX <= envelope.MinXMm + tolMm && maxX >= envelope.MinXMm - tolMm,
            FacadeSide.Right => maxX >= envelope.MaxXMm - tolMm && minX <= envelope.MaxXMm + tolMm,
            _ => false
        };
    }

    private static bool WallRunsAlongFacade(Wall wall, FacadeSide side)
    {
        if (wall.Location is not LocationCurve locationCurve)
            return false;

        var curve = locationCurve.Curve;
        var p0 = curve.GetEndPoint(0);
        var p1 = curve.GetEndPoint(1);
        var dx = Math.Abs(p1.X - p0.X);
        var dy = Math.Abs(p1.Y - p0.Y);

        // Bottom/top facade → walls running along X; left/right → along Y.
        return side is FacadeSide.Bottom or FacadeSide.Top
            ? dx >= dy
            : dy >= dx;
    }

    private static IEnumerable<FamilyInstance> CollectOpeningsOnWall(Document doc, Wall wall)
    {
        var wallId = wall.Id;
        return new FilteredElementCollector(doc)
            .WherePasses(new ElementCategoryFilter(BuiltInCategory.OST_Doors))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .Concat(
                new FilteredElementCollector(doc)
                    .WherePasses(new ElementCategoryFilter(BuiltInCategory.OST_Windows))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>())
            .Where(instance => instance.Host?.Id == wallId);
    }

    private static bool WallIsOnLevel(Wall wall, ElementId levelId)
    {
        if (levelId == null || levelId == ElementId.InvalidElementId)
            return true;

        var wallLevel = wall.LevelId;
        if (wallLevel != null && wallLevel != ElementId.InvalidElementId && wallLevel == levelId)
            return true;

        var level = wall.Document.GetElement(levelId) as Level;
        if (level == null)
            return false;

        var bbox = wall.get_BoundingBox(null);
        if (bbox == null)
            return false;

        const double eps = 0.05;
        return bbox.Min.Z - eps <= level.Elevation && bbox.Max.Z + eps >= level.Elevation;
    }
}
