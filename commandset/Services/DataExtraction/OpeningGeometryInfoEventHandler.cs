using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    /// Collects window sill height and door/window opening height for REV-58
    /// (window_sill_height / opening_height). Excludes accessories (REV-41/48).
    /// </summary>
    public class OpeningGeometryInfoEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _levelNameFilter = string.Empty;

        public GetOpeningGeometryInfoResult ResultInfo { get; private set; } = new();
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
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var openings = new List<OpeningGeometryInfo>();

                var windows = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .ToList();

                foreach (var window in windows)
                {
                    if (!OpeningFillClassifier.IsSchedulableWindow(window))
                        continue;

                    var levelName = doc.GetElement(window.LevelId)?.Name ?? string.Empty;
                    if (!MatchesLevel(levelName))
                        continue;

                    openings.Add(BuildInfo(window, "window", levelName, isOnEgressPath: false));
                }

                var doors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .ToList();

                foreach (var door in doors)
                {
                    if (!OpeningFillClassifier.IsSchedulableDoor(door))
                        continue;

                    var levelName = doc.GetElement(door.LevelId)?.Name ?? string.Empty;
                    if (!MatchesLevel(levelName))
                        continue;

                    openings.Add(BuildInfo(door, "door", levelName, IsOnEgressPath(door)));
                }

                var windowCount = openings.Count(o => o.Category == "window");
                var doorCount = openings.Count(o => o.Category == "door");

                ResultInfo = new GetOpeningGeometryInfoResult
                {
                    Success = true,
                    Message =
                        $"Collected opening geometry for {openings.Count} openings " +
                        $"({windowCount} windows, {doorCount} doors).",
                    TotalOpenings = openings.Count,
                    TotalWindows = windowCount,
                    TotalDoors = doorCount,
                    Openings = openings
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new GetOpeningGeometryInfoResult
                {
                    Success = false,
                    Message = $"Failed to collect opening geometry: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Get Opening Geometry Info";

        private bool MatchesLevel(string levelName)
        {
            if (string.IsNullOrWhiteSpace(_levelNameFilter))
                return true;

            return string.Equals(levelName, _levelNameFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static OpeningGeometryInfo BuildInfo(
            FamilyInstance instance,
            string category,
            string levelName,
            bool isOnEgressPath)
        {
            var hostWallId = instance.Host is Wall wall ? (long?)wall.Id.GetValue() : null;

            return new OpeningGeometryInfo
            {
                Id = instance.Id.GetValue(),
                UniqueId = instance.UniqueId,
                Category = category,
                Family = instance.Symbol?.Family?.Name ?? string.Empty,
                Type = instance.Symbol?.Name ?? string.Empty,
                Level = levelName,
                HostWallId = hostWallId,
                SillHeightMm = category == "window" ? GetSillHeightMm(instance) : null,
                OpeningHeightMm = GetOpeningHeightMm(instance, category),
                IsOnEgressPath = isOnEgressPath,
                Location = FormatLocation(instance.Location)
            };
        }

        private static double? GetSillHeightMm(FamilyInstance window)
        {
            var sillParam = window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)
                ?? window.LookupParameter("Sill Height")
                ?? window.LookupParameter("Высота подоконника")
                ?? window.LookupParameter("Подоконник");

            return ReadLengthMm(sillParam);
        }

        private static double? GetOpeningHeightMm(FamilyInstance instance, string category)
        {
            Parameter heightParam = null;
            if (category == "window")
            {
                heightParam = instance.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)
                    ?? instance.Symbol?.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)
                    ?? instance.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM)
                    ?? instance.Symbol?.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM)
                    ?? instance.LookupParameter("Height")
                    ?? instance.LookupParameter("Высота");
            }
            else
            {
                heightParam = instance.get_Parameter(BuiltInParameter.DOOR_HEIGHT)
                    ?? instance.Symbol?.get_Parameter(BuiltInParameter.DOOR_HEIGHT)
                    ?? instance.LookupParameter("Height")
                    ?? instance.LookupParameter("Высота");
            }

            return ReadLengthMm(heightParam);
        }

        private static double? ReadLengthMm(Parameter param)
        {
            if (param == null || !param.HasValue)
                return null;
            if (param.StorageType != StorageType.Double)
                return null;

            return RevitUnitConversion.ToMillimeters(param.AsDouble());
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
