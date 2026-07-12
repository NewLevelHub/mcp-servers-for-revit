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

        private double? _minWidthMm;
        private string _mode = ModeReport;
        private string _levelNameFilter = string.Empty;
        private string _roomNameFilter = string.Empty;
        private bool _includeCompliant;
        private bool _corridorOnly = true;
        private int[] _highlightColor = { 255, 0, 0 };

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
            int[] highlightColor = null)
        {
            _minWidthMm = minWidthMm;
            _mode = string.Equals(mode, ModeHighlight, StringComparison.OrdinalIgnoreCase)
                ? ModeHighlight
                : ModeReport;
            _levelNameFilter = levelName ?? string.Empty;
            _roomNameFilter = roomNameFilter ?? string.Empty;
            _includeCompliant = includeCompliant;
            _corridorOnly = corridorOnly;
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
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(room => room.Area > 0);

                var violations = new List<EvacuationWidthCheckItem>();
                var compliant = new List<EvacuationWidthCheckItem>();
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

        private int HighlightRooms(UIApplication app, Document doc, List<ElementId> elementIds)
        {
            var uidoc = app.ActiveUIDocument;
            var activeView = uidoc.ActiveView;

            using var transaction = new Transaction(doc, "Check Evacuation Width Highlight");
            transaction.Start();

            int highlighted = ElementGraphicOverrides.HighlightRoomsAndTags(
                activeView,
                doc,
                elementIds,
                _highlightColor);

            uidoc.Selection.SetElementIds(elementIds);
            uidoc.ShowElements(elementIds);

            transaction.Commit();
            return highlighted;
        }
    }
}
