using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    public class ValidateScheduleTypeAreaRow
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "";

        [JsonProperty("modelAreaM2")]
        public double ModelAreaM2 { get; set; }

        [JsonProperty("scheduleAreaM2")]
        public double ScheduleAreaM2 { get; set; }

        [JsonProperty("diffM2")]
        public double DiffM2 { get; set; }
    }

    public class ValidateScheduleResult
    {
        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("scheduleName")]
        public string ScheduleName { get; set; }

        [JsonProperty("levelName")]
        public string LevelName { get; set; }

        /// <summary>
        /// "elements" for doors/windows; "floor_areas" when Floors are compared by m² (REV-49).
        /// </summary>
        [JsonProperty("mode", NullValueHandling = NullValueHandling.Ignore)]
        public string Mode { get; set; }

        [JsonProperty("modelCount")]
        public int ModelCount { get; set; }

        [JsonProperty("scheduleCount")]
        public int ScheduleCount { get; set; }

        [JsonProperty("diff")]
        public int Diff { get; set; }

        [JsonProperty("missingIds")]
        public List<long> MissingIds { get; set; } = new List<long>();

        [JsonProperty("modelAreaM2", NullValueHandling = NullValueHandling.Ignore)]
        public double? ModelAreaM2 { get; set; }

        [JsonProperty("scheduleAreaM2", NullValueHandling = NullValueHandling.Ignore)]
        public double? ScheduleAreaM2 { get; set; }

        [JsonProperty("areaDiffM2", NullValueHandling = NullValueHandling.Ignore)]
        public double? AreaDiffM2 { get; set; }

        [JsonProperty("typeAreas", NullValueHandling = NullValueHandling.Ignore)]
        public List<ValidateScheduleTypeAreaRow> TypeAreas { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
