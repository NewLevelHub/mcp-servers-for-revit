using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class DoorEgressInfoEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _levelNameFilter = string.Empty;

        public GetDoorEgressInfoResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(string levelName = "")
        {
            _levelNameFilter = levelName ?? string.Empty;
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
                var doorInstances = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .ToList();

                var result = new List<DoorEgressInfo>();
                foreach (var door in doorInstances)
                {
                    // Slopes / trims (откосы) live in OST_Doors but are not door
                    // blocks — exclude them so width checks do not count them (REV-41).
                    if (!OpeningFillClassifier.IsSchedulableDoor(door))
                        continue;

                    var levelName = doc.GetElement(door.LevelId)?.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(_levelNameFilter) &&
                        !string.Equals(levelName, _levelNameFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var widthMm = GetDoorWidthMm(door);
                    var hostWallId = door.Host is Wall wall ? (long?)wall.Id.GetValue() : null;

                    result.Add(new DoorEgressInfo
                    {
                        Id = door.Id.GetValue(),
                        UniqueId = door.UniqueId,
                        Family = door.Symbol?.Family?.Name ?? string.Empty,
                        Type = door.Symbol?.Name ?? string.Empty,
                        Level = levelName,
                        HostWallId = hostWallId,
                        OpeningWidthMm = widthMm,
                        IsOnEgressPath = IsOnEgressPath(door),
                        Location = FormatLocation(door.Location)
                    });
                }

                ResultInfo = new GetDoorEgressInfoResult
                {
                    Success = true,
                    Message = $"Collected egress info for {result.Count} doors.",
                    TotalDoors = result.Count,
                    Doors = result
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new GetDoorEgressInfoResult
                {
                    Success = false,
                    Message = $"Failed to collect door egress info: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Get Door Egress Info";

        private static double? GetDoorWidthMm(FamilyInstance door)
        {
            var widthParam = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                ?? door.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                ?? door.LookupParameter("Width")
                ?? door.LookupParameter("Ширина");

            if (widthParam == null || !widthParam.HasValue)
                return null;

            if (widthParam.StorageType != StorageType.Double)
                return null;

            return RevitUnitConversion.ToMillimeters(widthParam.AsDouble());
        }

        private static bool IsOnEgressPath(FamilyInstance door)
        {
            var fromName = door.FromRoom?.Name?.ToLowerInvariant() ?? string.Empty;
            var toName = door.ToRoom?.Name?.ToLowerInvariant() ?? string.Empty;
            var comments = door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString()?.ToLowerInvariant() ?? string.Empty;

            return ContainsEgressKeyword(fromName)
                || ContainsEgressKeyword(toName)
                || comments.Contains("эвак")
                || comments.Contains("egress");
        }

        private static bool ContainsEgressKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Contains("коридор")
                || value.Contains("лест")
                || value.Contains("эвак")
                || value.Contains("corridor")
                || value.Contains("stair")
                || value.Contains("egress")
                || value.Contains("hall");
        }

        private static string FormatLocation(Location location)
        {
            if (location is not LocationPoint point)
                return string.Empty;

            var mmX = RevitUnitConversion.ToMillimeters(point.Point.X);
            var mmY = RevitUnitConversion.ToMillimeters(point.Point.Y);
            var mmZ = RevitUnitConversion.ToMillimeters(point.Point.Z);
            return $"{mmX:F0}, {mmY:F0}, {mmZ:F0}";
        }
    }
}
