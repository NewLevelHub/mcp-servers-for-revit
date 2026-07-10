using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Normatives;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Normatives
{
    public class CheckRoomDepthEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string ModeReport = "report";
        public const string ModeHighlight = "highlight";

        private double? _minDepthMm;
        private double? _maxDepthMm;
        private string _mode = ModeReport;
        private string _levelNameFilter = string.Empty;
        private string _roomNameFilter = string.Empty;
        private bool _includeCompliant;
        private int[] _highlightColor = { 255, 0, 0 };

        public CheckRoomDepthResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            double? minDepthMm,
            double? maxDepthMm,
            string mode = ModeReport,
            string levelName = "",
            string roomNameFilter = "",
            bool includeCompliant = false,
            int[] highlightColor = null)
        {
            _minDepthMm = minDepthMm;
            _maxDepthMm = maxDepthMm;
            _mode = string.Equals(mode, ModeHighlight, StringComparison.OrdinalIgnoreCase)
                ? ModeHighlight
                : ModeReport;
            _levelNameFilter = levelName ?? string.Empty;
            _roomNameFilter = roomNameFilter ?? string.Empty;
            _includeCompliant = includeCompliant;
            if (highlightColor is { Length: 3 })
            {
                _highlightColor = highlightColor;
            }
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
                if (_minDepthMm == null && _maxDepthMm == null)
                {
                    ResultInfo = new CheckRoomDepthResult
                    {
                        Success = false,
                        Message = "Either minDepthMm or maxDepthMm must be provided."
                    };
                    return;
                }

                var doc = app.ActiveUIDocument.Document;
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(room => room.Area > 0);

                var violations = new List<RoomDepthCheckItem>();
                var compliant = new List<RoomDepthCheckItem>();
                var violationElementIds = new List<ElementId>();
                int checkedCount = 0;

                foreach (var room in rooms)
                {
                    var levelName = room.Level?.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(_levelNameFilter) &&
                        !string.Equals(levelName, _levelNameFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(_roomNameFilter) &&
                        roomName.IndexOf(_roomNameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    checkedCount++;

                    var (widthMm, depthMm) = RoomFootprintCalculator.Calculate(room);
                    bool isCompliant = IsDepthCompliant(depthMm, _minDepthMm, _maxDepthMm);

                    var item = new RoomDepthCheckItem
                    {
                        Id = room.Id.GetValue(),
                        UniqueId = room.UniqueId,
                        Name = roomName,
                        Number = room.Number ?? string.Empty,
                        Level = levelName,
                        DepthMm = depthMm,
                        WidthMm = widthMm,
                        AreaM2 = RevitUnitConversion.ToSquareMeters(room.Area),
                        IsCompliant = isCompliant,
                        DeviationMm = CalculateDeviationMm(depthMm, _minDepthMm, _maxDepthMm)
                    };

                    if (isCompliant)
                    {
                        compliant.Add(item);
                    }
                    else
                    {
                        violations.Add(item);
                        violationElementIds.Add(room.Id);
                    }
                }

                int highlightedCount = 0;
                if (_mode == ModeHighlight && violationElementIds.Count > 0)
                {
                    highlightedCount = HighlightRooms(app, doc, violationElementIds);
                }

                ResultInfo = new CheckRoomDepthResult
                {
                    Success = true,
                    Message = $"Checked {checkedCount} rooms: {violations.Count} violate the depth requirement.",
                    Mode = _mode,
                    MinDepthMm = _minDepthMm,
                    MaxDepthMm = _maxDepthMm,
                    TotalRoomsChecked = checkedCount,
                    ViolationCount = violations.Count,
                    Violations = violations,
                    CompliantRooms = _includeCompliant ? compliant : new List<RoomDepthCheckItem>(),
                    HighlightedCount = highlightedCount
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new CheckRoomDepthResult
                {
                    Success = false,
                    Message = $"Failed to check room depth: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Check Room Depth";

        public static bool IsDepthCompliant(double depthMm, double? minDepthMm, double? maxDepthMm)
        {
            if (minDepthMm.HasValue && depthMm < minDepthMm.Value)
                return false;
            if (maxDepthMm.HasValue && depthMm > maxDepthMm.Value)
                return false;
            return true;
        }

        public static double CalculateDeviationMm(double depthMm, double? minDepthMm, double? maxDepthMm)
        {
            if (minDepthMm.HasValue && depthMm < minDepthMm.Value)
                return minDepthMm.Value - depthMm;
            if (maxDepthMm.HasValue && depthMm > maxDepthMm.Value)
                return depthMm - maxDepthMm.Value;
            return 0;
        }

        private int HighlightRooms(UIApplication app, Document doc, List<ElementId> elementIds)
        {
            var activeView = app.ActiveUIDocument.ActiveView;

            using var transaction = new Transaction(doc, "Check Room Depth Highlight");
            transaction.Start();

            var overrides = new OverrideGraphicSettings();
            var color = new Color(
                (byte)_highlightColor[0],
                (byte)_highlightColor[1],
                (byte)_highlightColor[2]);
            overrides.SetProjectionLineColor(color);
            overrides.SetSurfaceForegroundPatternColor(color);
            overrides.SetCutForegroundPatternColor(color);

            var solidFillPatternId = GetSolidFillPatternId(doc);
            if (solidFillPatternId != ElementId.InvalidElementId)
            {
                overrides.SetSurfaceForegroundPatternId(solidFillPatternId);
                overrides.SetCutForegroundPatternId(solidFillPatternId);
            }

            int highlighted = 0;
            foreach (var id in elementIds)
            {
                activeView.SetElementOverrides(id, overrides);
                highlighted++;
            }

            transaction.Commit();
            return highlighted;
        }

        private static ElementId GetSolidFillPatternId(Document doc)
        {
            var solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill);

            return solidFill?.Id ?? ElementId.InvalidElementId;
        }
    }
}
