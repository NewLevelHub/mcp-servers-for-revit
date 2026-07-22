using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class RoomGeometryMetricsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private bool _includeUnplacedRooms;
        private string _levelNameFilter = string.Empty;

        public GetRoomGeometryMetricsResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(bool includeUnplacedRooms = false, string levelName = "")
        {
            _includeUnplacedRooms = includeUnplacedRooms;
            _levelNameFilter = levelName ?? string.Empty;
            TaskCompleted = false;
            _resetEvent.Reset();
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
                var doc = app.ActiveUIDocument.Document;
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(room => _includeUnplacedRooms || room.Area > 0);

                var metrics = new List<RoomGeometryMetric>();

                foreach (var room in rooms)
                {
                    var levelName = room.Level?.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(_levelNameFilter) &&
                        !string.Equals(levelName, _levelNameFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var (widthMm, depthMm) = RoomFootprintCalculator.Calculate(room);
                    var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
                    bool isCorridor = CorridorClassifier.IsCorridor(roomName);

                    metrics.Add(new RoomGeometryMetric
                    {
                        Id = room.Id.GetValue(),
                        UniqueId = room.UniqueId,
                        Name = roomName,
                        Number = room.Number ?? string.Empty,
                        Level = levelName,
                        AreaM2 = RevitUnitConversion.ToSquareMeters(room.Area),
                        WidthMm = widthMm,
                        DepthMm = depthMm,
                        CorridorWidthMm = isCorridor ? widthMm : null
                    });
                }

                ResultInfo = new GetRoomGeometryMetricsResult
                {
                    Success = true,
                    Message = $"Collected geometry metrics for {metrics.Count} rooms.",
                    TotalRooms = metrics.Count,
                    Rooms = metrics
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new GetRoomGeometryMetricsResult
                {
                    Success = false,
                    Message = $"Failed to collect room geometry metrics: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Get Room Geometry Metrics";
    }
}
