using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Detailing;

public class CreateDetailViewEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double MmPerFoot = 304.8;

    private DetailViewCreationInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public DetailViewCreationResult ResultInfo { get; private set; } = new DetailViewCreationResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(DetailViewCreationInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            ResultInfo = Create(doc, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new DetailViewCreationResult
            {
                Success = false,
                Message = $"Error creating detail view: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Create Detail View";

    public static DetailViewCreationResult Create(Document doc, DetailViewCreationInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));
        if (info == null)
            throw new ArgumentNullException(nameof(info));

        var warnings = new List<string>();
        var mode = (info.Mode ?? "callout").Trim().ToLowerInvariant();
        View created;

        using (var tx = new Transaction(doc, "Create Detail View"))
        {
            tx.Start();

            created = mode switch
            {
                "drafting" => CreateDraftingView(doc),
                "callout" => CreateCalloutView(doc, info, warnings),
                _ => throw new ArgumentException($"Unknown mode '{info.Mode}'. Use 'callout' or 'drafting'.")
            };

            ApplyName(doc, created, info.Name, mode, warnings);
            ApplyScale(created, info.Scale, warnings);
            ApplyDetailLevel(created, info.DetailLevel, warnings);

            tx.Commit();
        }

        return new DetailViewCreationResult
        {
            Success = true,
            Message = $"Successfully created {mode} view '{created.Name}' at 1:{created.Scale}.",
            ViewId = created.Id.GetValue(),
            ViewUniqueId = created.UniqueId,
            ViewName = created.Name,
            Mode = mode,
            Scale = created.Scale,
            Warnings = warnings
        };
    }

    private static View CreateDraftingView(Document doc)
    {
        var draftingType = FindViewFamilyType(doc, ViewFamily.Drafting)
            ?? throw new InvalidOperationException("The project has no drafting view type.");

        return ViewDrafting.Create(doc, draftingType.Id);
    }

    private static View CreateCalloutView(
        Document doc,
        DetailViewCreationInfo info,
        List<string> warnings)
    {
        var parentView = ResolveParentView(doc, info)
            ?? throw new ArgumentException(
                "Parent view was not found. Provide parentViewId, parentViewUniqueId, or parentViewName.");

        var detailType = FindViewFamilyType(doc, ViewFamily.Detail)
            ?? throw new InvalidOperationException("The project has no detail view type for callouts.");

        var (min, max) = ResolveCalloutArea(doc, parentView, info, warnings);

        return ViewSection.CreateCallout(doc, parentView.Id, detailType.Id, min, max);
    }

    private static (XYZ Min, XYZ Max) ResolveCalloutArea(
        Document doc,
        View parentView,
        DetailViewCreationInfo info,
        List<string> warnings)
    {
        if (info.ElementId > 0)
        {
            var element = doc.GetElement(ElementIdExtensions.FromLong(info.ElementId))
                ?? throw new ArgumentException($"Element with id {info.ElementId} was not found.");

            var bbox = element.get_BoundingBox(parentView) ?? element.get_BoundingBox(null)
                ?? throw new InvalidOperationException(
                    $"Element {info.ElementId} has no bounding box to build the callout area from.");

            var padding = MmToFeet(info.Padding > 0 ? info.Padding : 300);
            return (
                new XYZ(bbox.Min.X - padding, bbox.Min.Y - padding, bbox.Min.Z),
                new XYZ(bbox.Max.X + padding, bbox.Max.Y + padding, bbox.Max.Z));
        }

        if (info.AreaMin != null && info.AreaMax != null)
        {
            var p0 = JZPoint.ToXYZ(info.AreaMin);
            var p1 = JZPoint.ToXYZ(info.AreaMax);
            return (
                new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Min(p0.Z, p1.Z)),
                new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), Math.Max(p0.Z, p1.Z)));
        }

        throw new ArgumentException(
            "Callout area is required: provide elementId (with optional padding) or areaMin/areaMax in mm.");
    }

    private static View ResolveParentView(Document doc, DetailViewCreationInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ParentViewUniqueId))
        {
            if (doc.GetElement(info.ParentViewUniqueId.Trim()) is View byUniqueId && !byUniqueId.IsTemplate)
                return byUniqueId;
        }

        if (info.ParentViewId > 0)
        {
            if (doc.GetElement(ElementIdExtensions.FromLong(info.ParentViewId)) is View byId && !byId.IsTemplate)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(info.ParentViewName))
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && !(view is ViewSheet))
                .FirstOrDefault(view =>
                    view.Name.Equals(info.ParentViewName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static ViewFamilyType FindViewFamilyType(Document doc, ViewFamily viewFamily)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == viewFamily);
    }

    private static void ApplyName(
        Document doc,
        View view,
        string requestedName,
        string mode,
        List<string> warnings)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName)
            ? $"Узел ({mode})"
            : requestedName.Trim();

        var name = baseName;
        var suffix = 1;
        while (ViewNameExists(doc, view, name))
            name = $"{baseName} ({suffix++})";

        try
        {
            view.Name = name;
            if (!name.Equals(baseName, StringComparison.Ordinal))
                warnings.Add($"View name '{baseName}' is taken; '{name}' is used.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to rename view: {ex.Message}");
        }
    }

    private static bool ViewNameExists(Document doc, View ownView, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Any(view => view.Id != ownView.Id &&
                         view.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyScale(View view, int scale, List<string> warnings)
    {
        if (scale <= 0)
            return;

        try
        {
            view.Scale = scale;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to set view scale 1:{scale}: {ex.Message}");
        }
    }

    private static void ApplyDetailLevel(View view, string detailLevel, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(detailLevel))
            return;

        try
        {
            view.DetailLevel = detailLevel.Trim().ToLowerInvariant() switch
            {
                "coarse" => ViewDetailLevel.Coarse,
                "medium" => ViewDetailLevel.Medium,
                _ => ViewDetailLevel.Fine
            };
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to set detail level '{detailLevel}': {ex.Message}");
        }
    }

    private static double MmToFeet(double millimeters) => millimeters / MmPerFoot;
}
