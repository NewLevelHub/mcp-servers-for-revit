using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    public enum ScheduleElementCategory
    {
        Doors,
        Windows,
        Floors
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
    }

    /// <summary>
    /// One grouped row in a schedule export (by family type, level, and size).
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
    }

    public class ScheduleExportResult
    {
        [JsonProperty("category")]
        public string Category { get; set; } = "";

        [JsonProperty("totalCount")]
        public int TotalCount { get; set; }

        [JsonProperty("unmarkedCount")]
        public int UnmarkedCount { get; set; }

        [JsonProperty("instances")]
        public List<ScheduleInstanceExport> Instances { get; set; } = new List<ScheduleInstanceExport>();

        [JsonProperty("groups")]
        public List<ScheduleGroupRow> Groups { get; set; } = new List<ScheduleGroupRow>();

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = "";
    }
}
