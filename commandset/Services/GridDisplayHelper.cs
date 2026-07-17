using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Architecture;

namespace RevitMCPCommandSet.Services;

public static class GridDisplayHelper
{
    private const double MillimetersToFeet = 1.0 / 304.8;
    private const double AxisEpsilon = 1e-6;

    private sealed class ExtentBounds
    {
        public double MinXFeet { get; set; }
        public double MaxXFeet { get; set; }
        public double MinYFeet { get; set; }
        public double MaxYFeet { get; set; }
    }

    public static GridDisplayConfigurationResult ConfigureGrids(
        Document doc,
        IEnumerable<Grid> grids,
        GridDisplayConfigurationInfo options,
        ViewPlan activeFloorPlan = null)
    {
        var warnings = new List<string>();
        var gridList = grids.Where(grid => grid != null).ToList();
        if (gridList.Count == 0)
            throw new InvalidOperationException("No grids were found to configure.");

        var gridType = ResolveGridType(doc, options.GridTypeName, options.GridTypeId);
        if (gridType == null)
            warnings.Add("No GridType was resolved; bubble style was not changed.");

        var views = ResolveViews(doc, options, activeFloorPlan, warnings);

        var bounds = ComputeExtentBounds(gridList, options);
        var gridViewUpdates = 0;

        foreach (var grid in gridList)
        {
            if (gridType != null && grid.GetTypeId() != gridType.Id)
                grid.ChangeTypeId(gridType.Id);

            try
            {
                EnsureGridSpansAllLevels(doc, grid);
            }
            catch (Exception ex)
            {
                warnings.Add($"Grid '{grid.Name}' (3D extents): {ex.Message}");
            }

            foreach (var view in views)
            {
                if (!grid.CanBeVisibleInView(view))
                    continue;

                var viewUpdated = false;

                try
                {
                    if (ApplyGridExtentInView(grid, view, bounds))
                        viewUpdated = true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Grid '{grid.Name}' on view '{view.Name}' (extent): {ex.Message}");
                }

                try
                {
                    ApplyBubbles(grid, view, options.ShowBubbles, options.BubbleEnd);
                    viewUpdated = true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Grid '{grid.Name}' on view '{view.Name}' (bubbles): {ex.Message}");
                }

                if (viewUpdated)
                    gridViewUpdates++;
            }
        }

        return new GridDisplayConfigurationResult
        {
            GridsProcessed = gridList.Count,
            ViewsProcessed = views.Count,
            GridViewUpdates = gridViewUpdates,
            GridTypeName = gridType?.Name ?? string.Empty,
            Warnings = warnings
        };
    }

    private static List<ViewPlan> ResolveViews(
        Document doc,
        GridDisplayConfigurationInfo options,
        ViewPlan activeFloorPlan,
        List<string> warnings)
    {
        if (options.ApplyToAllFloorPlans)
        {
            var all = GetFloorPlans(doc);
            if (all.Count == 0)
                warnings.Add("No floor plan views were found.");
            return all.ToList();
        }

        // Even when not applying to all plans, always configure the active floor plan
        // so bubbles (bottomLeft) and extents take effect on the view the user is looking at.
        if (activeFloorPlan != null &&
            !activeFloorPlan.IsTemplate &&
            activeFloorPlan.ViewType == ViewType.FloorPlan)
        {
            return new List<ViewPlan> { activeFloorPlan };
        }

        var fallback = doc.ActiveView as ViewPlan;
        if (fallback != null &&
            !fallback.IsTemplate &&
            fallback.ViewType == ViewType.FloorPlan)
        {
            return new List<ViewPlan> { fallback };
        }

        warnings.Add("No active floor plan to configure grid display on.");
        return new List<ViewPlan>();
    }

    public static GridDisplayConfigurationInfo FromCreationInfo(GridCreationInfo creationInfo)
    {
        return new GridDisplayConfigurationInfo
        {
            GridTypeName = creationInfo.GridTypeName ?? string.Empty,
            GridTypeId = creationInfo.GridTypeId,
            XExtentMin = creationInfo.XExtentMin,
            XExtentMax = creationInfo.XExtentMax,
            YExtentMin = creationInfo.YExtentMin,
            YExtentMax = creationInfo.YExtentMax,
            ShowBubbles = creationInfo.ShowBubbles,
            BubbleEnd = creationInfo.BubbleEnd,
            ApplyToAllFloorPlans = creationInfo.ConfigureDisplayOnAllPlans
        };
    }

    public static IList<Grid> GetGrids(Document doc, IEnumerable<long> gridIds)
    {
        if (gridIds != null && gridIds.Any())
        {
            return gridIds
                .Select(id =>
                {
#if REVIT2024_OR_GREATER
                    return doc.GetElement(new ElementId(id)) as Grid;
#else
                    return doc.GetElement(new ElementId((int)id)) as Grid;
#endif
                })
                .Where(grid => grid != null)
                .ToList();
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(Grid))
            .Cast<Grid>()
            .ToList();
    }

    public static GridType ResolveGridType(Document doc, string gridTypeName, int gridTypeId)
    {
        if (gridTypeId > 0)
        {
            var byId = doc.GetElement(new ElementId(gridTypeId)) as GridType;
            if (byId != null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(gridTypeName))
        {
            var trimmed = gridTypeName.Trim();
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(GridType))
                .Cast<GridType>()
                .ToList();

            var exact = types.FirstOrDefault(
                type => type.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            var partial = types.FirstOrDefault(
                type => type.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0);
            if (partial != null)
                return partial;
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(GridType))
            .Cast<GridType>()
            .FirstOrDefault();
    }

    public static IList<ViewPlan> GetFloorPlans(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(view => !view.IsTemplate && view.ViewType == ViewType.FloorPlan)
            .ToList();
    }

    private static ExtentBounds ComputeExtentBounds(
        IReadOnlyCollection<Grid> grids,
        GridDisplayConfigurationInfo options)
    {
        if (options.XExtentMin.HasValue &&
            options.XExtentMax.HasValue &&
            options.YExtentMin.HasValue &&
            options.YExtentMax.HasValue &&
            options.XExtentMin.Value < options.XExtentMax.Value &&
            options.YExtentMin.Value < options.YExtentMax.Value)
        {
            return new ExtentBounds
            {
                MinXFeet = options.XExtentMin.Value * MillimetersToFeet,
                MaxXFeet = options.XExtentMax.Value * MillimetersToFeet,
                MinYFeet = options.YExtentMin.Value * MillimetersToFeet,
                MaxYFeet = options.YExtentMax.Value * MillimetersToFeet
            };
        }

        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var maxY = double.MinValue;

        foreach (var grid in grids)
        {
            if (grid.Curve is not Line line)
                continue;

            foreach (var point in new[] { line.GetEndPoint(0), line.GetEndPoint(1) })
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
        }

        if (minX > maxX || minY > maxY)
            throw new InvalidOperationException("Unable to compute grid extents from model curves.");

        const double paddingFeet = 1.0;
        return new ExtentBounds
        {
            MinXFeet = minX - paddingFeet,
            MaxXFeet = maxX + paddingFeet,
            MinYFeet = minY - paddingFeet,
            MaxYFeet = maxY + paddingFeet
        };
    }

    /// <summary>
    /// Ensures the grid intersects all level elevations so it can appear on every floor plan.
    /// Newly created grids may only span the active level range until extended.
    /// </summary>
    public static void EnsureGridSpansAllLevels(Document doc, Grid grid)
    {
        try
        {
            grid.Maximize3DExtents();
            return;
        }
        catch
        {
            // Fall back to explicit level-based vertical range.
        }

        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .ToList();

        if (levels.Count == 0)
            return;

        var bottom = levels.Min(level => level.Elevation);
        var top = levels.Max(level => level.Elevation);
        const double paddingFeet = 10.0;

        grid.SetVerticalExtents(bottom - paddingFeet, top + paddingFeet);
    }

    /// <summary>
    /// Show/hide bubbles per working-drawing practice. "start"/"bottomLeft" keeps the bubble
    /// at the min X/Y end (numbers below, letters to the left); "end"/"topRight" the opposite.
    /// </summary>
    private static void ApplyBubbles(Grid grid, ViewPlan view, bool showBubbles, string bubbleEnd)
    {
        if (!showBubbles)
        {
            grid.HideBubbleInView(DatumEnds.End0, view);
            grid.HideBubbleInView(DatumEnds.End1, view);
            return;
        }

        var mode = (bubbleEnd ?? "both").Trim().ToLowerInvariant();
        if (mode == "both" || string.IsNullOrEmpty(mode))
        {
            grid.ShowBubbleInView(DatumEnds.End0, view);
            grid.ShowBubbleInView(DatumEnds.End1, view);
            return;
        }

        // Determine which datum end sits at the min (bottom/left) side in this view.
        var minEnd = ResolveMinEnd(grid, view);
        var maxEnd = minEnd == DatumEnds.End0 ? DatumEnds.End1 : DatumEnds.End0;

        var wantMin = mode is "start" or "min" or "bottomleft" or "bottom" or "left";

        if (wantMin)
        {
            grid.ShowBubbleInView(minEnd, view);
            grid.HideBubbleInView(maxEnd, view);
        }
        else
        {
            grid.ShowBubbleInView(maxEnd, view);
            grid.HideBubbleInView(minEnd, view);
        }
    }

    private static DatumEnds ResolveMinEnd(Grid grid, ViewPlan view)
    {
        Curve curve = null;
        try
        {
            var viewCurves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view);
            curve = viewCurves?.FirstOrDefault();
        }
        catch
        {
            // ignore, fall back to model curve
        }

        curve ??= grid.Curve;
        if (curve == null)
            return DatumEnds.End0;

        var p0 = curve.GetEndPoint(0);
        var p1 = curve.GetEndPoint(1);

        // Vertical grid (constant X) → compare Y (bottom = min). Otherwise compare X (left = min).
        bool vertical = Math.Abs(p1.X - p0.X) < Math.Abs(p1.Y - p0.Y);
        double c0 = vertical ? p0.Y : p0.X;
        double c1 = vertical ? p1.Y : p1.X;

        return c0 <= c1 ? DatumEnds.End0 : DatumEnds.End1;
    }

    private static bool ApplyGridExtentInView(Grid grid, ViewPlan view, ExtentBounds bounds)
    {
        if (grid.Curve is not Line modelLine)
            return false;

        EnsureViewSpecificExtent(grid, view);

        var viewCurve = BuildViewCurve(grid, view, bounds, modelLine);
        if (viewCurve == null)
            return false;

        grid.SetCurveInView(DatumExtentType.ViewSpecific, view, viewCurve);
        return true;
    }

    private static void EnsureViewSpecificExtent(Grid grid, ViewPlan view)
    {
        foreach (var end in new[] { DatumEnds.End0, DatumEnds.End1 })
        {
            if (grid.GetDatumExtentTypeInView(end, view) == DatumExtentType.ViewSpecific)
                continue;

            grid.SetDatumExtentType(end, view, DatumExtentType.ViewSpecific);
        }
    }

    private static Line BuildViewCurve(Grid grid, ViewPlan view, ExtentBounds bounds, Line modelLine)
    {
        var p0 = modelLine.GetEndPoint(0);
        var p1 = modelLine.GetEndPoint(1);
        var delta = p1 - p0;
        var length = delta.GetLength();
        if (length < 1e-9)
            return null;

        var direction = delta / length;
        var z = p0.Z;

        var xMin = Math.Min(bounds.MinXFeet, bounds.MaxXFeet);
        var xMax = Math.Max(bounds.MinXFeet, bounds.MaxXFeet);
        var yMin = Math.Min(bounds.MinYFeet, bounds.MaxYFeet);
        var yMax = Math.Max(bounds.MinYFeet, bounds.MaxYFeet);

        var candidates = new List<Line>();

        if (Math.Abs(direction.X) < AxisEpsilon)
        {
            var x = (p0.X + p1.X) * 0.5;
            candidates.Add(Line.CreateBound(new XYZ(x, yMin, z), new XYZ(x, yMax, z)));
        }
        else if (Math.Abs(direction.Y) < AxisEpsilon)
        {
            var y = (p0.Y + p1.Y) * 0.5;
            candidates.Add(Line.CreateBound(new XYZ(xMin, y, z), new XYZ(xMax, y, z)));
        }
        else
        {
            candidates.Add(BuildProjectedViewCurve(p0, direction, bounds));
        }

        candidates.Add(modelLine);

        foreach (var candidate in candidates)
        {
            if (candidate == null)
                continue;

            if (grid.IsCurveValidInView(DatumExtentType.ViewSpecific, view, candidate))
                return candidate;
        }

        return null;
    }

    private static Line BuildProjectedViewCurve(XYZ origin, XYZ direction, ExtentBounds bounds)
    {
        var ts = new List<double>();

        if (Math.Abs(direction.X) > AxisEpsilon)
        {
            ts.Add((bounds.MinXFeet - origin.X) / direction.X);
            ts.Add((bounds.MaxXFeet - origin.X) / direction.X);
        }

        if (Math.Abs(direction.Y) > AxisEpsilon)
        {
            ts.Add((bounds.MinYFeet - origin.Y) / direction.Y);
            ts.Add((bounds.MaxYFeet - origin.Y) / direction.Y);
        }

        if (ts.Count < 2)
            throw new InvalidOperationException("Unable to project grid extent along the datum line.");

        var tMin = ts.Min();
        var tMax = ts.Max();
        if (Math.Abs(tMax - tMin) < 1e-9)
            throw new InvalidOperationException("Projected grid extent is too small.");

        return Line.CreateBound(
            origin + (direction * tMin),
            origin + (direction * tMax));
    }
}
