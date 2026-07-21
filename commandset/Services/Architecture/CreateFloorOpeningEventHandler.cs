using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Architecture;

/// <summary>
/// Creates floor openings and shaft openings (REV-85).
/// Floor: Document.Create.NewOpening(host, profile, perpendicular).
/// Shaft: Document.Create.NewOpening(bottomLevel, topLevel, profile).
/// Explicit fail when host/levels/profile invalid — no silent skip.
/// </summary>
public class CreateFloorOpeningEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private Document _doc => _uiApp.ActiveUIDocument.Document;

    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public List<FloorOpeningCreationInfo> OpeningData { get; private set; }

    public AIResult<List<FloorOpeningResultInfo>> Result { get; private set; }

    public void SetParameters(List<FloorOpeningCreationInfo> data)
    {
        OpeningData = data;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 30000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        var created = new List<FloorOpeningResultInfo>();
        var errors = new List<string>();

        try
        {
            for (var index = 0; index < OpeningData.Count; index++)
            {
                try
                {
                    created.Add(CreateOneOpening(OpeningData[index]));
                }
                catch (Exception ex)
                {
                    errors.Add($"[{index}] {ex.Message}");
                }
            }

            if (errors.Count > 0 && created.Count == 0)
            {
                Result = new AIResult<List<FloorOpeningResultInfo>>
                {
                    Success = false,
                    Message = string.Join("; ", errors),
                    Response = created
                };
            }
            else if (errors.Count > 0)
            {
                Result = new AIResult<List<FloorOpeningResultInfo>>
                {
                    Success = true,
                    Message =
                        $"Created {created.Count} opening(s) with warnings: {string.Join("; ", errors)}",
                    Response = created
                };
            }
            else
            {
                Result = new AIResult<List<FloorOpeningResultInfo>>
                {
                    Success = true,
                    Message = $"Created {created.Count} opening(s)",
                    Response = created
                };
            }
        }
        catch (Exception ex)
        {
            Result = new AIResult<List<FloorOpeningResultInfo>>
            {
                Success = false,
                Message = $"Create floor opening failed: {ex.Message}",
                Response = created
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private FloorOpeningResultInfo CreateOneOpening(FloorOpeningCreationInfo info)
    {
        var mode = NormalizeMode(info.Mode);
        var pointsMm = ResolveBoundaryPointsMm(info);
        var profile = BuildClosedProfile(pointsMm);
        var bbox = ComputePlanBBoxMm(pointsMm);

        Opening opening;
        using (var tx = new Transaction(_doc, "MCP Create Floor Opening"))
        {
            tx.Start();

            if (mode == "shaft")
            {
                if (info.BaseLevelId <= 0 || info.TopLevelId <= 0)
                {
                    throw new ArgumentException(
                        "mode=shaft requires baseLevelId and topLevelId.");
                }

                var baseLevel =
                    _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.BaseLevelId)) as Level;
                var topLevel =
                    _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.TopLevelId)) as Level;
                if (baseLevel == null)
                    throw new ArgumentException($"baseLevelId {info.BaseLevelId} is not a Level.");
                if (topLevel == null)
                    throw new ArgumentException($"topLevelId {info.TopLevelId} is not a Level.");
                if (topLevel.Elevation <= baseLevel.Elevation)
                    throw new ArgumentException("topLevel must be above baseLevel for shaft.");

                opening = _doc.Create.NewOpening(baseLevel, topLevel, profile);

                tx.Commit();

                return new FloorOpeningResultInfo
                {
                    ElementId = opening.Id.GetIntValue(),
                    UniqueId = opening.UniqueId,
                    Mode = mode,
                    BaseLevelId = baseLevel.Id.GetIntValue(),
                    TopLevelId = topLevel.Id.GetIntValue(),
                    BoundaryPointCount = pointsMm.Count,
                    WidthMm = Math.Round(bbox.WidthMm, 1),
                    DepthMm = Math.Round(bbox.DepthMm, 1)
                };
            }

            var host = ResolveHostFloor(info, pointsMm);
            opening = _doc.Create.NewOpening(host, profile, info.PerpendicularFace);
            tx.Commit();

            return new FloorOpeningResultInfo
            {
                ElementId = opening.Id.GetIntValue(),
                UniqueId = opening.UniqueId,
                Mode = mode,
                HostFloorId = host.Id.GetIntValue(),
                BoundaryPointCount = pointsMm.Count,
                WidthMm = Math.Round(bbox.WidthMm, 1),
                DepthMm = Math.Round(bbox.DepthMm, 1)
            };
        }
    }

    private static string NormalizeMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "floor";

        var m = mode.Trim().ToLowerInvariant();
        return m switch
        {
            "floor" or "slab" or "перекрытие" => "floor",
            "shaft" or "шахта" => "shaft",
            _ => throw new ArgumentException(
                $"Unknown mode '{mode}'. Use floor or shaft.")
        };
    }

    private static List<(double X, double Y)> ResolveBoundaryPointsMm(FloorOpeningCreationInfo info)
    {
        var hasBoundary = info.BoundaryPoints != null && info.BoundaryPoints.Count >= 3;
        var hasRect = info.Rect != null;

        if (hasBoundary && hasRect)
        {
            throw new ArgumentException(
                "Provide either boundaryPoints or rect, not both.");
        }

        if (!hasBoundary && !hasRect)
        {
            throw new ArgumentException(
                "Opening profile required: boundaryPoints (≥3) or rect (origin + widthMm + depthMm).");
        }

        if (hasRect)
            return RectToPointsMm(info.Rect);

        // Deduplicate consecutive duplicates; drop closing duplicate if present.
        var raw = new List<(double X, double Y)>();
        foreach (var p in info.BoundaryPoints)
        {
            if (p == null) continue;
            var pt = (p.X, p.Y);
            if (raw.Count > 0)
            {
                var last = raw[raw.Count - 1];
                if (Math.Abs(last.X - pt.X) < 0.5 && Math.Abs(last.Y - pt.Y) < 0.5)
                    continue;
            }
            raw.Add(pt);
        }

        if (raw.Count >= 2)
        {
            var first = raw[0];
            var last = raw[raw.Count - 1];
            if (Math.Abs(first.X - last.X) < 0.5 && Math.Abs(first.Y - last.Y) < 0.5)
                raw.RemoveAt(raw.Count - 1);
        }

        if (raw.Count < 3)
        {
            throw new ArgumentException(
                "boundaryPoints must define a closed polygon with at least 3 distinct corners (mm).");
        }

        return raw;
    }

    private static List<(double X, double Y)> RectToPointsMm(FloorOpeningRect rect)
    {
        if (rect == null || rect.Origin == null)
            throw new ArgumentException("rect.origin is required (x/y in mm).");
        if (rect.WidthMm <= 0 || rect.DepthMm <= 0)
            throw new ArgumentException("rect.widthMm and rect.depthMm must be > 0.");

        var ox = rect.Origin.X;
        var oy = rect.Origin.Y;
        var w = rect.WidthMm;
        var d = rect.DepthMm;
        var rad = rect.RotationDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        (double X, double Y) Rotate(double lx, double ly) =>
            (ox + lx * cos - ly * sin, oy + lx * sin + ly * cos);

        return new List<(double X, double Y)>
        {
            Rotate(0, 0),
            Rotate(w, 0),
            Rotate(w, d),
            Rotate(0, d)
        };
    }

    private static CurveArray BuildClosedProfile(List<(double X, double Y)> pointsMm)
    {
        var xyz = pointsMm
            .Select(p => new XYZ(p.X / 304.8, p.Y / 304.8, 0.0))
            .ToList();

        var profile = new CurveArray();
        for (var i = 0; i < xyz.Count; i++)
        {
            var a = xyz[i];
            var b = xyz[(i + 1) % xyz.Count];
            if (a.DistanceTo(b) < 1e-9)
            {
                throw new ArgumentException(
                    "Opening profile has a zero-length edge — check boundaryPoints/rect.");
            }
            profile.Append(Line.CreateBound(a, b));
        }

        return profile;
    }

    private Floor ResolveHostFloor(
        FloorOpeningCreationInfo info,
        List<(double X, double Y)> pointsMm)
    {
        if (info.HostFloorId > 0)
        {
            var el = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.HostFloorId));
            if (el is Floor floor)
                return floor;
            throw new ArgumentException(
                $"hostFloorId {info.HostFloorId} is not a Floor.");
        }

        if (info.LevelId <= 0)
        {
            throw new ArgumentException(
                "mode=floor requires hostFloorId or levelId to locate the slab.");
        }

        var level = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.LevelId)) as Level;
        if (level == null)
            throw new ArgumentException($"levelId {info.LevelId} is not a Level.");

        var cx = pointsMm.Average(p => p.X);
        var cy = pointsMm.Average(p => p.Y);
        var centroid = new XYZ(cx / 304.8, cy / 304.8, level.Elevation);

        var candidates = new FilteredElementCollector(_doc)
            .OfClass(typeof(Floor))
            .Cast<Floor>()
            .Where(f => FloorIsOnLevel(f, level))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new ArgumentException(
                $"No Floor found on levelId {info.LevelId} ('{level.Name}').");
        }

        Floor best = null;
        foreach (var floor in candidates)
        {
            var bb = floor.get_BoundingBox(null);
            if (bb == null) continue;
            if (centroid.X < bb.Min.X - 1e-6 || centroid.X > bb.Max.X + 1e-6) continue;
            if (centroid.Y < bb.Min.Y - 1e-6 || centroid.Y > bb.Max.Y + 1e-6) continue;
            best = floor;
            break;
        }

        if (best == null)
        {
            throw new ArgumentException(
                $"No Floor on level '{level.Name}' contains opening centroid " +
                $"({Math.Round(cx)}, {Math.Round(cy)}) mm. Pass hostFloorId explicitly.");
        }

        return best;
    }

    private static bool FloorIsOnLevel(Floor floor, Level level)
    {
        var p = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM)
                ?? floor.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
        if (p != null)
        {
            var id = p.AsElementId();
            if (id != null && id == level.Id)
                return true;
        }

#if REVIT2022_OR_GREATER
        // Floor.LevelId is reliable on newer hosts when the parameter is missing.
        try
        {
            if (floor.LevelId == level.Id)
                return true;
        }
        catch
        {
            // ignore
        }
#endif

        return false;
    }

    private static (double WidthMm, double DepthMm) ComputePlanBBoxMm(
        List<(double X, double Y)> pointsMm)
    {
        var minX = pointsMm.Min(p => p.X);
        var maxX = pointsMm.Max(p => p.X);
        var minY = pointsMm.Min(p => p.Y);
        var maxY = pointsMm.Max(p => p.Y);
        return (maxX - minX, maxY - minY);
    }

    public string GetName() => "CreateFloorOpening";
}
