using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class ExportTepDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private const double LevelElevationToleranceFeet = 0.1 / 304.8;
        private const string UnassignedPurpose = "Не задано";

        private bool _includeUnplacedRooms;
        private bool _includeNotEnclosedRooms;

        public ExportTepDataResult ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(bool includeUnplacedRooms = false, bool includeNotEnclosedRooms = false)
        {
            _includeUnplacedRooms = includeUnplacedRooms;
            _includeNotEnclosedRooms = includeNotEnclosedRooms;
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
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var doc = app.ActiveUIDocument.Document;
                ResultInfo = Compute(
                    doc,
                    _includeUnplacedRooms,
                    _includeNotEnclosedRooms,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                ResultInfo = new ExportTepDataResult
                {
                    Success = false,
                    Message = $"Error exporting TEP data: {ex.Message}"
                };
            }
            finally
            {
                if (ResultInfo != null)
                {
                    stopwatch.Stop();
                    ResultInfo.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;
                }

                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public static ExportTepDataResult Compute(
            Document doc,
            bool includeUnplacedRooms = false,
            bool includeNotEnclosedRooms = false,
            long executionTimeMs = 0)
        {
            var placedRooms = CollectRooms(doc, includeUnplacedRooms, includeNotEnclosedRooms);
            var levelStats = new Dictionary<string, TepLevelData>();
            var purposeStats = new Dictionary<string, TepRoomPurposeData>();

            double totalAreaM2 = 0;
            double totalVolumeM3 = 0;

            foreach (Room room in placedRooms)
            {
                double areaM2 = RevitUnitConversion.ToSquareMeters(room.Area);
                double volumeM3 = RevitUnitConversion.ToCubicMeters(room.Volume);

                totalAreaM2 += areaM2;
                totalVolumeM3 += volumeM3;

                string levelName = room.Level?.Name ?? "No Level";
                if (!levelStats.TryGetValue(levelName, out var levelData))
                {
                    levelData = new TepLevelData
                    {
                        LevelName = levelName,
                        Elevation = room.Level != null
                            ? Round(RevitUnitConversion.ToMillimeters(room.Level.Elevation))
                            : 0
                    };
                    levelStats[levelName] = levelData;
                }

                levelData.Area = Round(levelData.Area + areaM2);
                levelData.Volume = Round(levelData.Volume + volumeM3);
                levelData.RoomCount++;

                string purpose = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString()?.Trim();
                if (string.IsNullOrEmpty(purpose))
                    purpose = UnassignedPurpose;

                if (!purposeStats.TryGetValue(purpose, out var purposeData))
                {
                    purposeData = new TepRoomPurposeData { Purpose = purpose };
                    purposeStats[purpose] = purposeData;
                }

                purposeData.RoomCount++;
                purposeData.Area = Round(purposeData.Area + areaM2);
                purposeData.Volume = Round(purposeData.Volume + volumeM3);
            }

            double buildingFootprintAreaM2 = ComputeBuildingFootprintAreaM2(placedRooms);
            var orderedLevels = levelStats.Values
                .OrderBy(level => level.Elevation)
                .ThenBy(level => level.LevelName)
                .ToList();

            foreach (var level in orderedLevels)
            {
                level.StoreyKind = StoreyLevelClassifier.Classify(level.LevelName, level.Elevation);
            }

            var levelsWithRooms = orderedLevels.Where(level => level.RoomCount > 0).ToList();
            int aboveGroundCount = levelsWithRooms.Count(level => level.StoreyKind == StoreyLevelClassifier.AboveGround);
            int basementCount = levelsWithRooms.Count(level => level.StoreyKind == StoreyLevelClassifier.Basement);
            int technicalCount = levelsWithRooms.Count(level => level.StoreyKind == StoreyLevelClassifier.Technical);
            int roofCount = levelsWithRooms.Count(level => level.StoreyKind == StoreyLevelClassifier.Roof);

            return new ExportTepDataResult
            {
                ProjectName = doc.Title,
                BuildingFootprintArea = Round(buildingFootprintAreaM2),
                TotalArea = Round(totalAreaM2),
                TotalVolume = Round(totalVolumeM3),
                StoreyCount = aboveGroundCount,
                BasementStoreyCount = basementCount,
                TechnicalStoreyCount = technicalCount,
                RoofStoreyCount = roofCount,
                TotalRooms = placedRooms.Count,
                Levels = orderedLevels,
                RoomsByPurpose = purposeStats.Values
                    .OrderByDescending(purpose => purpose.Area)
                    .ThenBy(purpose => purpose.Purpose)
                    .ToList(),
                ExecutionTimeMs = executionTimeMs,
                Success = true,
                Message = $"Successfully exported TEP data for {placedRooms.Count} rooms across {orderedLevels.Count} levels " +
                          $"(aboveGround={aboveGroundCount}, basement={basementCount}, technical={technicalCount}, roof={roofCount})"
            };
        }

        private static List<Room> CollectRooms(
            Document doc,
            bool includeUnplacedRooms,
            bool includeNotEnclosedRooms)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(room =>
                    (includeUnplacedRooms || room.Area > 0) &&
                    (includeNotEnclosedRooms || room.Area > 0))
                .ToList();
        }

        private static double ComputeBuildingFootprintAreaM2(IReadOnlyList<Room> rooms)
        {
            var roomsWithLevel = rooms
                .Where(room => room.Area > 0 && room.Level != null)
                .ToList();

            if (roomsWithLevel.Count == 0)
                return 0;

            // Пятно застройки = нижний надземный этаж с rooms (не −1 / цоколь).
            var aboveGroundRooms = roomsWithLevel
                .Where(room =>
                {
                    double elevationMm = RevitUnitConversion.ToMillimeters(room.Level.Elevation);
                    return StoreyLevelClassifier.IsAboveGround(room.Level.Name, elevationMm);
                })
                .ToList();

            var footprintRooms = aboveGroundRooms.Count > 0
                ? aboveGroundRooms
                : roomsWithLevel;

            double lowestElevation = footprintRooms.Min(room => room.Level.Elevation);

            return footprintRooms
                .Where(room => Math.Abs(room.Level.Elevation - lowestElevation) <= LevelElevationToleranceFeet)
                .Sum(room => RevitUnitConversion.ToSquareMeters(room.Area));
        }

        private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        public string GetName()
        {
            return "Export TEP Data";
        }
    }
}
