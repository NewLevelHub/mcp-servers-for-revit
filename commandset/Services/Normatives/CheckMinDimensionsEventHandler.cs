using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Normatives;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Normatives
{
    public class CheckMinDimensionsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string ModeReport = "report";
        public const string ModeHighlight = "highlight";

        private double? _minBalconyWidthMm;
        private double? _minLoggiaWidthMm;
        private double? _minLoggiaDepthMm;
        private double? _minFirePathOutdoorWidthMm;
        private double? _minFirePierToOpeningMm;
        private double? _minFirePierBetweenOpeningsMm;
        private string _mode = ModeReport;
        private string _levelNameFilter = string.Empty;
        private string _roomNameFilter = string.Empty;
        private bool _includeCompliant;
        private bool _checkFirePiers = true;
        private int[] _highlightColor = { 255, 0, 0 };

        public CheckMinDimensionsResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            double? minBalconyWidthMm,
            double? minLoggiaWidthMm,
            double? minLoggiaDepthMm,
            double? minFirePierToOpeningMm,
            double? minFirePierBetweenOpeningsMm,
            string mode = ModeReport,
            string levelName = "",
            string roomNameFilter = "",
            bool includeCompliant = false,
            bool checkFirePiers = true,
            int[] highlightColor = null,
            double? minFirePathOutdoorWidthMm = null)
        {
            _minBalconyWidthMm = minBalconyWidthMm;
            _minLoggiaWidthMm = minLoggiaWidthMm;
            _minLoggiaDepthMm = minLoggiaDepthMm;
            _minFirePathOutdoorWidthMm = minFirePathOutdoorWidthMm;
            _minFirePierToOpeningMm = minFirePierToOpeningMm;
            _minFirePierBetweenOpeningsMm = minFirePierBetweenOpeningsMm;
            _mode = string.Equals(mode, ModeHighlight, StringComparison.OrdinalIgnoreCase)
                ? ModeHighlight
                : ModeReport;
            _levelNameFilter = levelName ?? string.Empty;
            _roomNameFilter = roomNameFilter ?? string.Empty;
            _includeCompliant = includeCompliant;
            _checkFirePiers = checkFirePiers;
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
                if (!HasAnyLimit())
                {
                    ResultInfo = new CheckMinDimensionsResult
                    {
                        Success = false,
                        Message =
                            "At least one limit must be provided (minBalconyWidthMm, minLoggiaWidthMm, minLoggiaDepthMm, minFirePierToOpeningMm, minFirePierBetweenOpeningsMm)."
                    };
                    return;
                }

                var doc = app.ActiveUIDocument.Document;
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(room => room.Area > 0);

                var violations = new List<MinDimensionCheckItem>();
                var compliant = new List<MinDimensionCheckItem>();
                var highlightIds = new List<ElementId>();
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

                    if (!BalconyLoggiaClassifier.IsOutdoorSpaceForMinDimensions(roomName, roomPurpose))
                        continue;

                    checkedCount++;
                    var kind = BalconyLoggiaClassifier.Classify(roomName, roomPurpose);
                    var kindLabel = BalconyLoggiaClassifier.KindLabel(kind);
                    var (widthMm, depthMm) = RoomFootprintCalculator.Calculate(room);
                    var areaM2 = RevitUnitConversion.ToSquareMeters(room.Area);

                    if (kind == BalconyLoggiaClassifier.OutdoorSpaceKind.FirePathOutdoor)
                    {
                        EvaluateFirePathWidth(
                            room, kindLabel, levelName, roomName, widthMm, depthMm, areaM2,
                            violations, compliant, highlightIds);
                    }
                    else
                    {
                        EvaluateWidth(room, kind, kindLabel, levelName, roomName, widthMm, depthMm, areaM2, violations, compliant, highlightIds);
                        EvaluateDepth(room, kind, kindLabel, levelName, roomName, widthMm, depthMm, areaM2, violations, compliant, highlightIds);

                        if (_checkFirePiers)
                        {
                            EvaluateFirePiers(doc, room, kindLabel, levelName, roomName, widthMm, depthMm, areaM2, violations, compliant, highlightIds);
                        }
                    }
                }

                int highlightedCount = 0;
                if (_mode == ModeHighlight && highlightIds.Count > 0)
                {
                    highlightedCount = HighlightElements(app, doc, highlightIds.Distinct().ToList());
                }

                ResultInfo = new CheckMinDimensionsResult
                {
                    Success = true,
                    Message = $"Checked {checkedCount} balconies/loggias: {violations.Count} violations found.",
                    Mode = _mode,
                    MinBalconyWidthMm = _minBalconyWidthMm,
                    MinLoggiaWidthMm = _minLoggiaWidthMm,
                    MinLoggiaDepthMm = _minLoggiaDepthMm,
                    MinFirePathOutdoorWidthMm = _minFirePathOutdoorWidthMm,
                    MinFirePierToOpeningMm = _minFirePierToOpeningMm,
                    MinFirePierBetweenOpeningsMm = _minFirePierBetweenOpeningsMm,
                    TotalSpacesChecked = checkedCount,
                    ViolationCount = violations.Count,
                    Violations = violations,
                    CompliantItems = _includeCompliant ? compliant : new List<MinDimensionCheckItem>(),
                    HighlightedCount = highlightedCount
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new CheckMinDimensionsResult
                {
                    Success = false,
                    Message = $"Failed to check minimum dimensions: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Check Min Dimensions";

        public static bool IsCompliant(double actualMm, double requiredMm)
        {
            return actualMm >= requiredMm;
        }

        public static double CalculateDeviationMm(double actualMm, double requiredMm)
        {
            return actualMm < requiredMm ? requiredMm - actualMm : 0;
        }

        private bool HasAnyLimit()
        {
            return _minBalconyWidthMm.HasValue
                || _minLoggiaWidthMm.HasValue
                || _minLoggiaDepthMm.HasValue
                || _minFirePathOutdoorWidthMm.HasValue
                || _minFirePierToOpeningMm.HasValue
                || _minFirePierBetweenOpeningsMm.HasValue;
        }

        private void EvaluateFirePathWidth(
            Room room,
            string kindLabel,
            string levelName,
            string roomName,
            double widthMm,
            double depthMm,
            double areaM2,
            List<MinDimensionCheckItem> violations,
            List<MinDimensionCheckItem> compliant,
            List<ElementId> highlightIds)
        {
            if (!_minFirePathOutdoorWidthMm.HasValue)
                return;

            AddDimensionItem(
                room,
                kindLabel,
                levelName,
                roomName,
                checkType: "fire_path_width",
                metric: "width",
                actualValueMm: widthMm,
                requiredValueMm: _minFirePathOutdoorWidthMm.Value,
                widthMm,
                depthMm,
                areaM2,
                violations,
                compliant,
                highlightIds);
        }

        private void EvaluateWidth(
            Room room,
            BalconyLoggiaClassifier.OutdoorSpaceKind kind,
            string kindLabel,
            string levelName,
            string roomName,
            double widthMm,
            double depthMm,
            double areaM2,
            List<MinDimensionCheckItem> violations,
            List<MinDimensionCheckItem> compliant,
            List<ElementId> highlightIds)
        {
            double? required = kind switch
            {
                BalconyLoggiaClassifier.OutdoorSpaceKind.Balcony => _minBalconyWidthMm,
                BalconyLoggiaClassifier.OutdoorSpaceKind.Loggia => _minLoggiaWidthMm ?? _minBalconyWidthMm,
                BalconyLoggiaClassifier.OutdoorSpaceKind.Terrace => _minBalconyWidthMm,
                _ => null
            };

            if (!required.HasValue)
                return;

            AddDimensionItem(
                room,
                kindLabel,
                levelName,
                roomName,
                checkType: "width",
                metric: "width",
                actualValueMm: widthMm,
                requiredValueMm: required.Value,
                widthMm,
                depthMm,
                areaM2,
                violations,
                compliant,
                highlightIds);
        }

        private void EvaluateDepth(
            Room room,
            BalconyLoggiaClassifier.OutdoorSpaceKind kind,
            string kindLabel,
            string levelName,
            string roomName,
            double widthMm,
            double depthMm,
            double areaM2,
            List<MinDimensionCheckItem> violations,
            List<MinDimensionCheckItem> compliant,
            List<ElementId> highlightIds)
        {
            if (!_minLoggiaDepthMm.HasValue)
                return;

            if (kind != BalconyLoggiaClassifier.OutdoorSpaceKind.Loggia
                && kind != BalconyLoggiaClassifier.OutdoorSpaceKind.Terrace
                && kind != BalconyLoggiaClassifier.OutdoorSpaceKind.Balcony)
            {
                return;
            }

            AddDimensionItem(
                room,
                kindLabel,
                levelName,
                roomName,
                checkType: "depth",
                metric: "depth",
                actualValueMm: depthMm,
                requiredValueMm: _minLoggiaDepthMm.Value,
                widthMm,
                depthMm,
                areaM2,
                violations,
                compliant,
                highlightIds);
        }

        private void EvaluateFirePiers(
            Document doc,
            Room room,
            string kindLabel,
            string levelName,
            string roomName,
            double widthMm,
            double depthMm,
            double areaM2,
            List<MinDimensionCheckItem> violations,
            List<MinDimensionCheckItem> compliant,
            List<ElementId> highlightIds)
        {
            var piers = FirePierCalculator.CalculateForRoom(doc, room);
            foreach (var pier in piers)
            {
                double? required = pier.Kind switch
                {
                    FirePierCalculator.PierKind.EndPier => _minFirePierToOpeningMm,
                    FirePierCalculator.PierKind.BetweenOpenings => _minFirePierBetweenOpeningsMm,
                    _ => null
                };

                if (!required.HasValue)
                    continue;

                var item = new MinDimensionCheckItem
                {
                    Id = room.Id.GetValue(),
                    UniqueId = room.UniqueId,
                    Name = roomName,
                    Number = room.Number ?? string.Empty,
                    Level = levelName,
                    SpaceKind = kindLabel,
                    CheckType = "fire_pier",
                    Metric = pier.Kind == FirePierCalculator.PierKind.EndPier
                        ? "pier_to_opening"
                        : "pier_between_openings",
                    ActualValueMm = pier.LengthMm,
                    RequiredValueMm = required.Value,
                    IsCompliant = IsCompliant(pier.LengthMm, required.Value),
                    DeviationMm = CalculateDeviationMm(pier.LengthMm, required.Value),
                    WidthMm = widthMm,
                    DepthMm = depthMm,
                    AreaM2 = areaM2,
                    WallId = pier.WallId,
                    PierKind = pier.Kind.ToString(),
                    AdjacentOpeningIds = pier.AdjacentOpeningIds
                };

                if (item.IsCompliant)
                {
                    compliant.Add(item);
                }
                else
                {
                    violations.Add(item);
                    highlightIds.Add(room.Id);
                    highlightIds.Add(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(pier.WallId));
                }
            }
        }

        private void AddDimensionItem(
            Room room,
            string kindLabel,
            string levelName,
            string roomName,
            string checkType,
            string metric,
            double actualValueMm,
            double requiredValueMm,
            double widthMm,
            double depthMm,
            double areaM2,
            List<MinDimensionCheckItem> violations,
            List<MinDimensionCheckItem> compliant,
            List<ElementId> highlightIds)
        {
            var item = new MinDimensionCheckItem
            {
                Id = room.Id.GetValue(),
                UniqueId = room.UniqueId,
                Name = roomName,
                Number = room.Number ?? string.Empty,
                Level = levelName,
                SpaceKind = kindLabel,
                CheckType = checkType,
                Metric = metric,
                ActualValueMm = actualValueMm,
                RequiredValueMm = requiredValueMm,
                IsCompliant = IsCompliant(actualValueMm, requiredValueMm),
                DeviationMm = CalculateDeviationMm(actualValueMm, requiredValueMm),
                WidthMm = widthMm,
                DepthMm = depthMm,
                AreaM2 = areaM2
            };

            if (item.IsCompliant)
            {
                compliant.Add(item);
            }
            else
            {
                violations.Add(item);
                highlightIds.Add(room.Id);
            }
        }

        private int HighlightElements(UIApplication app, Document doc, List<ElementId> elementIds)
        {
            var uidoc = app.ActiveUIDocument;
            var activeView = uidoc.ActiveView;

            using var transaction = new Transaction(doc, "Check Min Dimensions Highlight");
            transaction.Start();

            var roomIds = elementIds
                .Where(id => doc.GetElement(id) is Room)
                .ToList();
            var otherIds = elementIds.Except(roomIds).ToList();

            int highlighted = 0;
            if (roomIds.Count > 0)
            {
                highlighted += ElementGraphicOverrides.HighlightRoomsAndTags(
                    activeView,
                    doc,
                    roomIds,
                    _highlightColor);
            }

            if (otherIds.Count > 0)
            {
                highlighted += ElementGraphicOverrides.ApplyToView(
                    activeView,
                    doc,
                    otherIds,
                    _highlightColor);
            }

            uidoc.Selection.SetElementIds(elementIds);
            uidoc.ShowElements(elementIds);

            transaction.Commit();
            return highlighted;
        }
    }
}
