using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Utils;
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

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>
///     Draws polylines as detail curves on a plan, detail callout, or drafting view (coordinates mm).
/// </summary>
public class CreateDetailLinesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double MinSegmentLengthMm = 1.0;

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

        if (!(view is ViewPlan || view is ViewDrafting ||
              view.ViewType == ViewType.Detail || view.ViewType == ViewType.DraftingView))
        {
            throw new InvalidOperationException(
                $"View '{view.Name}' ({view.ViewType}) does not support detail curves. " +
                "Use a floor plan, detail callout, or drafting view.");
        }

        var polylines = info.Polylines ?? new List<DetailPolylineInfo>();
        if (polylines.Count == 0)
            warnings.Add("No polylines provided.");

        double z = view is ViewPlan plan && plan.GenLevel != null
            ? plan.GenLevel.Elevation
            : 0;

        using (var tx = new Transaction(doc, "Create Detail Lines"))
        {
            tx.Start();

            foreach (var polyline in polylines)
            {
                var points = polyline?.Points;
                if (points == null || points.Count < 2)
                {
                    warnings.Add("Polyline with fewer than 2 points skipped.");
                    continue;
                }

                for (var i = 0; i < points.Count - 1; i++)
                {
                    var start = new XYZ(
                        RevitUnitConversion.FromMillimeters(points[i].X),
                        RevitUnitConversion.FromMillimeters(points[i].Y),
                        z);
                    var end = new XYZ(
                        RevitUnitConversion.FromMillimeters(points[i + 1].X),
                        RevitUnitConversion.FromMillimeters(points[i + 1].Y),
                        z);

                    if (start.DistanceTo(end) < RevitUnitConversion.FromMillimeters(MinSegmentLengthMm))
                        continue;

                    var line = doc.Create.NewDetailCurve(view, Line.CreateBound(start, end));
                    createdIds.Add(line.Id.GetValue());
                }
            }

            tx.Commit();
        }

        return new CreateDetailLinesResult
        {
            Success = true,
            Message = $"Created {createdIds.Count} detail line segments on view '{view.Name}'.",
            CreatedCount = createdIds.Count,
            DetailLineIds = createdIds,
            ViewId = view.Id.GetValue(),
            ViewName = view.Name,
            Warnings = warnings
        };
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
