using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Detailing
{
    public class DetailPolylineInfo
    {
        [JsonProperty("points")]
        public List<DetailLinePoint> Points { get; set; } = new List<DetailLinePoint>();
    }

    public class DetailLinePoint
    {
        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }
    }

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

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Draws polylines as detail curves on the active plan view (coordinates mm).
    /// Used by check_evacuation_distance to visualize traced escape routes.
    /// </summary>
    public class CreateDetailLinesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private const double MinSegmentLengthMm = 1.0;

        private List<DetailPolylineInfo> _polylines;

        public CreateDetailLinesResult ResultInfo { get; private set; } = new CreateDetailLinesResult();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(List<DetailPolylineInfo> polylines)
        {
            _polylines = polylines ?? new List<DetailPolylineInfo>();
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 60000)
        {
            // Do not Reset here — SetParameters already Reset; resetting after a fast
            // Execute can clear the signal and hang until timeout.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            var warnings = new List<string>();
            var createdIds = new List<long>();

            try
            {
                var doc = app.ActiveUIDocument.Document;
                var view = app.ActiveUIDocument.ActiveView
                    ?? throw new InvalidOperationException("No active view.");

                if (!(view is ViewPlan || view is ViewDrafting ||
                      view.ViewType == ViewType.Detail || view.ViewType == ViewType.DraftingView))
                    throw new InvalidOperationException(
                        $"Active view '{view.Name}' ({view.ViewType}) does not support detail curves. " +
                        "Open a floor plan, detail callout, or drafting view.");

                // Detail curves must lie in the view plane.
                // Plans: elevation of associated level; drafting/detail: sketch plane origin (z=0).
                double z = view is ViewPlan plan && plan.GenLevel != null
                    ? plan.GenLevel.Elevation
                    : 0;

                using (var tx = new Transaction(doc, "Create Detail Lines"))
                {
                    tx.Start();

                    foreach (var polyline in _polylines)
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

                ResultInfo = new CreateDetailLinesResult
                {
                    Success = true,
                    Message = $"Created {createdIds.Count} detail line segments on view '{view.Name}'.",
                    CreatedCount = createdIds.Count,
                    DetailLineIds = createdIds,
                    Warnings = warnings
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new CreateDetailLinesResult
                {
                    Success = false,
                    Message = $"Error creating detail lines: {ex.Message}",
                    CreatedCount = createdIds.Count,
                    DetailLineIds = createdIds,
                    Warnings = warnings
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Create Detail Lines";
    }
}
