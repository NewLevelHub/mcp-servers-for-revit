using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Architecture;

/// <summary>
/// Creates stairs via StairsEditScope (REV-83+):
/// straight, L (Г-образная), U (П-образная) with automatic landing.
/// typeId must resolve to StairsType — no silent FirstOrDefault fallback.
/// </summary>
public class CreateStairEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private Document _doc => _uiApp.ActiveUIDocument.Document;

    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public List<StairCreationInfo> StairData { get; private set; }

    public AIResult<List<StairResultInfo>> Result { get; private set; }

    public void SetParameters(List<StairCreationInfo> data)
    {
        StairData = data;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 30000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        var created = new List<StairResultInfo>();
        var errors = new List<string>();

        try
        {
            for (var index = 0; index < StairData.Count; index++)
            {
                try
                {
                    created.Add(CreateOneStair(StairData[index]));
                }
                catch (Exception ex)
                {
                    errors.Add($"[{index}] {ex.Message}");
                }
            }

            if (errors.Count > 0 && created.Count == 0)
            {
                Result = new AIResult<List<StairResultInfo>>
                {
                    Success = false,
                    Message = string.Join("; ", errors),
                    Response = created
                };
            }
            else if (errors.Count > 0)
            {
                Result = new AIResult<List<StairResultInfo>>
                {
                    Success = true,
                    Message = $"Created {created.Count} stair(s) with warnings: {string.Join("; ", errors)}",
                    Response = created
                };
            }
            else
            {
                Result = new AIResult<List<StairResultInfo>>
                {
                    Success = true,
                    Message = $"Created {created.Count} stair(s)",
                    Response = created
                };
            }
        }
        catch (Exception ex)
        {
            Result = new AIResult<List<StairResultInfo>>
            {
                Success = false,
                Message = $"Create stair failed: {ex.Message}",
                Response = created
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private StairResultInfo CreateOneStair(StairCreationInfo info)
    {
        if (info.TypeId <= 0)
        {
            throw new ArgumentException(
                "typeId is required. Call get_available_family_types (StairsType) and pass a valid typeId.");
        }

        if (info.BaseLevelId <= 0 || info.TopLevelId <= 0)
            throw new ArgumentException("baseLevelId and topLevelId are required.");

        if (info.WidthMm <= 0)
            throw new ArgumentException("widthMm must be > 0 (resolve from norms on the server if omitted).");

        var layout = NormalizeLayout(info.Layout);
        var stairsType = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.TypeId)) as StairsType;
        if (stairsType == null)
        {
            throw new ArgumentException(
                $"typeId {info.TypeId} not found or is not a StairsType. Call get_available_family_types.");
        }

        var baseLevel = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.BaseLevelId)) as Level;
        var topLevel = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.TopLevelId)) as Level;
        if (baseLevel == null)
            throw new ArgumentException($"baseLevelId {info.BaseLevelId} is not a Level.");
        if (topLevel == null)
            throw new ArgumentException($"topLevelId {info.TopLevelId} is not a Level.");
        if (topLevel.Elevation <= baseLevel.Elevation)
            throw new ArgumentException("topLevel must be above baseLevel.");

        var warnings = new List<string>();
        var treadMmHint = info.TreadDepthMm > 0 ? info.TreadDepthMm : 300;
        var riserMmHint = info.RiserHeightMm > 0 ? info.RiserHeightMm : 150;
        ApplyShaftOrMirror(info, ref layout, treadMmHint, riserMmHint, baseLevel, topLevel, warnings);

        if (info.StartPoint == null)
        {
            throw new ArgumentException(
                "startPoint is required (mm), or provide shaftRect / mirrorElementId to place inside a cell.");
        }

        var widthInternal = RevitUnitConversion.FromMillimeters(info.WidthMm);
        ElementId newStairsId;
        var landingCount = 0;

        using (var scope = new StairsEditScope(_doc, "MCP Create Stair"))
        {
            newStairsId = scope.Start(baseLevel.Id, topLevel.Id);

            using (var tx = new Transaction(_doc, "MCP Add Stair Runs"))
            {
                tx.Start();

                var stairs = _doc.GetElement(newStairsId) as Stairs;
                if (stairs == null)
                    throw new InvalidOperationException("StairsEditScope.Start did not create a Stairs element.");

                stairs.ChangeTypeId(stairsType.Id);

                var treadMm = info.TreadDepthMm > 0
                    ? info.TreadDepthMm
                    : RevitUnitConversion.ToMillimeters(stairs.ActualTreadDepth);
                if (treadMm < 200 || treadMm > 400)
                    treadMm = 300;

                var riserMm = info.RiserHeightMm > 0
                    ? info.RiserHeightMm
                    : RevitUnitConversion.ToMillimeters(stairs.ActualRiserHeight);
                if (riserMm < 120 || riserMm > 220)
                    riserMm = 150;

                var heightMm = RevitUnitConversion.ToMillimeters(topLevel.Elevation - baseLevel.Elevation);
                var totalRisers = Math.Max(2, (int)Math.Round(heightMm / riserMm));

                // Pin desired riser count to story height before runs are sized.
                var desiredRisersParam = stairs.get_Parameter(BuiltInParameter.STAIRS_DESIRED_NUMBER_OF_RISERS);
                if (desiredRisersParam != null && !desiredRisersParam.IsReadOnly)
                    desiredRisersParam.Set(totalRisers);

                switch (layout)
                {
                    case "straight":
                        CreateStraight(info, baseLevel, newStairsId, widthInternal);
                        break;
                    case "L":
                        landingCount = CreateLOrU(
                            info, baseLevel, newStairsId, widthInternal, treadMm, totalRisers, uShape: false);
                        break;
                    case "U":
                        landingCount = CreateLOrU(
                            info, baseLevel, newStairsId, widthInternal, treadMm, totalRisers, uShape: true);
                        break;
                    default:
                        throw new ArgumentException(
                            $"Unknown layout '{info.Layout}'. Use straight, L (Г), or U (П).");
                }

                // Re-assert after runs: path length can inflate ActualRisersNumber.
                if (desiredRisersParam != null && !desiredRisersParam.IsReadOnly)
                    desiredRisersParam.Set(totalRisers);

                tx.Commit();
            }

            scope.Commit(new StairsWarningPreprocessor());
        }

        var created = _doc.GetElement(newStairsId) as Stairs;
        if (created == null)
            throw new InvalidOperationException("Stairs were not committed successfully.");

        double appliedWidthMm = info.WidthMm;
        var runIds = created.GetStairsRuns();
        if (runIds != null && runIds.Count > 0 &&
            _doc.GetElement(runIds.First()) is StairsRun firstRun)
        {
            appliedWidthMm = RevitUnitConversion.ToMillimeters(firstRun.ActualRunWidth);
        }

        var landingIds = created.GetStairsLandings();
        if (landingIds != null && landingIds.Count > 0)
            landingCount = landingIds.Count;

        var desiredRisers = Math.Max(2, (int)Math.Round(
            RevitUnitConversion.ToMillimeters(topLevel.Elevation - baseLevel.Elevation) /
            Math.Max(1.0, info.RiserHeightMm > 0 ? info.RiserHeightMm : 150)));
        if (created.ActualRisersNumber != desiredRisers)
        {
            warnings.Add(
                $"ActualRisersNumber={created.ActualRisersNumber} vs desired={desiredRisers} for story height. " +
                "Shaft may be too short for ideal tread — enlarge shaftRect or use fitMode=extend.");
        }

        return new StairResultInfo
        {
            ElementId = created.Id.GetIntValue(),
            UniqueId = created.UniqueId,
            TypeId = stairsType.Id.GetIntValue(),
            TypeName = stairsType.Name,
            Layout = layout,
            BaseLevelId = baseLevel.Id.GetIntValue(),
            TopLevelId = topLevel.Id.GetIntValue(),
            AppliedWidthMm = Math.Round(appliedWidthMm, 1),
            ActualRiserHeightMm = Math.Round(RevitUnitConversion.ToMillimeters(created.ActualRiserHeight), 1),
            ActualTreadDepthMm = Math.Round(RevitUnitConversion.ToMillimeters(created.ActualTreadDepth), 1),
            RunCount = runIds?.Count ?? 0,
            LandingCount = landingCount,
            ActualNumRisers = created.ActualRisersNumber,
            Warnings = warnings.Count > 0 ? warnings : null
        };
    }

    /// <summary>
    /// Fit U/L into shaftRect or mirrorElementId bbox so the stair stays compact
    /// like a typical-floor reference cell (does not elongate past the shaft).
    /// </summary>
    private void ApplyShaftOrMirror(
        StairCreationInfo info,
        ref string layout,
        double treadMm,
        double riserMm,
        Level baseLevel,
        Level topLevel,
        List<string> warnings)
    {
        FloorOpeningRect shaft = info.ShaftRect;

        if (info.MirrorElementId > 0)
        {
            var mirrored = _doc.GetElement(
                RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.MirrorElementId)) as Stairs;
            if (mirrored == null)
            {
                throw new ArgumentException(
                    $"mirrorElementId {info.MirrorElementId} is not a Stairs element.");
            }

            var bb = mirrored.get_BoundingBox(null);
            if (bb == null)
                throw new ArgumentException($"mirrorElementId {info.MirrorElementId} has no bounding box.");

            shaft = new FloorOpeningRect
            {
                Origin = new JZPoint(
                    RevitUnitConversion.ToMillimeters(bb.Min.X),
                    RevitUnitConversion.ToMillimeters(bb.Min.Y)),
                WidthMm = RevitUnitConversion.ToMillimeters(bb.Max.X - bb.Min.X),
                DepthMm = RevitUnitConversion.ToMillimeters(bb.Max.Y - bb.Min.Y),
                RotationDeg = 0
            };

            // Prefer reference run width when caller left default/norm width.
            var refRuns = mirrored.GetStairsRuns();
            if (refRuns != null && refRuns.Count > 0 &&
                _doc.GetElement(refRuns.First()) is StairsRun refRun)
            {
                var refW = RevitUnitConversion.ToMillimeters(refRun.ActualRunWidth);
                if (refW > 0 && (info.WidthMm <= 0 || Math.Abs(info.WidthMm - refW) < 1))
                    info.WidthMm = Math.Round(refW, 1);
            }

            if (layout is not ("U" or "L"))
                layout = "U";
            info.Layout = layout;
            warnings.Add(
                $"Plan fitted to mirrorElementId {info.MirrorElementId} bbox " +
                $"{Math.Round(shaft.WidthMm)}×{Math.Round(shaft.DepthMm)} mm.");
        }

        if (shaft == null || shaft.Origin == null)
            return;

        if (shaft.WidthMm <= 0 || shaft.DepthMm <= 0)
            throw new ArgumentException("shaftRect.widthMm and depthMm must be > 0.");

        if (layout is not ("U" or "L"))
        {
            layout = "U";
            info.Layout = "U";
        }

        const double marginMm = 40;
        var fitMode = string.IsNullOrWhiteSpace(info.FitMode)
            ? "clamp"
            : info.FitMode.Trim().ToLowerInvariant();

        if (info.WidthMm * 2 + 2 * marginMm > shaft.WidthMm + 0.5)
        {
            throw new ArgumentException(
                $"shaftRect width {Math.Round(shaft.WidthMm)} mm is too narrow for two runs of " +
                $"{info.WidthMm} mm (need ≥ {Math.Round(info.WidthMm * 2 + 2 * marginMm)} mm).");
        }

        var landingMm = info.LandingDepthMm > 0 ? info.LandingDepthMm : info.WidthMm;
        var availRunMm = shaft.DepthMm - landingMm - 2 * marginMm;
        if (availRunMm < 800)
        {
            throw new ArgumentException(
                $"shaftRect depth {Math.Round(shaft.DepthMm)} mm too shallow for U-stair " +
                $"(landing {landingMm} mm + margins). Need deeper cell.");
        }

        var heightMm = RevitUnitConversion.ToMillimeters(topLevel.Elevation - baseLevel.Elevation);
        var totalRisers = Math.Max(2, (int)Math.Round(heightMm / Math.Max(1.0, riserMm)));
        var firstRisers = Math.Max(1, totalRisers / 2);
        var idealRunMm = Math.Max(treadMm, (firstRisers - 1) * treadMm);

        double runMm;
        if (fitMode is "extend")
        {
            runMm = idealRunMm;
            if (idealRunMm > availRunMm + 1)
            {
                warnings.Add(
                    $"fitMode=extend: run {Math.Round(idealRunMm)} mm exceeds shaft depth capacity " +
                    $"{Math.Round(availRunMm)} mm — stair will stick out of the cell.");
            }
        }
        else if (fitMode is "strict")
        {
            if (idealRunMm > availRunMm + 1)
            {
                var needDepth = idealRunMm + landingMm + 2 * marginMm;
                throw new ArgumentException(
                    $"Shaft too short for story height {Math.Round(heightMm)} mm at tread {treadMm} mm. " +
                    $"Ideal run {Math.Round(idealRunMm)} mm, available {Math.Round(availRunMm)} mm. " +
                    $"Need shaft depth ≥ {Math.Round(needDepth)} mm, or fitMode=clamp/extend.");
            }
            runMm = idealRunMm;
        }
        else
        {
            // clamp (default): stay inside cell like typical-floor reference.
            runMm = Math.Min(idealRunMm, availRunMm);
            if (idealRunMm > availRunMm + 1)
            {
                warnings.Add(
                    $"Clamped run to shaft ({Math.Round(runMm)} mm < ideal {Math.Round(idealRunMm)} mm). " +
                    $"Story {Math.Round(heightMm)} mm is taller than this cell was designed for " +
                    $"(typical floors ~{(firstRisers * riserMm):0} mm). Prefer a deeper shaftRect on this level.");
            }
        }

        info.FirstRunLengthMm = runMm;
        info.SecondRunLengthMm = runMm;
        info.LandingDepthMm = landingMm;

        // Axis-aligned shaft: origin SW, width +X, depth +Y → first run along +Y (bearing 90).
        // With rotationDeg: rotate local axes.
        var rad = shaft.RotationDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        // Local start: left run center near south edge.
        var lx = marginMm + info.WidthMm * 0.5;
        var ly = marginMm;
        var wx = shaft.Origin.X + lx * cos - ly * sin;
        var wy = shaft.Origin.Y + lx * sin + ly * cos;

        info.StartPoint = new JZPoint(wx, wy);
        info.BearingDeg = shaft.RotationDeg + 90; // local +Y
        info.EndPoint = null;
        if (string.IsNullOrWhiteSpace(info.Turn))
            info.Turn = "right";
    }

    private void CreateStraight(
        StairCreationInfo info,
        Level baseLevel,
        ElementId stairsId,
        double widthInternal)
    {
        if (info.EndPoint == null)
            throw new ArgumentException("endPoint is required for layout=straight.");

        var baseZ = baseLevel.Elevation;
        var p0 = Flat(JZPoint.ToXYZ(info.StartPoint), baseZ);
        var p1 = Flat(JZPoint.ToXYZ(info.EndPoint), baseZ);
        if (p0.DistanceTo(p1) < 1e-6)
            throw new ArgumentException("startPoint and endPoint must define a non-zero path.");

        var run = StairsRun.CreateStraightRun(
            _doc,
            stairsId,
            Line.CreateBound(p0, p1),
            StairsRunJustification.Center);
        run.ActualRunWidth = widthInternal;
    }

    private int CreateLOrU(
        StairCreationInfo info,
        Level baseLevel,
        ElementId stairsId,
        double widthInternal,
        double treadMm,
        int totalRisers,
        bool uShape)
    {
        var dir = ResolveFirstRunDirection(info);
        var turnLeft = IsTurnLeft(info.Turn);
        var perp = turnLeft
            ? new XYZ(-dir.Y, dir.X, 0).Normalize()
            : new XYZ(dir.Y, -dir.X, 0).Normalize();

        var firstRisers = Math.Max(1, totalRisers / 2);
        var secondRisers = Math.Max(1, totalRisers - firstRisers);

        // Going length ≈ (risers − 1) × tread — using risers×tread overshoots DesiredNumRisers.
        var len1Mm = info.FirstRunLengthMm > 0
            ? info.FirstRunLengthMm
            : Math.Max(treadMm, (firstRisers - 1) * treadMm);
        var len2Mm = info.SecondRunLengthMm > 0
            ? info.SecondRunLengthMm
            : Math.Max(treadMm, (secondRisers - 1) * treadMm);
        var len1 = RevitUnitConversion.FromMillimeters(len1Mm);
        var len2 = RevitUnitConversion.FromMillimeters(len2Mm);

        var widthMm = RevitUnitConversion.ToMillimeters(widthInternal);
        var landingDepthMm = info.LandingDepthMm > 0 ? info.LandingDepthMm : widthMm;
        var landingDepth = RevitUnitConversion.FromMillimeters(landingDepthMm);

        var baseZ = baseLevel.Elevation;
        var run1Start = Flat(JZPoint.ToXYZ(info.StartPoint), baseZ);
        var run1End = run1Start + dir * len1;

        var run1 = StairsRun.CreateStraightRun(
            _doc,
            stairsId,
            Line.CreateBound(run1Start, run1End),
            StairsRunJustification.Center);
        run1.ActualRunWidth = widthInternal;

        var z2 = run1.TopElevation;
        XYZ run2Start;
        XYZ run2End;

        if (uShape)
        {
            // П: second run parallel opposite, offset by run width (side-by-side in shaft).
            var offset = widthInternal;
            var landingShift = dir * Math.Max(landingDepth * 0.5, widthInternal * 0.25);
            run2Start = Flat(run1End + perp * offset + landingShift, z2);
            run2End = Flat(run2Start - dir * len2, z2);
        }
        else
        {
            // Г: second run turns 90°.
            var landingShift = dir * Math.Max(landingDepth * 0.5, widthInternal * 0.25);
            run2Start = Flat(run1End + landingShift + perp * (widthInternal * 0.5), z2);
            run2End = Flat(run2Start + perp * len2, z2);
        }

        if (run2Start.DistanceTo(run2End) < 1e-6)
            throw new InvalidOperationException("Second run path length is zero — check tread/riser sizing.");

        var run2 = StairsRun.CreateStraightRun(
            _doc,
            stairsId,
            Line.CreateBound(run2Start, run2End),
            StairsRunJustification.Center);
        run2.ActualRunWidth = widthInternal;

        try
        {
            if (StairsLanding.CanCreateAutomaticLanding(_doc, run1.Id, run2.Id))
            {
                var landingIds = StairsLanding.CreateAutomaticLanding(_doc, run1.Id, run2.Id);
                return landingIds?.Count ?? 0;
            }
        }
        catch
        {
            // fall through to sketched landing
        }

        return CreateSketchedLandingFallback(stairsId, run1, dir, perp, widthInternal, landingDepth, uShape);
    }

    private int CreateSketchedLandingFallback(
        ElementId stairsId,
        StairsRun run1,
        XYZ dir,
        XYZ perp,
        double widthInternal,
        double landingDepth,
        bool uShape)
    {
        var z = run1.TopElevation;
        var path = run1.GetStairsPath();
        if (path == null || !path.Any())
            throw new InvalidOperationException("First run has no stairs path for landing fallback.");

        var end1 = Flat(path.First().GetEndPoint(1), z);
        var halfW = widthInternal * 0.5;
        var toward = perp;

        var a = Flat(end1 - toward * halfW, z);
        var b = Flat(end1 + toward * (uShape ? widthInternal + halfW : halfW), z);
        var c = Flat(b + dir * landingDepth, z);
        var d = Flat(a + dir * landingDepth, z);

        var loop = new CurveLoop();
        loop.Append(Line.CreateBound(a, b));
        loop.Append(Line.CreateBound(b, c));
        loop.Append(Line.CreateBound(c, d));
        loop.Append(Line.CreateBound(d, a));

        try
        {
            // R25 signature: (Document, stairsId, CurveLoop, baseElevation)
            StairsLanding.CreateSketchedLanding(_doc, stairsId, loop, z);
            return 1;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not create automatic or sketched landing between runs. " +
                "Adjust startPoint/bearing/turn/width or use layout=straight. Details: " + ex.Message);
        }
    }

    private static XYZ ResolveFirstRunDirection(StairCreationInfo info)
    {
        if (info.EndPoint != null)
        {
            var a = JZPoint.ToXYZ(info.StartPoint);
            var b = JZPoint.ToXYZ(info.EndPoint);
            var v = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (v.GetLength() > 1e-6)
                return v.Normalize();
        }

        if (info.BearingDeg.HasValue)
        {
            var rad = info.BearingDeg.Value * Math.PI / 180.0;
            return new XYZ(Math.Cos(rad), Math.Sin(rad), 0).Normalize();
        }

        throw new ArgumentException(
            "For layout L/U provide bearingDeg (0=east, 90=north) or endPoint as direction hint.");
    }

    private static bool IsTurnLeft(string turn)
    {
        if (string.IsNullOrWhiteSpace(turn)) return false;
        var t = turn.Trim().ToLowerInvariant();
        return t is "left" or "l" or "лево" or "л";
    }

    private static string NormalizeLayout(string layout)
    {
        if (string.IsNullOrWhiteSpace(layout)) return "straight";
        var s = layout.Trim().ToLowerInvariant();
        if (s is "straight" or "direct" or "прямо" or "прямая") return "straight";
        if (s is "l" or "g" or "г" or "г-образная" or "gobraznaya" or "l-shape" or "lshape") return "L";
        if (s is "u" or "p" or "п" or "п-образная" or "u-shape" or "ushape") return "U";
        if (string.Equals(layout.Trim(), "L", StringComparison.OrdinalIgnoreCase)) return "L";
        if (string.Equals(layout.Trim(), "U", StringComparison.OrdinalIgnoreCase)) return "U";
        return layout.Trim();
    }

    private static XYZ Flat(XYZ p, double z) => new XYZ(p.X, p.Y, z);

    public string GetName() => "CreateStair";
}

internal class StairsWarningPreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        foreach (var failure in failuresAccessor.GetFailureMessages())
        {
            if (failure.GetSeverity() == FailureSeverity.Warning)
                failuresAccessor.DeleteWarning(failure);
        }

        return FailureProcessingResult.Continue;
    }
}
