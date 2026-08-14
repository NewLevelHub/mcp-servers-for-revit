using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.Detailing;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Detailing;

public class CreateDetailLinesResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("createdCount")]
    public int CreatedCount { get; set; }

    [JsonProperty("detailLineIds")]
    public List<long> DetailLineIds { get; set; } = new List<long>();

    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    /// <summary>Filled in when a requested line style could not be resolved.</summary>
    [JsonProperty("availableLineStyles")]
    public List<string> AvailableLineStyles { get; set; } = new List<string>();

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>
///     Draws polylines as detail curves on a plan, detail callout, or drafting view (coordinates mm).
/// </summary>
public class CreateDetailLinesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private DetailLinesCreationInfo _info;

    public CreateDetailLinesResult ResultInfo { get; private set; } = new CreateDetailLinesResult();
    public bool TaskCompleted { get; private set; }
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public void SetParameters(DetailLinesCreationInfo info)
    {
        _info = info ?? new DetailLinesCreationInfo();
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
            var doc = app.ActiveUIDocument.Document;
            ResultInfo = Create(doc, app.ActiveUIDocument, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new CreateDetailLinesResult
            {
                Success = false,
                Message = $"Error creating detail lines: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Create Detail Lines";

    public static CreateDetailLinesResult Create(
        Document doc,
        UIDocument uiDoc,
        DetailLinesCreationInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));

        info ??= new DetailLinesCreationInfo();
        var warnings = new List<string>();
        var createdIds = new List<long>();

        var view = ResolveView(doc, uiDoc, info)
            ?? throw new InvalidOperationException(
                "Target view was not found. Provide viewId, viewUniqueId, or viewName, " +
                "or open a floor plan, detail callout, or drafting view.");

        if (!DetailDrawing.SupportsDetailing(view))
        {
            throw new InvalidOperationException(
                $"View '{view.Name}' ({view.ViewType}) does not support detail curves. " +
                "Use a floor plan, detail callout, or drafting view.");
        }

        var polylines = info.Polylines ?? new List<DetailPolylineInfo>();
        var arcs = info.Arcs ?? new List<DetailArcInfo>();
        if (polylines.Count == 0 && arcs.Count == 0)
            warnings.Add("No polylines or arcs provided.");

        double z = DetailDrawing.ViewPlaneZ(view);
        var unresolvedStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var tx = new Transaction(doc, "Create Detail Lines"))
        {
            tx.Start();

            var defaultStyle = ResolveStyle(doc, info.LineStyleName, unresolvedStyles);

            foreach (var polyline in polylines)
            {
                var points = polyline?.Points;
                if (points == null || points.Count < 2)
                {
                    warnings.Add("Polyline with fewer than 2 points skipped.");
                    continue;
                }

                var style = ResolveStyle(doc, polyline.LineStyleName, unresolvedStyles) ?? defaultStyle;

                var viewPoints = points
                    .Select(point => DetailDrawing.ToViewPoint(point.X, point.Y, z))
                    .ToList();

                foreach (var curve in DetailDrawing.DrawPolyline(doc, view, viewPoints, polyline.Closed))
                {
                    createdIds.Add(curve.Id.GetValue());
                    ApplyStyle(curve, style, warnings);
                }
            }

            foreach (var arc in arcs)
            {
                if (arc?.Start == null || arc.End == null || arc.PointOnArc == null)
                {
                    warnings.Add("Arc without start, end, or pointOnArc skipped.");
                    continue;
                }

                try
                {
                    var curve = DetailDrawing.DrawArc(
                        doc,
                        view,
                        DetailDrawing.ToViewPoint(arc.Start.X, arc.Start.Y, z),
                        DetailDrawing.ToViewPoint(arc.End.X, arc.End.Y, z),
                        DetailDrawing.ToViewPoint(arc.PointOnArc.X, arc.PointOnArc.Y, z));

                    createdIds.Add(curve.Id.GetValue());
                    ApplyStyle(curve, ResolveStyle(doc, arc.LineStyleName, unresolvedStyles) ?? defaultStyle, warnings);
                }
                catch (Exception ex)
                {
                    // Collinear three points cannot form an arc — Revit throws rather than degrade to a line.
                    warnings.Add($"Arc skipped: {ex.Message}");
                }
            }

            tx.Commit();
        }

        var requestedCurves = polylines.Count + arcs.Count;
        var result = new CreateDetailLinesResult
        {
            // Asked for curves and drew none: the warnings say why, and calling that a
            // success let the model move on as if the detail had been drawn.
            Success = requestedCurves == 0 || createdIds.Count > 0,
            Message = $"Created {createdIds.Count} detail curves on view '{view.Name}'.",
            CreatedCount = createdIds.Count,
            DetailLineIds = createdIds,
            ViewId = view.Id.GetValue(),
            ViewName = view.Name,
            Warnings = warnings
        };

        if (unresolvedStyles.Count > 0)
        {
            foreach (var name in unresolvedStyles)
                warnings.Add($"Line style '{name}' was not found; the view default was used.");

            result.AvailableLineStyles = DetailDrawing.CollectLineStyleNames(doc);
        }

        return result;
    }

    private static GraphicsStyle ResolveStyle(Document doc, string name, HashSet<string> unresolved)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var style = DetailDrawing.ResolveLineStyle(doc, name);
        if (style == null)
            unresolved.Add(name.Trim());

        return style;
    }

    private static void ApplyStyle(CurveElement curve, GraphicsStyle style, List<string> warnings)
    {
        if (style == null)
            return;

        if (!DetailDrawing.TryApplyLineStyle(curve, style, out var error) && error != null)
            warnings.Add($"Could not apply line style '{style.Name}': {error}");
    }

    private static View ResolveView(Document doc, UIDocument uiDoc, DetailLinesCreationInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ViewUniqueId))
        {
            if (doc.GetElement(info.ViewUniqueId.Trim()) is View byUniqueId && !byUniqueId.IsTemplate)
                return byUniqueId;
        }

        if (info.ViewId > 0)
        {
            if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.ViewId)) is View byId && !byId.IsTemplate)
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
