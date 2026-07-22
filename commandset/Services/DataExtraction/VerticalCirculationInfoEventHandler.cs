using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    /// Collects stair / ramp / railing geometry for REV-59 norm checks.
    /// </summary>
    public class VerticalCirculationInfoEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _levelNameFilter = string.Empty;

        public GetVerticalCirculationInfoResult ResultInfo { get; private set; } = new();
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
                var stairs = CollectStairs(doc);
                var ramps = CollectRamps(doc);
                var railings = CollectRailings(doc);

                ResultInfo = new GetVerticalCirculationInfoResult
                {
                    Success = true,
                    Message =
                        $"Collected vertical circulation: {stairs.Count} stairs, " +
                        $"{ramps.Count} ramps, {railings.Count} railings.",
                    TotalStairs = stairs.Count,
                    TotalRamps = ramps.Count,
                    TotalRailings = railings.Count,
                    Stairs = stairs,
                    Ramps = ramps,
                    Railings = railings
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new GetVerticalCirculationInfoResult
                {
                    Success = false,
                    Message = $"Failed to collect vertical circulation info: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Get Vertical Circulation Info";

        private List<StairGeometryInfo> CollectStairs(Document doc)
        {
            var result = new List<StairGeometryInfo>();
            var elements = new FilteredElementCollector(doc)
                .OfClass(typeof(Stairs))
                .WhereElementIsNotElementType()
                .OfType<Stairs>()
                .ToList();

            foreach (var stair in elements)
            {
                var levelName = ResolveStairsBaseLevelName(doc, stair);
                if (!MatchesLevel(levelName))
                    continue;

                result.Add(new StairGeometryInfo
                {
                    Id = stair.Id.GetValue(),
                    UniqueId = stair.UniqueId,
                    Name = stair.Name ?? string.Empty,
                    Type = doc.GetElement(stair.GetTypeId())?.Name ?? string.Empty,
                    Level = levelName,
                    WidthMm = GetStairWidthMm(doc, stair),
                    RiserMm = ToMmOrNull(stair.ActualRiserHeight),
                    TreadMm = ToMmOrNull(stair.ActualTreadDepth)
                });
            }

            return result;
        }

        private List<RampGeometryInfo> CollectRamps(Document doc)
        {
            var result = new List<RampGeometryInfo>();
            var elements = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Ramps)
                .WhereElementIsNotElementType()
                .ToList();

            foreach (var element in elements)
            {
                var levelName = ResolveElementLevelName(doc, element);
                if (!MatchesLevel(levelName))
                    continue;

                result.Add(new RampGeometryInfo
                {
                    Id = element.Id.GetValue(),
                    UniqueId = element.UniqueId,
                    Name = element.Name ?? string.Empty,
                    Type = doc.GetElement(element.GetTypeId())?.Name ?? string.Empty,
                    Level = levelName,
                    WidthMm = GetRampWidthMm(element),
                    SlopePercent = GetRampSlopePercent(doc, element)
                });
            }

            return result;
        }

        private List<RailingGeometryInfo> CollectRailings(Document doc)
        {
            var result = new List<RailingGeometryInfo>();
            var elements = new FilteredElementCollector(doc)
                .OfClass(typeof(Railing))
                .WhereElementIsNotElementType()
                .OfType<Railing>()
                .ToList();

            foreach (var railing in elements)
            {
                var levelName = ResolveElementLevelName(doc, railing);
                if (!MatchesLevel(levelName))
                    continue;

                long? hostId = null;
                try
                {
                    var hostIdProp = railing.GetType().GetProperty("HostId");
                    if (hostIdProp?.GetValue(railing) is ElementId hid &&
                        hid != ElementId.InvalidElementId)
                    {
                        hostId = hid.GetValue();
                    }
                }
                catch
                {
                    // optional host id
                }

                result.Add(new RailingGeometryInfo
                {
                    Id = railing.Id.GetValue(),
                    UniqueId = railing.UniqueId,
                    Name = railing.Name ?? string.Empty,
                    Type = doc.GetElement(railing.GetTypeId())?.Name ?? string.Empty,
                    Level = levelName,
                    HeightMm = GetRailingHeightMm(doc, railing),
                    HostElementId = hostId
                });
            }

            return result;
        }

        private bool MatchesLevel(string levelName)
        {
            if (string.IsNullOrWhiteSpace(_levelNameFilter))
                return true;
            return string.Equals(levelName, _levelNameFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static double? GetStairWidthMm(Document doc, Stairs stair)
        {
            double? minWidth = null;
            try
            {
                foreach (var runId in stair.GetStairsRuns())
                {
                    if (doc.GetElement(runId) is not StairsRun run)
                        continue;
                    var w = ToMmOrNull(run.ActualRunWidth);
                    if (w == null) continue;
                    minWidth = minWidth == null ? w : Math.Min(minWidth.Value, w.Value);
                }
            }
            catch
            {
                // fall through to parameters
            }

            if (minWidth != null && minWidth > 0)
                return minWidth;

            // R23 has no STAIRS_ATTR_MINIMUM_RUN_WIDTH BIP — use named params / type.
            var typeElement = doc.GetElement(stair.GetTypeId());
            return ReadLengthMm(
                stair.LookupParameter("Minimum Run Width")
                ?? stair.LookupParameter("Width")
                ?? stair.LookupParameter("Ширина")
                ?? typeElement?.LookupParameter("Minimum Run Width")
                ?? typeElement?.LookupParameter("Width")
                ?? typeElement?.LookupParameter("Ширина"));
        }

        private static double? GetRampWidthMm(Element ramp)
        {
            return ReadLengthMm(
                ramp.LookupParameter("Width")
                ?? ramp.LookupParameter("Ширина")
                ?? ramp.LookupParameter("Ramp Width")
                ?? ramp.LookupParameter("Walkway Width")
                ?? ramp.Document.GetElement(ramp.GetTypeId())?.LookupParameter("Width"));
        }

        private static double? GetRampSlopePercent(Document doc, Element ramp)
        {
            // Prefer explicit slope parameter when present (already %).
            var slopeParam = ramp.LookupParameter("Slope")
                ?? ramp.LookupParameter("Уклон")
                ?? ramp.LookupParameter("Max Slope")
                ?? doc.GetElement(ramp.GetTypeId())?.LookupParameter("Max Slope");
            if (slopeParam != null && slopeParam.HasValue && slopeParam.StorageType == StorageType.Double)
            {
                var raw = slopeParam.AsDouble();
                // Revit often stores slope as a ratio (rise/run), e.g. 0.05 = 5%.
                if (raw > 0 && raw < 1.0)
                    return Math.Round(raw * 100.0, 2);
                if (raw >= 1.0 && raw <= 100.0)
                    return Math.Round(raw, 2);
            }

            // Fallback: ΔZ / horizontal length from bounding box.
            try
            {
                var box = ramp.get_BoundingBox(null);
                if (box == null) return null;
                var dz = Math.Abs(box.Max.Z - box.Min.Z);
                var dx = box.Max.X - box.Min.X;
                var dy = box.Max.Y - box.Min.Y;
                var horiz = Math.Sqrt(dx * dx + dy * dy);
                if (horiz < 1e-6) return null;
                return Math.Round((dz / horiz) * 100.0, 2);
            }
            catch
            {
                return null;
            }
        }

        private static double? GetRailingHeightMm(Document doc, Railing railing)
        {
            var typeElement = doc.GetElement(railing.GetTypeId());

            // 1) Explicit length parameters on instance / type
            var fromParam = ReadLengthMm(
                railing.LookupParameter("Height")
                ?? railing.LookupParameter("Высота")
                ?? railing.LookupParameter("Railing Height")
                ?? railing.LookupParameter("Top Rail Height")
                ?? railing.LookupParameter("Высота верхнего поручня")
                ?? typeElement?.LookupParameter("Height")
                ?? typeElement?.LookupParameter("Высота")
                ?? typeElement?.LookupParameter("Top Rail Height")
                ?? typeElement?.LookupParameter("Высота верхнего поручня")
                ?? typeElement?.LookupParameter("Handrail Height")
                ?? typeElement?.LookupParameter("Высота поручня"));
            if (fromParam != null && fromParam > 0)
                return fromParam;

            // 2) Top rail element geometry (relative height of BB)
            try
            {
                var topRailId = railing.TopRail;
                if (topRailId != null && topRailId != ElementId.InvalidElementId)
                {
                    var topRail = doc.GetElement(topRailId);
                    if (topRail != null)
                    {
                        var box = topRail.get_BoundingBox(null);
                        var hostBox = railing.get_BoundingBox(null);
                        if (box != null && hostBox != null)
                        {
                            var heightFt = box.Max.Z - hostBox.Min.Z;
                            var mm = ToMmOrNull(heightFt);
                            if (mm != null && mm >= 400 && mm <= 2000)
                                return Math.Round(mm.Value, 1);
                        }
                    }
                }
            }
            catch
            {
                // TopRail may be unavailable on some types
            }

            // 3) ADSK naming: «ADSK_Стандартное_h 1200 …», «ADSK_МГН_h 900»
            var fromName = ParseHeightMmFromName(railing.Name)
                ?? ParseHeightMmFromName(typeElement?.Name);
            if (fromName != null)
                return fromName;

            return null;
        }

        /// <summary>
        /// Parse «h 1200», «h1200», «Н=900» style heights from type/instance names.
        /// </summary>
        private static double? ParseHeightMmFromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            // h 1200 / h1200 / H=900 / высота 1200
            var match = System.Text.RegularExpressions.Regex.Match(
                name,
                @"(?:^|[_\s\-])(?:h|H|н|Н)\s*[=:]?\s*(\d{3,4})(?:\b|$)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                match = System.Text.RegularExpressions.Regex.Match(
                    name,
                    @"(?:высота|Высота)\s*[=:]?\s*(\d{3,4})",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            }

            if (!match.Success)
                return null;

            if (!double.TryParse(
                    match.Groups[1].Value,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var mm))
                return null;

            if (mm < 400 || mm > 2000)
                return null;
            return mm;
        }

        /// <summary>
        /// Stairs in R23 expose base level via STAIRS_BASE_LEVEL_PARAM, not BaseLevelId.
        /// </summary>
        private static string ResolveStairsBaseLevelName(Document doc, Stairs stair)
        {
            var baseLevelParam = stair.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)
                ?? stair.LookupParameter("Base Level")
                ?? stair.LookupParameter("Базовый уровень");
            if (baseLevelParam != null &&
                baseLevelParam.HasValue &&
                baseLevelParam.StorageType == StorageType.ElementId)
            {
                var level = doc.GetElement(baseLevelParam.AsElementId());
                if (level != null)
                    return level.Name ?? string.Empty;
            }

            return ResolveElementLevelName(doc, stair);
        }

        private static string ResolveElementLevelName(Document doc, Element element)
        {
            try
            {
                if (element.LevelId != null && element.LevelId != ElementId.InvalidElementId)
                    return doc.GetElement(element.LevelId)?.Name ?? string.Empty;
            }
            catch
            {
                // ignore
            }

            var levelParam = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                ?? element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                ?? element.LookupParameter("Level")
                ?? element.LookupParameter("Base Level");
            if (levelParam != null && levelParam.HasValue && levelParam.StorageType == StorageType.ElementId)
            {
                var level = doc.GetElement(levelParam.AsElementId());
                return level?.Name ?? string.Empty;
            }

            // Hosted railing on stairs: inherit host base level
            if (element is Railing railing)
            {
                try
                {
                    var hostIdProp = railing.GetType().GetProperty("HostId");
                    if (hostIdProp?.GetValue(railing) is ElementId hid &&
                        hid != ElementId.InvalidElementId &&
                        doc.GetElement(hid) is Stairs hostStair)
                    {
                        return ResolveStairsBaseLevelName(doc, hostStair);
                    }
                }
                catch
                {
                    // optional
                }
            }

            return string.Empty;
        }

        private static double? ToMmOrNull(double feet)
        {
            // net48: no double.IsFinite
            if (double.IsNaN(feet) || double.IsInfinity(feet) || feet <= 0)
                return null;
            return RevitUnitConversion.ToMillimeters(feet);
        }

        private static double? ReadLengthMm(Parameter param)
        {
            if (param == null || !param.HasValue)
                return null;
            if (param.StorageType != StorageType.Double)
                return null;
            return RevitUnitConversion.ToMillimeters(param.AsDouble());
        }
    }
}
