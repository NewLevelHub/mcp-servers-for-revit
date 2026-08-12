using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.Detailing;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Detailing;

/// <summary>
///     Hatches arbitrary contours on a drafting, detail or plan view.
///     <para>
///     Deliberately separate from create_filled_regions, which paints room boundaries on a plan for
///     norm audits and is wired into that playbook by name. A node needs a hatch over coordinates,
///     not over a Room, and mixing the two would break routing for both.
///     </para>
/// </summary>
public class CreateDetailRegionsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public const string DefaultCommentTag = "MCP-DR";

    private DetailRegionsCreationInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public DetailRegionsCreationResult ResultInfo { get; private set; } = new DetailRegionsCreationResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(DetailRegionsCreationInfo info)
    {
        _info = info ?? new DetailRegionsCreationInfo();
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 60000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            ResultInfo = Create(app.ActiveUIDocument.Document, app.ActiveUIDocument, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new DetailRegionsCreationResult
            {
                Success = false,
                Message = $"Error creating detail regions: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Create Detail Regions";

    public static DetailRegionsCreationResult Create(
        Document doc,
        UIDocument uiDoc,
        DetailRegionsCreationInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));

        info ??= new DetailRegionsCreationInfo();

        var view = ResolveView(doc, uiDoc, info)
            ?? throw new InvalidOperationException(
                "Target view was not found. Provide viewId, viewUniqueId, or viewName, " +
                "or open a drafting, detail, or floor plan view.");

        if (!DetailDrawing.SupportsDetailing(view))
        {
            throw new InvalidOperationException(
                $"View '{view.Name}' ({view.ViewType}) does not support filled regions. " +
                "Use a drafting, detail callout, section, or floor plan view.");
        }

        var commentTag = string.IsNullOrWhiteSpace(info.CommentTag)
            ? DefaultCommentTag
            : info.CommentTag.Trim();

        var result = new DetailRegionsCreationResult
        {
            ViewId = view.Id.GetValue(),
            ViewName = view.Name,
            CommentTag = commentTag
        };

        if (info.ClearOnly)
        {
            using (var tx = new Transaction(doc, "MCP Clear Detail Regions"))
            {
                tx.Start();
                result.DeletedPreviousCount = ClearPrevious(doc, view, commentTag);
                tx.Commit();
            }

            result.Success = true;
            result.Message = $"Deleted {result.DeletedPreviousCount} previous detail regions on '{view.Name}'.";
            return result;
        }

        var regions = info.Regions ?? new List<DetailRegionInfo>();
        if (regions.Count == 0)
            throw new ArgumentException("At least one region with a points contour is required.");

        double z = DetailDrawing.ViewPlaneZ(view);

        using (var tx = new Transaction(doc, "MCP Create Detail Regions"))
        {
            tx.Start();

            if (info.ClearPrevious)
                result.DeletedPreviousCount = ClearPrevious(doc, view, commentTag);

            for (var i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                var label = string.IsNullOrWhiteSpace(region?.Label) ? $"region {i + 1}" : region.Label.Trim();

                try
                {
                    var regionType = ResolveType(doc, region, info, result);
                    if (regionType == null)
                    {
                        result.Warnings.Add(
                            $"{label}: no filled region type resolved " +
                            $"(filledRegionTypeName='{region?.FilledRegionTypeName}', fillPatternName='{region?.FillPatternName}').");
                        continue;
                    }

                    var loops = BuildLoops(region, z);
                    var filledRegion = DetailDrawing.FillContour(doc, view, loops, regionType.Id);
                    TagRegion(filledRegion, commentTag, region.Label);

                    result.Created.Add(new DetailRegionCreatedItem
                    {
                        RegionId = filledRegion.Id.GetValue(),
                        Label = region.Label ?? string.Empty,
                        FilledRegionType = regionType.Name,
                        Holes = loops.Count - 1
                    });
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{label}: {ex.Message}");
                }
            }

            tx.Commit();
        }

        result.CreatedCount = result.Created.Count;
        result.Success = result.CreatedCount > 0;
        result.Message = result.Success
            ? $"Created {result.CreatedCount} of {regions.Count} detail regions on '{view.Name}'."
            : $"No detail regions were created on '{view.Name}'.";

        if (!result.Success || result.Warnings.Count > 0)
        {
            result.AvailableTypes = FilledRegionTypes.ListTypeNames(doc);
            result.AvailablePatterns = FilledRegionTypes.ListPatternNames(doc);
        }

        return result;
    }

    /// <summary>
    ///     Type by explicit name, else by hatch pattern (creating a type when the project has none),
    ///     else the call-level fallback name.
    /// </summary>
    private static FilledRegionType ResolveType(
        Document doc,
        DetailRegionInfo region,
        DetailRegionsCreationInfo info,
        DetailRegionsCreationResult result)
    {
        var byName = FilledRegionTypes.FindByName(doc, region?.FilledRegionTypeName);
        if (byName != null)
            return byName;

        if (!string.IsNullOrWhiteSpace(region?.FillPatternName))
        {
            var pattern = FilledRegionTypes.FindPattern(doc, region.FillPatternName);
            if (pattern == null)
            {
                result.Warnings.Add($"Fill pattern '{region.FillPatternName}' was not found in the project.");
            }
            else
            {
                var existing = FilledRegionTypes.FindByPattern(doc, pattern.Id);
                if (existing != null)
                    return existing;

                if (!info.CreateMissingTypes)
                {
                    result.Warnings.Add(
                        $"No filled region type draws with '{pattern.Name}' and createMissingTypes is false.");
                }
                else
                {
                    var ensured = FilledRegionTypes.EnsureForPattern(doc, pattern, null, out var created);
                    if (ensured != null)
                    {
                        if (created)
                            result.CreatedTypes.Add(ensured.Name);
                        return ensured;
                    }
                }
            }
        }

        return FilledRegionTypes.FindByName(doc, info.FilledRegionTypeName);
    }

    private static List<CurveLoop> BuildLoops(DetailRegionInfo region, double z)
    {
        if (region?.Points == null || region.Points.Count < 3)
            throw new ArgumentException("A region contour needs at least 3 points.");

        var loops = new List<CurveLoop>
        {
            DetailDrawing.BuildClosedLoop(ToViewPoints(region.Points, z))
        };

        foreach (var hole in region.Holes ?? new List<List<DetailLinePoint>>())
        {
            if (hole == null || hole.Count < 3)
                continue;

            loops.Add(DetailDrawing.BuildClosedLoop(ToViewPoints(hole, z)));
        }

        return loops;
    }

    private static List<XYZ> ToViewPoints(IEnumerable<DetailLinePoint> points, double z)
    {
        return points
            .Where(point => point != null)
            .Select(point => DetailDrawing.ToViewPoint(point.X, point.Y, z))
            .ToList();
    }

    private static void TagRegion(FilledRegion region, string commentTag, string label)
    {
        var comments = region.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments == null || comments.IsReadOnly)
            return;

        var value = string.IsNullOrWhiteSpace(label) ? commentTag : $"{commentTag} {label.Trim()}";
        comments.Set(value);
    }

    private static int ClearPrevious(Document doc, View view, string commentTag)
    {
        var ids = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(FilledRegion))
            .Cast<FilledRegion>()
            .Where(region =>
            {
                var comments = region.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
                               ?? string.Empty;
                return comments.StartsWith(commentTag, StringComparison.OrdinalIgnoreCase);
            })
            .Select(region => region.Id)
            .ToList();

        if (ids.Count == 0)
            return 0;

        doc.Delete(ids);
        return ids.Count;
    }

    private static View ResolveView(Document doc, UIDocument uiDoc, DetailRegionsCreationInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ViewUniqueId))
        {
            if (doc.GetElement(info.ViewUniqueId.Trim()) is View byUniqueId && !byUniqueId.IsTemplate)
                return byUniqueId;
        }

        if (info.ViewId > 0)
        {
            if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.ViewId)) is View byId &&
                !byId.IsTemplate)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(info.ViewName))
        {
            var byName = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && view is not ViewSheet)
                .FirstOrDefault(view =>
                    view.Name.Equals(info.ViewName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (byName != null)
                return byName;
        }

        return uiDoc?.ActiveView is { IsTemplate: false } activeView ? activeView : null;
    }
}
