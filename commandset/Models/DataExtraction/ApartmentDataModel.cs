using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>
    /// Plain room input for apartment aggregation (no Revit types, unit-testable).
    /// </summary>
    public class ApartmentRoomInput
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Level { get; set; } = "";
        public double AreaM2 { get; set; }

        /// <summary>Apartment number from the grouping parameter; empty = unassigned (МОП etc.).</summary>
        public string ApartmentNumber { get; set; } = "";
    }

    /// <summary>
    /// One room inside an apartment with its area-counting category and coefficient
    /// per СП РК 3.02-101-2012*, приложение А, п. А.8.
    /// </summary>
    public class ApartmentRoomExport
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("level")]
        public string Level { get; set; } = "";

        [JsonProperty("areaM2")]
        public double AreaM2 { get; set; }

        /// <summary>living | auxiliary | summer</summary>
        [JsonProperty("category")]
        public string Category { get; set; } = "";

        /// <summary>loggia | balcony | terrace | veranda | combined (summer rooms only)</summary>
        [JsonProperty("summerKind", NullValueHandling = NullValueHandling.Ignore)]
        public string SummerKind { get; set; }

        /// <summary>Reduction coefficient applied to countedAreaM2 (1.0 for non-summer rooms).</summary>
        [JsonProperty("coefficient")]
        public double Coefficient { get; set; } = 1.0;

        /// <summary>areaM2 × coefficient — contribution to the apartment total area.</summary>
        [JsonProperty("countedAreaM2")]
        public double CountedAreaM2 { get; set; }
    }

    public class ApartmentExport
    {
        [JsonProperty("apartmentNumber")]
        public string ApartmentNumber { get; set; } = "";

        /// <summary>Distinct room levels; multi-level apartments list several, comma-separated.</summary>
        [JsonProperty("level")]
        public string Level { get; set; } = "";

        /// <summary>Apartment type by living room count: Студия, 1К, 2К, 3К…</summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "";

        [JsonProperty("roomCount")]
        public int RoomCount { get; set; }

        [JsonProperty("livingRoomCount")]
        public int LivingRoomCount { get; set; }

        /// <summary>Жилая площадь: сумма жилых комнат, м².</summary>
        [JsonProperty("livingAreaM2")]
        public double LivingAreaM2 { get; set; }

        /// <summary>Площадь подсобных (нежилых) помещений, м².</summary>
        [JsonProperty("auxiliaryAreaM2")]
        public double AuxiliaryAreaM2 { get; set; }

        /// <summary>Полезная площадь: жилые + подсобные, без летних, м².</summary>
        [JsonProperty("usefulAreaM2")]
        public double UsefulAreaM2 { get; set; }

        /// <summary>Фактическая площадь летних помещений (без коэффициентов), м².</summary>
        [JsonProperty("summerAreaM2")]
        public double SummerAreaM2 { get; set; }

        /// <summary>Приведённая площадь летних помещений (с коэффициентами А.8), м².</summary>
        [JsonProperty("summerAreaReducedM2")]
        public double SummerAreaReducedM2 { get; set; }

        /// <summary>Общая площадь квартиры: полезная + приведённая летних, м² (А.8).</summary>
        [JsonProperty("totalAreaM2")]
        public double TotalAreaM2 { get; set; }

        [JsonProperty("rooms", NullValueHandling = NullValueHandling.Ignore)]
        public List<ApartmentRoomExport> Rooms { get; set; }
    }

    public class ApartmentTypeSummary
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "";

        [JsonProperty("apartmentCount")]
        public int ApartmentCount { get; set; }

        /// <summary>Доля типа от общего числа квартир, %.</summary>
        [JsonProperty("sharePercent")]
        public double SharePercent { get; set; }

        [JsonProperty("livingAreaM2")]
        public double LivingAreaM2 { get; set; }

        [JsonProperty("usefulAreaM2")]
        public double UsefulAreaM2 { get; set; }

        [JsonProperty("totalAreaM2")]
        public double TotalAreaM2 { get; set; }

        [JsonProperty("avgTotalAreaM2")]
        public double AvgTotalAreaM2 { get; set; }
    }

    public class ApartmentTotals
    {
        [JsonProperty("livingAreaM2")]
        public double LivingAreaM2 { get; set; }

        [JsonProperty("usefulAreaM2")]
        public double UsefulAreaM2 { get; set; }

        [JsonProperty("summerAreaM2")]
        public double SummerAreaM2 { get; set; }

        [JsonProperty("summerAreaReducedM2")]
        public double SummerAreaReducedM2 { get; set; }

        [JsonProperty("totalAreaM2")]
        public double TotalAreaM2 { get; set; }
    }

    /// <summary>
    /// Norm reference for the reduction coefficients so the chat can cite the source.
    /// </summary>
    public class ApartmentNormInfo
    {
        [JsonProperty("code")]
        public string Code { get; set; } = "";

        [JsonProperty("clause")]
        public string Clause { get; set; } = "";

        [JsonProperty("quote")]
        public string Quote { get; set; } = "";

        [JsonProperty("coefficients")]
        public Dictionary<string, double> Coefficients { get; set; } = new Dictionary<string, double>();
    }

    public class ExportApartmentDataResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = "";

        [JsonProperty("projectName")]
        public string ProjectName { get; set; } = "";

        /// <summary>Room parameter used to group rooms into apartments.</summary>
        [JsonProperty("apartmentNumberParameter")]
        public string ApartmentNumberParameter { get; set; } = "";

        [JsonProperty("norm")]
        public ApartmentNormInfo Norm { get; set; } = new ApartmentNormInfo();

        [JsonProperty("totalApartments")]
        public int TotalApartments { get; set; }

        [JsonProperty("assignedRoomCount")]
        public int AssignedRoomCount { get; set; }

        /// <summary>Placed rooms without an apartment number (МОП, техпомещения…).</summary>
        [JsonProperty("unassignedRoomCount")]
        public int UnassignedRoomCount { get; set; }

        [JsonProperty("unassignedRoomSample")]
        public List<string> UnassignedRoomSample { get; set; } = new List<string>();

        /// <summary>Ведомость квартир.</summary>
        [JsonProperty("apartments")]
        public List<ApartmentExport> Apartments { get; set; } = new List<ApartmentExport>();

        /// <summary>Сводный ТЭП по типам квартир (Студия/1К/2К/3К…).</summary>
        [JsonProperty("byType")]
        public List<ApartmentTypeSummary> ByType { get; set; } = new List<ApartmentTypeSummary>();

        [JsonProperty("totals")]
        public ApartmentTotals Totals { get; set; } = new ApartmentTotals();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        [JsonProperty("executionTimeMs")]
        public long ExecutionTimeMs { get; set; }
    }
}
