using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Architecture;

/// <summary>
/// Creates railings by path or hosted on stairs (REV-83).
/// typeId must resolve to RailingType — no silent FirstOrDefault fallback.
/// </summary>
public class CreateRailingEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private static readonly Regex HeightFromName = new(
        @"h\s*[:=]?\s*(\d{3,4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private UIApplication _uiApp;
    private Document _doc => _uiApp.ActiveUIDocument.Document;

    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public List<RailingCreationInfo> RailingData { get; private set; }

    public AIResult<List<RailingResultInfo>> Result { get; private set; }

    public void SetParameters(List<RailingCreationInfo> data)
    {
        RailingData = data;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 30000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        var created = new List<RailingResultInfo>();
        var errors = new List<string>();

        try
        {
            for (var index = 0; index < RailingData.Count; index++)
            {
                var info = RailingData[index];
                try
                {
                    created.Add(CreateOneRailing(info, index));
                }
                catch (Exception ex)
                {
                    errors.Add($"[{index}] {ex.Message}");
                }
            }

            if (errors.Count > 0 && created.Count == 0)
            {
                Result = new AIResult<List<RailingResultInfo>>
                {
                    Success = false,
                    Message = string.Join("; ", errors),
                    Response = created
                };
            }
            else if (errors.Count > 0)
            {
                Result = new AIResult<List<RailingResultInfo>>
                {
                    Success = true,
                    Message = $"Created {created.Count} railing(s) with warnings: {string.Join("; ", errors)}",
                    Response = created
                };
            }
            else
            {
                Result = new AIResult<List<RailingResultInfo>>
                {
                    Success = true,
                    Message = $"Created {created.Count} railing(s)",
                    Response = created
                };
            }
        }
        catch (Exception ex)
        {
            Result = new AIResult<List<RailingResultInfo>>
            {
                Success = false,
                Message = $"Create railing failed: {ex.Message}",
                Response = created
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private RailingResultInfo CreateOneRailing(RailingCreationInfo info, int index)
    {
        if (info.TypeId <= 0)
        {
            throw new ArgumentException(
                "typeId is required. Call get_available_family_types (RailingType) and pass a valid typeId.");
        }

        var railingType = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.TypeId)) as RailingType;
        if (railingType == null)
        {
            throw new ArgumentException(
                $"typeId {info.TypeId} not found or is not a RailingType. Call get_available_family_types.");
        }

        var hostMode = info.HostElementId > 0;
        var pathMode = info.PathPoints != null && info.PathPoints.Count >= 2;

        if (hostMode && pathMode)
        {
            throw new ArgumentException(
                "Provide either hostElementId (stair) OR pathPoints+levelId, not both.");
        }

        if (!hostMode && !pathMode)
        {
            throw new ArgumentException(
                "Provide hostElementId (stair) or pathPoints (≥2) with levelId.");
        }

        List<ElementId> createdIds;

        using (var tx = new Transaction(_doc, "MCP Create Railing"))
        {
            tx.Start();

            if (hostMode)
            {
                createdIds = CreateHostedOnStairs(info, railingType);
            }
            else
            {
                createdIds = new List<ElementId> { CreateByPath(info, railingType) };
            }

            tx.Commit();
        }

        if (createdIds == null || createdIds.Count == 0)
        {
            throw new InvalidOperationException("Railing.Create returned no elements.");
        }

        // Return the first railing; hosted mode may create left+right.
        var primary = _doc.GetElement(createdIds.First()) as Railing;
        if (primary == null)
        {
            throw new InvalidOperationException("Created element is not a Railing.");
        }

        double? appliedHeight = TryParseHeightFromTypeName(railingType.Name);
        if (info.HeightMm > 0)
        {
            appliedHeight ??= info.HeightMm;
        }

        return new RailingResultInfo
        {
            ElementId = primary.Id.GetIntValue(),
            UniqueId = primary.UniqueId,
            TypeId = railingType.Id.GetIntValue(),
            TypeName = railingType.Name,
            HostElementId = hostMode ? info.HostElementId : -1,
            LevelId = hostMode ? -1 : info.LevelId,
            AppliedHeightMm = appliedHeight.HasValue ? Math.Round(appliedHeight.Value, 1) : null
        };
    }

    private List<ElementId> CreateHostedOnStairs(RailingCreationInfo info, RailingType railingType)
    {
        var host = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.HostElementId));
        if (host is not Stairs stairs)
        {
            throw new ArgumentException(
                $"hostElementId {info.HostElementId} is not a Stairs element.");
        }

        // Railing.Create(host) requires no existing associated railings.
        var existing = stairs.GetAssociatedRailings();
        if (existing != null && existing.Count > 0)
        {
            _doc.Delete(existing);
        }

        var ids = Railing.Create(
            _doc,
            stairs.Id,
            railingType.Id,
            RailingPlacementPosition.Treads);

        return ids?.ToList() ?? new List<ElementId>();
    }

    private ElementId CreateByPath(RailingCreationInfo info, RailingType railingType)
    {
        if (info.LevelId <= 0)
        {
            throw new ArgumentException("levelId is required for path mode.");
        }

        var level = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.LevelId)) as Level;
        if (level == null)
        {
            throw new ArgumentException($"levelId {info.LevelId} is not a Level.");
        }

        var loop = new CurveLoop();
        for (var i = 0; i < info.PathPoints.Count - 1; i++)
        {
            var a = JZPoint.ToXYZ(info.PathPoints[i]);
            var b = JZPoint.ToXYZ(info.PathPoints[i + 1]);
            // Keep path on level plane (Z from points ignored for consistency).
            a = new XYZ(a.X, a.Y, 0);
            b = new XYZ(b.X, b.Y, 0);
            if (a.DistanceTo(b) < 1e-6)
            {
                continue;
            }

            loop.Append(Line.CreateBound(a, b));
        }

        if (info.IsClosedLoop && info.PathPoints.Count >= 3)
        {
            var first = JZPoint.ToXYZ(info.PathPoints[0]);
            var last = JZPoint.ToXYZ(info.PathPoints[info.PathPoints.Count - 1]);
            first = new XYZ(first.X, first.Y, 0);
            last = new XYZ(last.X, last.Y, 0);
            if (first.DistanceTo(last) > 1e-6)
            {
                loop.Append(Line.CreateBound(last, first));
            }
        }

        if (!loop.Any())
        {
            throw new ArgumentException("pathPoints did not produce a valid CurveLoop.");
        }

        var railing = Railing.Create(_doc, loop, railingType.Id, level.Id);
        if (info.LevelOffsetMm != 0)
        {
            var baseOffset = railing.LookupParameter("Base Offset")
                             ?? railing.LookupParameter("Смещение снизу")
                             ?? railing.LookupParameter("Offset from Base");
            if (baseOffset != null && !baseOffset.IsReadOnly)
            {
                baseOffset.Set(RevitUnitConversion.FromMillimeters(info.LevelOffsetMm));
            }
        }

        return railing.Id;
    }

    private static double? TryParseHeightFromTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        var m = HeightFromName.Match(typeName);
        if (!m.Success) return null;
        if (double.TryParse(m.Groups[1].Value, out var h) && h >= 700 && h <= 2000)
        {
            return h;
        }

        return null;
    }

    public string GetName() => "CreateRailing";
}
