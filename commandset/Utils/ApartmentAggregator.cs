using RevitMCPCommandSet.Models.DataExtraction;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Groups plain room inputs into apartments and computes квартирография/ТЭП areas
    /// per СП РК 3.02-101-2012*, приложение А, п. А.8. No Revit types — unit-testable.
    /// </summary>
    public static class ApartmentAggregator
    {
        private const int UnassignedSampleLimit = 10;

        public static ExportApartmentDataResult Aggregate(
            string projectName,
            string parameterName,
            IReadOnlyList<ApartmentRoomInput> rooms,
            bool includeRooms)
        {
            var result = new ExportApartmentDataResult
            {
                ProjectName = projectName ?? string.Empty,
                ApartmentNumberParameter = parameterName ?? string.Empty,
                Norm = BuildNormInfo()
            };

            var assigned = new Dictionary<string, List<ApartmentRoomInput>>();
            var unassigned = new List<ApartmentRoomInput>();

            foreach (var room in rooms ?? (IReadOnlyList<ApartmentRoomInput>)new List<ApartmentRoomInput>())
            {
                var number = room.ApartmentNumber?.Trim() ?? string.Empty;
                if (number.Length == 0)
                {
                    unassigned.Add(room);
                    continue;
                }

                if (!assigned.TryGetValue(number, out var list))
                {
                    list = new List<ApartmentRoomInput>();
                    assigned[number] = list;
                }

                list.Add(room);
            }

            result.AssignedRoomCount = assigned.Values.Sum(list => list.Count);
            result.UnassignedRoomCount = unassigned.Count;
            result.UnassignedRoomSample = unassigned
                .Take(UnassignedSampleLimit)
                .Select(room => $"{room.Name} ({room.Level})".Trim())
                .ToList();

            if (assigned.Count == 0)
            {
                result.Success = false;
                result.Message =
                    $"No rooms carry an apartment number in parameter '{parameterName}'. " +
                    "Fill the apartment-number parameter on rooms or pass apartmentNumberParameter explicitly.";
                return result;
            }

            foreach (var pair in assigned.OrderBy(item => item.Key, ApartmentNumberComparer.Instance))
                result.Apartments.Add(BuildApartment(pair.Key, pair.Value, includeRooms));

            result.TotalApartments = result.Apartments.Count;
            result.ByType = BuildTypeSummaries(result.Apartments);
            result.Totals = new ApartmentTotals
            {
                LivingAreaM2 = Round(result.Apartments.Sum(a => a.LivingAreaM2)),
                UsefulAreaM2 = Round(result.Apartments.Sum(a => a.UsefulAreaM2)),
                SummerAreaM2 = Round(result.Apartments.Sum(a => a.SummerAreaM2)),
                SummerAreaReducedM2 = Round(result.Apartments.Sum(a => a.SummerAreaReducedM2)),
                TotalAreaM2 = Round(result.Apartments.Sum(a => a.TotalAreaM2))
            };

            if (unassigned.Count > 0)
            {
                result.Warnings.Add(
                    $"{unassigned.Count} placed rooms have no apartment number (МОП, техпомещения и т.п.) and are excluded from apartment totals.");
            }

            result.Success = true;
            result.Message =
                $"Grouped {result.AssignedRoomCount} rooms into {result.TotalApartments} apartments " +
                $"({string.Join(", ", result.ByType.Select(t => $"{t.Type}: {t.ApartmentCount}"))}). " +
                $"Areas per {ApartmentRoomClassifier.NormCode}, {ApartmentRoomClassifier.NormClause}.";
            return result;
        }

        private static ApartmentExport BuildApartment(
            string number,
            List<ApartmentRoomInput> rooms,
            bool includeRooms)
        {
            var apartment = new ApartmentExport
            {
                ApartmentNumber = number,
                RoomCount = rooms.Count,
                Level = string.Join(", ", rooms
                    .Select(room => room.Level)
                    .Where(level => !string.IsNullOrWhiteSpace(level))
                    .Distinct()
                    .OrderBy(level => level))
            };

            var roomExports = includeRooms ? new List<ApartmentRoomExport>() : null;

            foreach (var room in rooms)
            {
                var category = ApartmentRoomClassifier.Classify(room.Name, out var summerKind, out var coefficient);
                double counted = room.AreaM2 * coefficient;

                switch (category)
                {
                    case ApartmentRoomCategory.Living:
                        apartment.LivingRoomCount++;
                        apartment.LivingAreaM2 += room.AreaM2;
                        break;
                    case ApartmentRoomCategory.Summer:
                        apartment.SummerAreaM2 += room.AreaM2;
                        apartment.SummerAreaReducedM2 += counted;
                        break;
                    default:
                        apartment.AuxiliaryAreaM2 += room.AreaM2;
                        break;
                }

                roomExports?.Add(new ApartmentRoomExport
                {
                    Id = room.Id,
                    Name = room.Name,
                    Level = room.Level,
                    AreaM2 = Round(room.AreaM2),
                    Category = category.ToString().ToLowerInvariant(),
                    SummerKind = summerKind,
                    Coefficient = coefficient,
                    CountedAreaM2 = Round(counted)
                });
            }

            apartment.LivingAreaM2 = Round(apartment.LivingAreaM2);
            apartment.AuxiliaryAreaM2 = Round(apartment.AuxiliaryAreaM2);
            apartment.UsefulAreaM2 = Round(apartment.LivingAreaM2 + apartment.AuxiliaryAreaM2);
            apartment.SummerAreaM2 = Round(apartment.SummerAreaM2);
            apartment.SummerAreaReducedM2 = Round(apartment.SummerAreaReducedM2);
            apartment.TotalAreaM2 = Round(apartment.UsefulAreaM2 + apartment.SummerAreaReducedM2);
            apartment.Type = ApartmentRoomClassifier.GetApartmentType(apartment.LivingRoomCount);
            apartment.Rooms = roomExports;

            return apartment;
        }

        private static List<ApartmentTypeSummary> BuildTypeSummaries(IReadOnlyList<ApartmentExport> apartments)
        {
            return apartments
                .GroupBy(apartment => apartment.Type)
                .Select(group => new ApartmentTypeSummary
                {
                    Type = group.Key,
                    ApartmentCount = group.Count(),
                    SharePercent = Math.Round(100.0 * group.Count() / apartments.Count, 1, MidpointRounding.AwayFromZero),
                    LivingAreaM2 = Round(group.Sum(a => a.LivingAreaM2)),
                    UsefulAreaM2 = Round(group.Sum(a => a.UsefulAreaM2)),
                    TotalAreaM2 = Round(group.Sum(a => a.TotalAreaM2)),
                    AvgTotalAreaM2 = Round(group.Average(a => a.TotalAreaM2))
                })
                .OrderBy(summary => summary.Type == "Студия" ? 0 : 1)
                .ThenBy(summary => summary.Type, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static ApartmentNormInfo BuildNormInfo()
        {
            return new ApartmentNormInfo
            {
                Code = ApartmentRoomClassifier.NormCode,
                Clause = ApartmentRoomClassifier.NormClause,
                Quote = ApartmentRoomClassifier.NormQuote,
                Coefficients = new Dictionary<string, double>
                {
                    ["balcony"] = ApartmentRoomClassifier.BalconyTerraceCoefficient,
                    ["terrace"] = ApartmentRoomClassifier.BalconyTerraceCoefficient,
                    ["loggia"] = ApartmentRoomClassifier.LoggiaCoefficient,
                    ["veranda"] = ApartmentRoomClassifier.VerandaCoefficient,
                    ["combined"] = ApartmentRoomClassifier.CombinedCoefficient
                }
            };
        }

        private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        /// <summary>Numeric-aware ordering so «10» follows «9», not «1».</summary>
        private sealed class ApartmentNumberComparer : IComparer<string>
        {
            public static readonly ApartmentNumberComparer Instance = new ApartmentNumberComparer();

            public int Compare(string x, string y)
            {
                bool xNumeric = long.TryParse(x, out var xValue);
                bool yNumeric = long.TryParse(y, out var yValue);

                if (xNumeric && yNumeric)
                    return xValue.CompareTo(yValue);
                if (xNumeric)
                    return -1;
                if (yNumeric)
                    return 1;

                return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
