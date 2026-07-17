using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Annotation;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.AnnotationComponents;

/// <summary>
///     Creates exterior axial dimension chains offset from the full building envelope
///     (walls including loggias/balconies), not from grid axis coordinates.
///     Working-drawing layout: numbers bottom, letters left; inter-axis then overall tiers.
/// </summary>
public class DimensionGridsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double FeetToMm = 304.8;
    private GridDimensionInfo _info;
    private readonly ManualResetEvent _resetEvent = new(false);

    public AIResult<object> Result { get; private set; }

    public void SetParameters(GridDimensionInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _resetEvent.Reset();
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var uiDoc = app.ActiveUIDocument
                ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uiDoc.Document;
            var view = ResolveView(doc, uiDoc, _info.ViewId);
            if (view is not ViewPlan viewPlan)
                throw new InvalidOperationException("Axial dimensions require an active floor plan view.");

            var grids = ResolveGrids(doc, _info.GridIds);
            if (grids.Count < 2)
                throw new InvalidOperationException(
                    "Need at least 2 grids to create axial dimensions. Create grids first.");

            var vertical = ClassifyGrids(grids, vertical: true);
            var horizontal = ClassifyGrids(grids, vertical: false);
            if (vertical.Count < 2 && horizontal.Count < 2)
                throw new InvalidOperationException(
                    "Need at least 2 grids in one direction (vertical or horizontal).");

            var levelId = viewPlan.GenLevel?.Id ?? ElementId.InvalidElementId;
            var envelope = ComputeBuildingEnvelopeMm(doc, levelId, viewPlan, _info.EnvelopePaddingMm);
            if (envelope == null)
                throw new InvalidOperationException(
                    "Could not compute building envelope from walls on this level.");

            var firstOffset = _info.FirstOffsetMm > 0 ? _info.FirstOffsetMm : 1200;
            var tierGap = _info.TierGapMm > 0 ? _info.TierGapMm : 800;
            var bubbleClearance = _info.BubbleClearanceMm > 0 ? _info.BubbleClearanceMm : 1200;

            var createdIds = new List<int>();
            var warnings = new List<string>();

            using (var transaction = new Transaction(doc, "Dimension Grids"))
            {
                transaction.Start();

                if (vertical.Count >= 2)
                {
                    var sorted = vertical.OrderBy(g => g.PositionMm).ToList();
                    var lineY = ComputeExteriorLineCoordinate(
                        envelope.MinYMm,
                        envelope.MaxYMm,
                        firstOffset,
                        IsBottomSide(_info.NumericSide));

                    var inter = CreateChain(doc, viewPlan, sorted, forHorizontalChain: true, lineY);
                    if (inter != null)
                        createdIds.Add(inter.Id.GetIntValue());

                    if (_info.IncludeOverall && sorted.Count >= 2)
                    {
                        var overallY = ComputeExteriorLineCoordinate(
                            envelope.MinYMm,
                            envelope.MaxYMm,
                            firstOffset + tierGap,
                            IsBottomSide(_info.NumericSide));
                        var extremes = new List<GridAxis>
                        {
                            sorted.First(),
                            sorted.Last()
                        };
                        var overall = CreateChain(doc, viewPlan, extremes, forHorizontalChain: true, overallY);
                        if (overall != null)
                            createdIds.Add(overall.Id.GetIntValue());
                    }
                }
                else
                {
                    warnings.Add("Fewer than 2 vertical grids — skipped bottom/top chains.");
                }

                if (horizontal.Count >= 2)
                {
                    var sorted = horizontal.OrderBy(g => g.PositionMm).ToList();
                    var lineX = ComputeExteriorLineCoordinate(
                        envelope.MinXMm,
                        envelope.MaxXMm,
                        firstOffset,
                        IsLeftSide(_info.LetterSide));

                    var inter = CreateChain(doc, viewPlan, sorted, forHorizontalChain: false, lineX);
                    if (inter != null)
                        createdIds.Add(inter.Id.GetIntValue());

                    if (_info.IncludeOverall && sorted.Count >= 2)
                    {
                        var overallX = ComputeExteriorLineCoordinate(
                            envelope.MinXMm,
                            envelope.MaxXMm,
                            firstOffset + tierGap,
                            IsLeftSide(_info.LetterSide));
                        var extremes = new List<GridAxis>
                        {
                            sorted.First(),
                            sorted.Last()
                        };
                        var overall = CreateChain(doc, viewPlan, extremes, forHorizontalChain: false, overallX);
                        if (overall != null)
                            createdIds.Add(overall.Id.GetIntValue());
                    }
                }
                else
                {
                    warnings.Add("Fewer than 2 horizontal grids — skipped left/right chains.");
                }

                if (_info.ExtendGridExtents && createdIds.Count > 0)
                {
                    try
                    {
                        ExtendGridExtentsBeyondTiers(
                            doc,
                            viewPlan,
                            grids,
                            envelope,
                            firstOffset,
                            _info.IncludeOverall ? tierGap : 0,
                            bubbleClearance);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Grid extent extension: {ex.Message}");
                    }
                }

                transaction.Commit();
            }

            if (createdIds.Count == 0)
                throw new InvalidOperationException("No axial dimensions could be created.");

            var message =
                $"Successfully created {createdIds.Count} axial dimension chain(s) " +
                $"from building envelope " +
                $"(X [{envelope.MinXMm:F0}..{envelope.MaxXMm:F0}], " +
                $"Y [{envelope.MinYMm:F0}..{envelope.MaxYMm:F0}] mm), " +
                $"firstOffset={firstOffset} mm.";
            if (warnings.Count > 0)
                message += " " + string.Join(" ", warnings);

            Result = new AIResult<object>
            {
                Success = true,
                Message = message,
                Response = new
                {
                    dimensionIds = createdIds,
                    envelopeMm = new
                    {
                        minX = envelope.MinXMm,
                        maxX = envelope.MaxXMm,
                        minY = envelope.MinYMm,
                        maxY = envelope.MaxYMm
                    },
                    firstOffsetMm = firstOffset,
                    tierGapMm = tierGap,
                    warnings
                }
            };
        }
        catch (Exception ex)
        {
            Result = new AIResult<object>
            {
                Success = false,
                Message = $"Error creating axial dimensions: {ex.Message}",
                Response = null
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public string GetName() => "Dimension Grids";

    /// <summary>
    ///     Exterior dimension line coordinate: envMin - offset (bottom/left) or envMax + offset (top/right).
    ///     Exposed for unit tests.
    /// </summary>
    public static double ComputeExteriorLineCoordinate(
        double envMinMm,
        double envMaxMm,
        double offsetMm,
        bool towardMin)
    {
        return towardMin ? envMinMm - offsetMm : envMaxMm + offsetMm;
    }

    public static bool IsBottomSide(string side)
    {
        var s = (side ?? "bottom").Trim().ToLowerInvariant();
        return s is not ("top" or "max" or "north");
    }

    public static bool IsLeftSide(string side)
    {
        var s = (side ?? "left").Trim().ToLowerInvariant();
        return s is not ("right" or "max" or "east");
    }

    private Dimension CreateChain(
        Document doc,
        ViewPlan view,
        IReadOnlyList<GridAxis> axes,
        bool forHorizontalChain,
        double lineCoordMm)
    {
        if (axes.Count < 2)
            return null;

        var references = new ReferenceArray();
        foreach (var axis in axes)
            references.Append(new Reference(axis.Grid));

        if (references.Size < 2)
            return null;

        Line line;
        if (forHorizontalChain)
        {
            // Horizontal dimension line (measures X between vertical grids)
            var y = lineCoordMm / FeetToMm;
            var x0 = axes.First().PositionMm / FeetToMm;
            var x1 = axes.Last().PositionMm / FeetToMm;
            var z = axes.First().Grid.Curve.GetEndPoint(0).Z;
            line = Line.CreateBound(new XYZ(x0, y, z), new XYZ(x1, y, z));
        }
        else
        {
            // Vertical dimension line (measures Y between horizontal grids)
            var x = lineCoordMm / FeetToMm;
            var y0 = axes.First().PositionMm / FeetToMm;
            var y1 = axes.Last().PositionMm / FeetToMm;
            var z = axes.First().Grid.Curve.GetEndPoint(0).Z;
            line = Line.CreateBound(new XYZ(x, y0, z), new XYZ(x, y1, z));
        }

        var dimension = doc.Create.NewDimension(view, line, references);
        if (dimension == null)
            return null;

        DimensionAnnotationHelper.ApplyDimensionType(
            dimension,
            doc,
            _info.DimensionType,
            _info.DimensionStyleId);
        return dimension;
    }

    private void ExtendGridExtentsBeyondTiers(
        Document doc,
        ViewPlan view,
        IReadOnlyList<Grid> grids,
        EnvelopeMm envelope,
        double firstOffsetMm,
        double tierGapMm,
        double bubbleClearanceMm)
    {
        var outer = firstOffsetMm + tierGapMm + bubbleClearanceMm;
        var options = new GridDisplayConfigurationInfo
        {
            XExtentMin = envelope.MinXMm - outer,
            XExtentMax = envelope.MaxXMm + outer,
            YExtentMin = envelope.MinYMm - outer,
            YExtentMax = envelope.MaxYMm + outer,
            ShowBubbles = true,
            BubbleEnd = "bottomLeft",
            ApplyToAllFloorPlans = false,
            GridTypeName = string.Empty,
            GridTypeId = -1
        };

        GridDisplayHelper.ConfigureGrids(doc, grids, options, view);
    }

    private static View ResolveView(Document doc, UIDocument uiDoc, int viewId)
    {
        if (viewId > 0)
        {
            var byId = doc.GetElement(new ElementId(viewId)) as View;
            if (byId != null)
                return byId;
        }

        return uiDoc.ActiveView;
    }

    private static List<Grid> ResolveGrids(Document doc, IEnumerable<int> gridIds)
    {
        if (gridIds != null && gridIds.Any(id => id > 0))
        {
            return gridIds
                .Where(id => id > 0)
                .Select(id => doc.GetElement(new ElementId(id)) as Grid)
                .Where(g => g != null)
                .ToList();
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(Grid))
            .Cast<Grid>()
            .ToList();
    }

    private static List<GridAxis> ClassifyGrids(IEnumerable<Grid> grids, bool vertical)
    {
        var result = new List<GridAxis>();
        foreach (var grid in grids)
        {
            if (grid.Curve is not Line line)
                continue;

            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            var dx = Math.Abs(p1.X - p0.X);
            var dy = Math.Abs(p1.Y - p0.Y);
            var isVertical = dy >= dx; // constant X

            if (vertical && isVertical)
            {
                result.Add(new GridAxis
                {
                    Grid = grid,
                    PositionMm = ((p0.X + p1.X) * 0.5) * FeetToMm
                });
            }
            else if (!vertical && !isVertical)
            {
                result.Add(new GridAxis
                {
                    Grid = grid,
                    PositionMm = ((p0.Y + p1.Y) * 0.5) * FeetToMm
                });
            }
        }

        return result;
    }

    /// <summary>
    ///     Full building envelope from all walls on the level (bbox), plus padding for wall faces.
    /// </summary>
    public static EnvelopeMm ComputeBuildingEnvelopeMm(
        Document doc,
        ElementId levelId,
        View view,
        double paddingMm)
    {
        var walls = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .Where(w => WallIsOnLevel(w, levelId))
            .ToList();

        if (walls.Count == 0)
            return null;

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        var found = false;

        foreach (var wall in walls)
        {
            var bbox = wall.get_BoundingBox(view) ?? wall.get_BoundingBox(null);
            if (bbox == null)
                continue;

            found = true;
            minX = Math.Min(minX, bbox.Min.X);
            maxX = Math.Max(maxX, bbox.Max.X);
            minY = Math.Min(minY, bbox.Min.Y);
            maxY = Math.Max(maxY, bbox.Max.Y);
        }

        if (!found)
            return null;

        var pad = Math.Max(0, paddingMm) / FeetToMm;
        return new EnvelopeMm
        {
            MinXMm = (minX - pad) * FeetToMm,
            MaxXMm = (maxX + pad) * FeetToMm,
            MinYMm = (minY - pad) * FeetToMm,
            MaxYMm = (maxY + pad) * FeetToMm
        };
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

        const double eps = 0.05; // ~15 mm
        return bbox.Min.Z - eps <= level.Elevation && bbox.Max.Z + eps >= level.Elevation;
    }

    public sealed class EnvelopeMm
    {
        public double MinXMm { get; set; }
        public double MaxXMm { get; set; }
        public double MinYMm { get; set; }
        public double MaxYMm { get; set; }
    }

    private sealed class GridAxis
    {
        public Grid Grid { get; set; }
        public double PositionMm { get; set; }
    }
}
