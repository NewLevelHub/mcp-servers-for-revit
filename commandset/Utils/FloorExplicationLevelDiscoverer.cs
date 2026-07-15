using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Discovers floor экспликация level groups from the model:
    /// levels that host explicit (полы)* finishes, merged into typified ranges when
    /// consecutive storeys share the same finish types and XY footprint (same place).
    /// Recipe columns stay fixed; group titles are dynamic (not hardcoded 2–16).
    /// </summary>
    public static class FloorExplicationLevelDiscoverer
    {
        private static readonly Regex NumberedStorey = new Regex(
            @"^(?:(?:уровень|level|ур\.?)\s*(-?\d+)|(-?\d+)\s*этаж)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>XY bbox and area rounding in mm / m² for «same place» match.</summary>
        private const double BboxQuantumMm = 200;
        private const double AreaQuantumM2 = 0.5;

        public sealed class DiscoveredGroup
        {
            public string Key { get; set; } = "";
            public string Title { get; set; } = "";
            public List<Level> Levels { get; set; } = new List<Level>();
            public bool IsTypicalRange { get; set; }
            public string Signature { get; set; } = "";
        }

        private sealed class LevelFootprint
        {
            public Level Level { get; set; }
            public int? StoreyNumber { get; set; }
            public string Signature { get; set; } = "";
            public int FinishCount { get; set; }
        }

        public static List<DiscoveredGroup> Discover(Document doc)
        {
            if (doc == null)
                return new List<DiscoveredGroup>();

            var byLevel = new Dictionary<ElementId, List<Floor>>();
            foreach (var floor in new FilteredElementCollector(doc)
                         .OfClass(typeof(Floor))
                         .WhereElementIsNotElementType()
                         .Cast<Floor>())
            {
                if (!FloorFinishClassifier.IsExplicitFloorFinish(floor))
                    continue;

                var levelId = floor.LevelId;
                if (levelId == null || levelId == ElementId.InvalidElementId)
                    continue;

                if (!byLevel.TryGetValue(levelId, out var list))
                {
                    list = new List<Floor>();
                    byLevel[levelId] = list;
                }

                list.Add(floor);
            }

            if (byLevel.Count == 0)
                return new List<DiscoveredGroup>();

            var footprints = new List<LevelFootprint>();
            foreach (var kv in byLevel)
            {
                var level = doc.GetElement(kv.Key) as Level;
                if (level == null)
                    continue;

                footprints.Add(new LevelFootprint
                {
                    Level = level,
                    StoreyNumber = GetStoreyNumber(level.Name),
                    Signature = BuildSignature(kv.Value),
                    FinishCount = kv.Value.Count
                });
            }

            footprints = footprints
                .OrderBy(f => f.Level.Elevation)
                .ThenBy(f => f.Level.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var groups = new List<DiscoveredGroup>();
            var i = 0;
            while (i < footprints.Count)
            {
                var run = new List<LevelFootprint> { footprints[i] };
                var j = i + 1;
                while (j < footprints.Count && CanMergeAsTypical(run[run.Count - 1], footprints[j]))
                {
                    run.Add(footprints[j]);
                    j++;
                }

                groups.Add(BuildGroup(run));
                i = j;
            }

            return groups;
        }

        private static bool CanMergeAsTypical(LevelFootprint previous, LevelFootprint next)
        {
            if (previous == null || next == null)
                return false;

            if (!string.Equals(previous.Signature, next.Signature, StringComparison.Ordinal))
                return false;

            // Typified range only for consecutive numbered storeys with identical plan.
            if (!previous.StoreyNumber.HasValue || !next.StoreyNumber.HasValue)
                return false;

            return next.StoreyNumber.Value == previous.StoreyNumber.Value + 1;
        }

        private static DiscoveredGroup BuildGroup(List<LevelFootprint> run)
        {
            var levels = run.Select(r => r.Level).ToList();
            var numbers = run
                .Where(r => r.StoreyNumber.HasValue)
                .Select(r => r.StoreyNumber.Value)
                .ToList();

            string title;
            string key;
            var isTypical = numbers.Count >= 2
                            && numbers.Count == run.Count
                            && numbers.Max() - numbers.Min() + 1 == numbers.Count;

            if (isTypical)
            {
                var min = numbers.Min();
                var max = numbers.Max();
                title = FormatRangeTitle(min, max);
                key = $"floors{min}to{max}";
            }
            else if (numbers.Count == 1 && run.Count == 1)
            {
                title = FormatSingleTitle(numbers[0]);
                key = $"floor{numbers[0]}";
            }
            else
            {
                var name = levels[0].Name?.Trim() ?? "этаж";
                title = $"Экспликация полов ({name})";
                key = $"level_{GetElementIdValue(levels[0].Id)}";
            }

            return new DiscoveredGroup
            {
                Key = key,
                Title = title,
                Levels = levels,
                IsTypicalRange = isTypical,
                Signature = run[0].Signature
            };
        }

        public static string FormatSingleTitle(int storey)
        {
            return $"Экспликация полов {storey}-го этажа";
        }

        public static string FormatRangeTitle(int from, int to)
        {
            if (from == to)
                return FormatSingleTitle(from);

            return $"Экспликация полов {from}-{to}-го этажа";
        }

        public static int? GetStoreyNumber(string levelName)
        {
            if (string.IsNullOrWhiteSpace(levelName))
                return null;

            var m = NumberedStorey.Match(levelName.Trim());
            if (!m.Success)
                return null;

            var raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : (int?)null;
        }

        /// <summary>
        /// Stable XY+type signature: same type(s), same plan location/size (ignores Z).
        /// </summary>
        private static string BuildSignature(IReadOnlyList<Floor> floors)
        {
            var parts = new List<string>();
            foreach (var floor in floors)
            {
                var doc = floor.Document;
                var floorType = doc?.GetElement(floor.GetTypeId()) as FloorType;
                var typeName = floorType?.Name ?? floor.Name ?? "";

                var bb = floor.get_BoundingBox(null);
                var minX = 0L;
                var minY = 0L;
                var maxX = 0L;
                var maxY = 0L;
                if (bb != null)
                {
                    minX = QuantizeMm(bb.Min.X * 304.8);
                    minY = QuantizeMm(bb.Min.Y * 304.8);
                    maxX = QuantizeMm(bb.Max.X * 304.8);
                    maxY = QuantizeMm(bb.Max.Y * 304.8);
                }

                var areaM2 = 0.0;
                var areaParam = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (areaParam != null && areaParam.HasValue)
                    areaM2 = areaParam.AsDouble() * 0.09290304; // ft² → m²

                var areaQ = Math.Round(areaM2 / AreaQuantumM2) * AreaQuantumM2;
                parts.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1},{2},{3},{4}|{5:0.0}",
                    typeName,
                    minX,
                    minY,
                    maxX,
                    maxY,
                    areaQ));
            }

            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts);
        }

        private static long QuantizeMm(double mm)
        {
            return (long)Math.Round(mm / BboxQuantumMm) * (long)BboxQuantumMm;
        }

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
    }
}
