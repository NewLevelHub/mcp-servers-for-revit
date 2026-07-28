using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Normatives;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Normatives
{
    public class CheckEvacuationWidthEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string ModeReport = "report";
        public const string ModeHighlight = "highlight";

        public const string HighlightTargetViolations = "violations";
        public const string HighlightTargetCompliant = "compliant";
        public const string HighlightTargetBoth = "both";

        private double? _minWidthMm;
        private string _mode = ModeReport;
        private string _levelNameFilter = string.Empty;
        private long? _levelIdFilter;
        private long? _viewIdFilter;
        private bool _filterByActiveView = true;
        private LevelScopeHelper.Scope _scope;
        private string _roomNameFilter = string.Empty;
        private bool _includeCompliant;
        private bool _corridorOnly = true;
        private string _highlightTarget = HighlightTargetViolations;
        private int[] _highlightColor = { 255, 0, 0 };
        private int[] _compliantHighlightColor = { 0, 180, 0 };

        public CheckEvacuationWidthResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            double? minWidthMm,
            string mode = ModeReport,
            string levelName = "",
            string roomNameFilter = "",
            bool includeCompliant = false,
            bool corridorOnly = true,
            int[] highlightColor = null,
            string highlightTarget = HighlightTargetViolations,
            int[] compliantHighlightColor = null,
            long? levelId = null,
            long? viewId = null,
            bool filterByActiveView = true)
        {
            _minWidthMm = minWidthMm;
            _mode = string.Equals(mode, ModeHighlight, StringComparison.OrdinalIgnoreCase)
                ? ModeHighlight
                : ModeReport;
            _levelNameFilter = levelName ?? string.Empty;
            _levelIdFilter = levelId;
            _viewIdFilter = viewId;
            _filterByActiveView = filterByActiveView;
            _roomNameFilter = roomNameFilter ?? string.Empty;
            _includeCompliant = includeCompliant;
            _corridorOnly = corridorOnly;
            _highlightTarget = NormalizeHighlightTarget(highlightTarget);
            if (highlightColor is { Length: 3 })
            {
                _highlightColor = highlightColor;
            }
            if (compliantHighlightColor is { Length: 3 })
            {
                _compliantHighlightColor = compliantHighlightColor;
            }
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        private static string NormalizeHighlightTarget(string target)
        {
            if (string.Equals(target, HighlightTargetCompliant, StringComparison.OrdinalIgnoreCase))
                return HighlightTargetCompliant;
            if (string.Equals(target, HighlightTargetBoth, StringComparison.OrdinalIgnoreCase))
                return HighlightTargetBoth;
            return HighlightTargetViolations;
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                if (_minWidthMm == null)
                {
                    ResultInfo = new CheckEvacuationWidthResult
                    {
                        Success = false,
                        Message = "minWidthMm must be provided."
                    };
                    return;
                }

                var doc = app.ActiveUIDocument.Document;
                var activeView = app.ActiveUIDocument.ActiveView;
                _scope = LevelScopeHelper.BuildScope(
                    doc, activeView, _levelNameFilter, _levelIdFilter, _viewIdFilter, _filterByActiveView);

                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(room => room.Area > 0);

                var violations = new List<EvacuationWidthCheckItem>();
                var compliant = new List<EvacuationWidthCheckItem>();
                var violationElementIds = new List<ElementId>();
                var compliantElementIds = new List<ElementId>();
                int checkedCount = 0;

                foreach (var room in rooms)
                {
                    if (!LevelScopeHelper.RoomInScope(room, _scope))
                        continue;

                    var levelName = room.Level?.Name ?? string.Empty;
                    var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
                    var roomPurpose = CorridorClassifier.ReadRoomPurpose(room);
                    if (!string.IsNullOrWhiteSpace(_roomNameFilter) &&
                        roomName.IndexOf(_roomNameFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        roomPurpose.IndexOf(_roomNameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (_corridorOnly && !CorridorClassifier.IsEvacuationCorridor(roomName, roomPurpose))
                    {
                        continue;
                    }

                    checkedCount++;

                    var (widthMm, depthMm) = RoomFootprintCalculator.Calculate(room);
                    bool isCompliant = IsWidthCompliant(widthMm, _minWidthMm.Value);

                    var item = new EvacuationWidthCheckItem
                    {
                        Id = room.Id.GetValue(),
                        UniqueId = room.UniqueId,
                        Name = roomName,
                        Number = room.Number ?? string.Empty,
                        Level = levelName,
                        RoomPurpose = roomPurpose,
                        ActualWidthMm = widthMm,
                        DepthMm = depthMm,
                        AreaM2 = RevitUnitConversion.ToSquareMeters(room.Area),
                        RequiredWidthMm = _minWidthMm.Value,
                        IsCompliant = isCompliant,
                        DeviationMm = CalculateDeviationMm(widthMm, _minWidthMm.Value)
                    };

                    if (isCompliant)
                    {
                        compliant.Add(item);
                        compliantElementIds.Add(room.Id);
                    }
                    else
                    {
                        violations.Add(item);
                        violationElementIds.Add(room.Id);
                    }
                }

                int highlightedCount = 0;
                if (_mode == ModeHighlight)
                {
                    highlightedCount = HighlightByTarget(
                        app,
                        doc,
                        violationElementIds,
                        compliantElementIds);
                }

                ResultInfo = new CheckEvacuationWidthResult
                {
                    Success = true,
                    Message = $"Checked {checkedCount} corridors: {violations.Count} violate the width requirement.",
                    Mode = _mode,
                    MinWidthMm = _minWidthMm,
                    CorridorOnly = _corridorOnly,
                    TotalCorridorsChecked = checkedCount,
                    ViolationCount = violations.Count,
                    Violations = violations,
                    CompliantCorridors = _includeCompliant ? compliant : new List<EvacuationWidthCheckItem>(),
                    HighlightedCount = highlightedCount
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new CheckEvacuationWidthResult
                {
                    Success = false,
                    Message = $"Failed to check evacuation corridor width: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Check Evacuation Width";

        public static bool IsWidthCompliant(double actualWidthMm, double minWidthMm)
        {
            return actualWidthMm >= minWidthMm;
        }

        public static double CalculateDeviationMm(double actualWidthMm, double minWidthMm)
        {
            if (actualWidthMm < minWidthMm)
                return minWidthMm - actualWidthMm;
            return 0;
        }

        private int HighlightByTarget(
            UIApplication app,
            Document doc,
            List<ElementId> violationIds,
            List<ElementId> compliantIds)
        {
            var uidoc = app.ActiveUIDocument;
            var activeView = uidoc.ActiveView;
            var selectIds = new List<ElementId>();
            int highlighted = 0;

            using var transaction = new Transaction(doc, "Check Evacuation Width Highlight");
            transaction.Start();

            bool paintViolations = _highlightTarget is HighlightTargetViolations or HighlightTargetBoth;
            bool paintCompliant = _highlightTarget is HighlightTargetCompliant or HighlightTargetBoth;

            if (paintViolations && violationIds.Count > 0)
            {
                highlighted += ElementGraphicOverrides.HighlightRoomsAndTags(
                    activeView,
                    doc,
                    violationIds,
                    _highlightColor);
                selectIds.AddRange(violationIds);
            }

            if (paintCompliant && compliantIds.Count > 0)
            {
                highlighted += ElementGraphicOverrides.HighlightRoomsAndTags(
                    activeView,
                    doc,
                    compliantIds,
                    _compliantHighlightColor);
                selectIds.AddRange(compliantIds);
            }

            if (selectIds.Count > 0)
            {
                uidoc.Selection.SetElementIds(selectIds);
                uidoc.ShowElements(selectIds);
            }

            transaction.Commit();
            return highlighted;
        }
    }
}
