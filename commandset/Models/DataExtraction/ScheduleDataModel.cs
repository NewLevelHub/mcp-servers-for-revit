using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    public enum ScheduleElementCategory
    {
        Doors,
        Windows,
        Floors,
        CurtainWalls
    }

    /// <summary>
    /// One compound-structure layer for floor finish экспликация (optional).
    /// </summary>
    public class FloorLayerExport
    {
        [JsonProperty("function")]
        public string Function { get; set; } = "";

        [JsonProperty("material")]
        public string Material { get; set; } = "";

        [JsonProperty("thicknessMm")]
        public double ThicknessMm { get; set; }
    }

    /// <summary>
    /// One element instance in a schedule export. Mark is optional and may be empty.
    /// </summary>
    public class ScheduleInstanceExport
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("mark")]
        public string Mark { get; set; } = "";

        [JsonProperty("type")]
        public string Type { get; set; } = "";

        [JsonProperty("familyName")]
        public string FamilyName { get; set; } = "";

        [JsonProperty("size")]
        public string Size { get; set; } = "";

        [JsonProperty("level")]
        public string Level { get; set; } = "";

        [JsonProperty("typeId")]
        public long TypeId { get; set; }

        /// <summary>
        /// Floor finish area in m² (floors / экспликация only).
        /// </summary>
        [JsonProperty("areaM2", NullValueHandling = NullValueHandling.Ignore)]
        public double? AreaM2 { get; set; }

        /// <summary>
        /// Floor type compound layers when available (floors only).
        /// </summary>
        [JsonProperty("layers", NullValueHandling = NullValueHandling.Ignore)]
        public List<FloorLayerExport> Layers { get; set; }
    }

    /// <summary>
    /// One grouped row in a schedule export (by family type, level, and size).
    /// For floors, Size is the summed area string and AreaM2 is numeric.
    /// </summary>
    public class ScheduleGroupRow
    {
        [JsonProperty("mark")]
        public string Mark { get; set; } = "";

        [JsonProperty("type")]
        public string Type { get; set; } = "";

        [JsonProperty("familyName")]
        public string FamilyName { get; set; } = "";

        [JsonProperty("size")]
        public string Size { get; set; } = "";

        [JsonProperty("level")]
        public string Level { get; set; } = "";

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("unmarkedCount")]
        public int UnmarkedCount { get; set; }

        [JsonProperty("typeId")]
        public long TypeId { get; set; }

        [JsonProperty("elementIds")]
        public List<long> ElementIds { get; set; } = new List<long>();

        [JsonProperty("areaM2", NullValueHandling = NullValueHandling.Ignore)]
        public double? AreaM2 { get; set; }

        [JsonProperty("layers", NullValueHandling = NullValueHandling.Ignore)]
        public List<FloorLayerExport> Layers { get; set; }
    }

    public class ScheduleExportResult
    {
        [JsonProperty("category")]
        public string Category { get; set; } = "";

        [JsonProperty("totalCount")]
        public int TotalCount { get; set; }

        [JsonProperty("unmarkedCount")]
        public int UnmarkedCount { get; set; }

        /// <summary>
        /// Sum of finish floor areas in m² (floors / экспликация only).
        /// </summary>
        [JsonProperty("totalAreaM2", NullValueHandling = NullValueHandling.Ignore)]
        public double? TotalAreaM2 { get; set; }

        [JsonProperty("instances")]
        public List<ScheduleInstanceExport> Instances { get; set; } = new List<ScheduleInstanceExport>();

        [JsonProperty("groups")]
        public List<ScheduleGroupRow> Groups { get; set; } = new List<ScheduleGroupRow>();

        [JsonProperty("createdViewSchedule")]
        public bool CreatedViewSchedule { get; set; }

        [JsonProperty("scheduleId", NullValueHandling = NullValueHandling.Ignore)]
        public long? ScheduleId { get; set; }

        [JsonProperty("scheduleUniqueId", NullValueHandling = NullValueHandling.Ignore)]
        public string ScheduleUniqueId { get; set; }

        [JsonProperty("scheduleName", NullValueHandling = NullValueHandling.Ignore)]
        public string ScheduleName { get; set; }

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = "";
    }
}
